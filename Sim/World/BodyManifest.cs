using System.Globalization;
using System.Text.Json;
using Sim.Biomes;

namespace Sim.World;

/// <summary>Body metadata stored in <c>manifest.json</c>.</summary>
public sealed record BodyInfo(string Key, string DisplayName, double RadiusKm);

/// <summary>
/// One region (coarse H3 parent) in <c>fine.bin</c>: where its tile lives and what
/// its dominant biome is.
/// </summary>
public sealed record RegionEntry(
    ulong Index,
    byte DominantBiomeCode,
    int HexCount,
    long Offset,
    int Length);

/// <summary>
/// Parsed <c>data/&lt;body&gt;/manifest.json</c> (format v1).
///
/// The manifest is the single source of truth for the per-body biome table
/// (code → <see cref="Biome"/>), so the runtime never hardcodes a body's biomes.
/// </summary>
public sealed class BodyManifest
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public required string Format { get; init; }

    public required int Version { get; init; }

    public required string GeneratedUtc { get; init; }

    public required BodyInfo Body { get; init; }

    public required int FineResolution { get; init; }

    public required int RegionResolution { get; init; }

    public required int RecordBytes { get; init; }

    public required int TileHeaderBytes { get; init; }

    public required string TilesFile { get; init; }

    /// <summary>Biomes ordered by their tile code: index == biome code.</summary>
    public required IReadOnlyList<Biome> Biomes { get; init; }

    /// <summary>Regions in tile-file order.</summary>
    public required IReadOnlyList<RegionEntry> Regions { get; init; }

    public required IReadOnlyDictionary<string, string> Sources { get; init; }

    /// <summary>Reads and validates a manifest from disk.</summary>
    public static BodyManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        using var stream = File.OpenRead(manifestPath);
        return Load(stream, manifestPath);
    }

    /// <summary>Reads and validates a manifest from a stream.</summary>
    public static BodyManifest Load(Stream stream, string sourceName = "manifest")
    {
        ArgumentNullException.ThrowIfNull(stream);

        ManifestJson? json;
        try
        {
            json = JsonSerializer.Deserialize<ManifestJson>(stream, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{sourceName}: invalid JSON: {ex.Message}", ex);
        }

        if (json is null)
            throw new InvalidDataException($"{sourceName}: empty manifest.");

        return FromJson(json, sourceName);
    }

    private static BodyManifest FromJson(ManifestJson json, string source)
    {
        if (json.Format != FormatName)
            throw new InvalidDataException(
                $"{source}: expected format '{FormatName}', got '{json.Format}'.");

        if (json.Version != TileFormat.Version)
            throw new InvalidDataException(
                $"{source}: unsupported format version {json.Version} (runtime supports {TileFormat.Version}).");

        if (json.Body is null || string.IsNullOrWhiteSpace(json.Body.Key))
            throw new InvalidDataException($"{source}: missing body metadata.");

        if (json.RecordBytes != TileFormat.RecordBytes)
            throw new InvalidDataException(
                $"{source}: record size {json.RecordBytes} B is unsupported (expected {TileFormat.RecordBytes} B).");

        if (json.TileHeaderBytes != TileFormat.HeaderBytes)
            throw new InvalidDataException(
                $"{source}: tile header size {json.TileHeaderBytes} B is unsupported (expected {TileFormat.HeaderBytes} B).");

        if (json.FineResolution <= json.RegionResolution || json.FineResolution > 15)
            throw new InvalidDataException(
                $"{source}: fine resolution {json.FineResolution} must be greater than region resolution {json.RegionResolution}.");

        if (string.IsNullOrWhiteSpace(json.TilesFile))
            throw new InvalidDataException($"{source}: missing tiles file name.");

        var biomes = ParseBiomes(json.Biomes, source);
        var regions = ParseRegions(json.Regions, source, json.TileHeaderBytes, json.RecordBytes);

        return new BodyManifest
        {
            Format = json.Format,
            Version = json.Version,
            GeneratedUtc = json.GeneratedUtc ?? string.Empty,
            Body = new BodyInfo(json.Body.Key, json.Body.DisplayName ?? json.Body.Key, json.Body.RadiusKm),
            FineResolution = json.FineResolution,
            RegionResolution = json.RegionResolution,
            RecordBytes = json.RecordBytes,
            TileHeaderBytes = json.TileHeaderBytes,
            TilesFile = json.TilesFile,
            Biomes = biomes,
            Regions = regions,
            Sources = json.Sources ?? new Dictionary<string, string>(),
        };
    }

    private static IReadOnlyList<Biome> ParseBiomes(List<BiomeJson>? json, string source)
    {
        if (json is null || json.Count == 0)
            throw new InvalidDataException($"{source}: biome table is empty.");

        var biomes = new Biome?[json.Count];
        foreach (var entry in json)
        {
            if (entry.Code < 0 || entry.Code >= json.Count)
                throw new InvalidDataException(
                    $"{source}: biome code {entry.Code} is outside 0..{json.Count - 1}.");

            if (biomes[entry.Code] is not null)
                throw new InvalidDataException($"{source}: duplicate biome code {entry.Code}.");

            if (string.IsNullOrWhiteSpace(entry.Id))
                throw new InvalidDataException($"{source}: biome {entry.Code} has no id.");

            biomes[entry.Code] = new Biome
            {
                Id = entry.Id,
                DisplayName = string.IsNullOrWhiteSpace(entry.DisplayName) ? entry.Id : entry.DisplayName,
                ElevationRank = entry.ElevationRank,
                Buildable = entry.Buildable,
            };
        }

        var result = new Biome[biomes.Length];
        for (var code = 0; code < biomes.Length; code++)
            result[code] = biomes[code] ?? throw new InvalidDataException($"{source}: biome code {code} is missing.");

        return result;
    }

    private static IReadOnlyList<RegionEntry> ParseRegions(
        List<RegionJson>? json,
        string source,
        int headerBytes,
        int recordBytes)
    {
        if (json is null || json.Count == 0)
            throw new InvalidDataException($"{source}: region table is empty.");

        var regions = new List<RegionEntry>(json.Count);
        var seen = new HashSet<ulong>(json.Count);

        foreach (var entry in json)
        {
            var index = ParseRegionIndex(entry.Index, source);

            if (!seen.Add(index))
                throw new InvalidDataException($"{source}: duplicate region {index}.");

            if (entry.HexCount <= 0)
                throw new InvalidDataException($"{source}: region {index} has {entry.HexCount} hexes.");

            if (entry.Offset < 0)
                throw new InvalidDataException($"{source}: region {index} has a negative offset.");

            var expectedLength = headerBytes + entry.HexCount * recordBytes;
            if (entry.Length != expectedLength)
                throw new InvalidDataException(
                    $"{source}: region {index} claims {entry.Length} B but {entry.HexCount} hexes need {expectedLength} B.");

            if (entry.DominantBiome < 0 || entry.DominantBiome > byte.MaxValue)
                throw new InvalidDataException($"{source}: region {index} has an invalid dominant biome code.");

            regions.Add(new RegionEntry(
                index,
                (byte)entry.DominantBiome,
                entry.HexCount,
                entry.Offset,
                entry.Length));
        }

        return regions;
    }

    private static ulong ParseRegionIndex(JsonElement element, string source)
    {
        if (element.ValueKind == JsonValueKind.String)
        {
            var text = element.GetString();
            if (ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed))
                return parsed;
        }
        else if (element.ValueKind == JsonValueKind.Number && element.TryGetUInt64(out var number))
        {
            return number;
        }

        throw new InvalidDataException($"{source}: invalid region index '{element}'.");
    }

    private const string FormatName = "spm-region-tiles";

    private sealed class ManifestJson
    {
        public string? Format { get; set; }

        public int Version { get; set; }

        public string? GeneratedUtc { get; set; }

        public BodyJson? Body { get; set; }

        public int FineResolution { get; set; }

        public int RegionResolution { get; set; }

        public int RecordBytes { get; set; }

        public int TileHeaderBytes { get; set; }

        public string? TilesFile { get; set; }

        public List<BiomeJson>? Biomes { get; set; }

        public List<RegionJson>? Regions { get; set; }

        public Dictionary<string, string>? Sources { get; set; }
    }

    private sealed class BodyJson
    {
        public string? Key { get; set; }

        public string? DisplayName { get; set; }

        public double RadiusKm { get; set; }
    }

    private sealed class BiomeJson
    {
        public int Code { get; set; }

        public string? Id { get; set; }

        public string? DisplayName { get; set; }

        public int ElevationRank { get; set; }

        public bool Buildable { get; set; } = true;
    }

    private sealed class RegionJson
    {
        public JsonElement Index { get; set; }

        public int DominantBiome { get; set; }

        public int HexCount { get; set; }

        public long Offset { get; set; }

        public int Length { get; set; }
    }
}
