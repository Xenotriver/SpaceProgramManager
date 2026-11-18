using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using Sim.World;

/// <summary>
/// Optional presentation-only surface colour sidecar for one baked body.
///
/// <c>color.bin</c> is a flat RGB triplet array in the same H3 child order as
/// <c>fine.bin</c>; <c>imagery.json</c> records provenance and validation data.
/// Neither file is authoritative: a missing, mismatched or corrupt sidecar is
/// reported as a warning and the caller falls back to biome colours.
/// </summary>
public sealed class SurfaceImagery
{
    private const string MetadataFile = "imagery.json";
    private const string ExpectedFormat = "spm-imagery";
    private const int ExpectedVersion = 1;
    private const int BytesPerHex = 3;
    private const string ExpectedOrder = "fine.bin child order";

    private readonly byte[] _rgb;
    private readonly int[] _regionStarts;
    private readonly Dictionary<ulong, int> _regionOrdinals;
    private readonly Color[] _regionMeans;

    private SurfaceImagery(
        byte[] rgb,
        int[] regionStarts,
        Dictionary<ulong, int> regionOrdinals,
        Color[] regionMeans,
        string sourceName,
        string sourceCredit,
        string sourceLicense,
        bool synthetic)
    {
        _rgb = rgb;
        _regionStarts = regionStarts;
        _regionOrdinals = regionOrdinals;
        _regionMeans = regionMeans;
        SourceName = sourceName;
        SourceCredit = sourceCredit;
        SourceLicense = sourceLicense;
        Synthetic = synthetic;
    }

    /// <summary>Human-readable imagery product used for this body.</summary>
    public string SourceName { get; }

    /// <summary>Short attribution shown in overlay legends.</summary>
    public string SourceCredit { get; }

    /// <summary>Licence or rights statement from the provenance file.</summary>
    public string SourceLicense { get; }

    /// <summary>True for deterministic CI fixtures, false for real imagery.</summary>
    public bool Synthetic { get; }

    /// <summary>Precomputed mean colour for every region, in manifest order.</summary>
    public IReadOnlyList<Color> RegionMeans => _regionMeans;

    /// <summary>Loads the sidecar, or returns null with a warning on any problem.</summary>
    public static SurfaceImagery? TryLoad(BodyWorld world)
    {
        try
        {
            return Load(world);
        }
        catch (Exception ex) when (ex is IOException
                                      or UnauthorizedAccessException
                                      or InvalidDataException
                                      or InvalidOperationException
                                      or JsonException
                                      or FormatException
                                      or OverflowException
                                      or ArgumentException
                                      or NotSupportedException
                                      or CryptographicException
                                      or KeyNotFoundException)
        {
            GD.PushWarning($"surface imagery unavailable: {ex.Message}; using biome colours");
            return null;
        }
    }

    private static SurfaceImagery? Load(BodyWorld world)
    {
        var bodyDirectory = Path.Combine(world.DataDirectory, world.BodyKey);
        var metadataPath = Path.Combine(bodyDirectory, MetadataFile);
        if (!File.Exists(metadataPath))
        {
            // A missing optional sidecar is expected on older bakes. Keep the
            // warning concise so automation can spot it without a stack trace.
            GD.PushWarning($"no {MetadataFile} for {world.BodyKey}; using biome colours");
            return null;
        }

        using var document = JsonDocument.Parse(File.ReadAllBytes(metadataPath));
        var root = document.RootElement;

        RequireString(root, "format", ExpectedFormat);
        RequireInt(root, "version", ExpectedVersion);
        RequireString(root, "body.key", world.BodyKey, ignoreCase: true);
        RequireInt(root, "fineResolution", world.FineResolution);
        RequireInt(root, "regionResolution", world.RegionResolution);
        RequireString(root, "order", ExpectedOrder);
        RequireInt(root, "bytesPerHex", BytesPerHex);

        var hexCount = RequireInt(root, "hexCount");
        if (hexCount <= 0)
            throw new InvalidDataException($"{MetadataFile}: hexCount must be positive.");

        var colorName = RequireString(root, "colorFile");
        if (colorName.Length == 0 || colorName != Path.GetFileName(colorName))
            throw new InvalidDataException($"{MetadataFile}: colorFile must be a plain file name.");

        var regions = world.Regions;
        var starts = new int[regions.Count];
        var ordinals = new Dictionary<ulong, int>(regions.Count);
        var cursor = 0;
        for (var i = 0; i < regions.Count; i++)
        {
            starts[i] = cursor;
            ordinals.Add(regions[i].Index, i);
            cursor += regions[i].HexCount;
        }

        if (cursor != hexCount)
            throw new InvalidDataException(
                $"{MetadataFile}: hexCount {hexCount} does not match the manifest's {cursor} fine hexes.");

        var colorPath = Path.Combine(bodyDirectory, colorName);
        if (!File.Exists(colorPath))
            throw new FileNotFoundException($"No surface colour file for {world.BodyKey}.", colorPath);

        var rgb = File.ReadAllBytes(colorPath);
        var expectedBytes = checked(hexCount * BytesPerHex);
        if (rgb.Length != expectedBytes)
            throw new InvalidDataException(
                $"{colorName}: {rgb.Length} bytes; expected {expectedBytes} for {hexCount} hexes.");

        if (root.TryGetProperty("colorSha256", out var hashElement)
            && hashElement.ValueKind == JsonValueKind.String)
        {
            var expectedHash = hashElement.GetString();
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                var actualHash = Convert.ToHexString(SHA256.HashData(rgb));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{colorName}: SHA-256 does not match {MetadataFile}.");
            }
        }

        var source = GetObject(root, "source");
        var sourceName = OptionalString(source, "name", "surface imagery");
        var sourceCredit = OptionalString(source, "credits", "see imagery.json");
        var sourceLicense = OptionalString(source, "license", "see imagery.json");
        var synthetic = source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("synthetic", out var syntheticElement)
                        && syntheticElement.ValueKind == JsonValueKind.True;

        var means = new Color[regions.Count];
        for (var r = 0; r < regions.Count; r++)
        {
            var end = r + 1 < regions.Count ? starts[r + 1] : rgb.Length / BytesPerHex;
            long red = 0;
            long green = 0;
            long blue = 0;
            for (var hex = starts[r]; hex < end; hex++)
            {
                var offset = hex * BytesPerHex;
                red += rgb[offset];
                green += rgb[offset + 1];
                blue += rgb[offset + 2];
            }

            var count = Math.Max(1, end - starts[r]);
            means[r] = new Color(
                red / (255f * count),
                green / (255f * count),
                blue / (255f * count),
                1f);
        }

        return new SurfaceImagery(
            rgb, starts, ordinals, means, sourceName, sourceCredit, sourceLicense, synthetic);
    }

    /// <summary>Copies this region's fine-hex colours into an ordinal-indexed array.</summary>
    public bool TryCopyRegion(ulong regionIndex, int expectedHexCount, Color[] destination)
    {
        if (destination.Length != expectedHexCount
            || !_regionOrdinals.TryGetValue(regionIndex, out var regionOrdinal))
            return false;

        var start = _regionStarts[regionOrdinal];
        for (var i = 0; i < expectedHexCount; i++)
        {
            var offset = (start + i) * BytesPerHex;
            destination[i] = new Color(
                _rgb[offset] / 255f,
                _rgb[offset + 1] / 255f,
                _rgb[offset + 2] / 255f,
                1f);
        }

        return true;
    }

    /// <summary>Mean colour of one region, used by the globe's single instance per region.</summary>
    public Color MeanFor(ulong regionIndex)
        => _regionOrdinals.TryGetValue(regionIndex, out var ordinal)
            ? _regionMeans[ordinal]
            : Colors.Gray;

    private static JsonElement GetObject(JsonElement parent, string path)
    {
        var parts = path.Split('.');
        var current = parent;
        foreach (var part in parts)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(part, out current))
            {
                throw new InvalidDataException($"{MetadataFile}: missing '{path}'.");
            }
        }

        return current;
    }

    private static string RequireString(
        JsonElement root,
        string path,
        string? expected = null,
        bool ignoreCase = false)
    {
        var value = GetObject(root, path).GetString();
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidDataException($"{MetadataFile}: '{path}' is missing or empty.");

        if (expected is not null
            && !string.Equals(value, expected,
                ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
        {
            throw new InvalidDataException($"{MetadataFile}: '{path}' is '{value}', expected '{expected}'.");
        }

        return value;
    }

    private static int RequireInt(JsonElement root, string path, int? expected = null)
    {
        var element = GetObject(root, path);
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out var value))
            throw new InvalidDataException($"{MetadataFile}: '{path}' is not an integer.");

        if (expected is not null && value != expected.Value)
            throw new InvalidDataException($"{MetadataFile}: '{path}' is {value}, expected {expected.Value}.");

        return value;
    }

    private static string OptionalString(JsonElement source, string name, string fallback)
        => source.ValueKind == JsonValueKind.Object
           && source.TryGetProperty(name, out var element)
           && element.ValueKind == JsonValueKind.String
           && element.GetString() is { Length: > 0 } value
             ? value
             : fallback;
}
