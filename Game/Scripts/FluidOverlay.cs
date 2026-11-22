using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Godot;
using H3;
using H3.Extensions;
using H3.Model;
using Sim.World;

/// <summary>
/// Optional presentation-only fluid sidecar for one baked body.
///
/// <c>fluid.bin</c> is one byte per fine hex in the same H3 child order as
/// <c>fine.bin</c>. The low three bits carry flow direction and the high five
/// bits carry river width. Water presence and class remain authoritative in the
/// biome byte; this overlay only draws river strokes and flat-water tint.
///
/// A missing, mismatched or corrupt sidecar is a warning, never a load failure.
/// </summary>
public sealed class FluidOverlay
{
    /// <summary>One screen-space river segment produced for a view.</summary>
    public readonly record struct FluidStroke(Vector2 From, Vector2 To, float Width, Color Color);

    private static readonly Color RiverColor = new(0.35f, 0.72f, 1.00f, 0.78f);

    private const string MetadataFile = "fluid.json";
    private const string ExpectedFormat = "spm-fluid";
    private const int ExpectedVersion = 1;
    private const int BytesPerHex = 1;
    private const string ExpectedOrder = "fine.bin child order";

    private readonly byte[] _values;
    private readonly int[] _regionStarts;
    private readonly Dictionary<ulong, int> _regionOrdinals;

    private FluidOverlay(
        byte[] values,
        int[] regionStarts,
        Dictionary<ulong, int> regionOrdinals,
        string sourceName,
        bool synthetic)
    {
        _values = values;
        _regionStarts = regionStarts;
        _regionOrdinals = regionOrdinals;
        SourceName = sourceName;
        Synthetic = synthetic;
    }

    /// <summary>Provenance label for the overlay legend.</summary>
    public string SourceName { get; }

    /// <summary>True for deterministic CI fixtures.</summary>
    public bool Synthetic { get; }

    /// <summary>Direction bits, 1..6 for H3 directions K..IJ and 7 for an outlet.</summary>
    public const int DirectionMask = 0x07;

    /// <summary>Width-class shift. Width 0 means no visible flow.</summary>
    public const int WidthShift = 3;

    /// <summary>Loads the sidecar, or returns null with a warning on any problem.</summary>
    public static FluidOverlay? TryLoad(BodyWorld world)
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
            GD.PushWarning($"fluid overlay unavailable: {ex.Message}; using biome water only");
            return null;
        }
    }

    private static FluidOverlay? Load(BodyWorld world)
    {
        var bodyDirectory = Path.Combine(world.DataDirectory, world.BodyKey);
        var metadataPath = Path.Combine(bodyDirectory, MetadataFile);
        if (!File.Exists(metadataPath))
        {
            // Fluid is Earth-only: other bodies need no warning for an optional
            // sidecar they were never expected to ship.
            if (string.Equals(world.BodyKey, KnownBodies.Earth, StringComparison.OrdinalIgnoreCase))
                GD.PushWarning($"no {MetadataFile} for {world.BodyKey}; using biome water only");
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

        var fluidName = RequireString(root, "fluidFile");
        if (fluidName.Length == 0 || fluidName != Path.GetFileName(fluidName))
            throw new InvalidDataException($"{MetadataFile}: fluidFile must be a plain file name.");

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

        var fluidPath = Path.Combine(bodyDirectory, fluidName);
        if (!File.Exists(fluidPath))
            throw new FileNotFoundException($"No fluid data file for {world.BodyKey}.", fluidPath);

        var values = File.ReadAllBytes(fluidPath);
        var expectedBytes = checked(hexCount * BytesPerHex);
        if (values.Length != expectedBytes)
            throw new InvalidDataException(
                $"{fluidName}: {values.Length} bytes; expected {expectedBytes} for {hexCount} hexes.");

        if (root.TryGetProperty("fluidSha256", out var hashElement)
            && hashElement.ValueKind == JsonValueKind.String)
        {
            var expectedHash = hashElement.GetString();
            if (!string.IsNullOrWhiteSpace(expectedHash))
            {
                var actualHash = Convert.ToHexString(SHA256.HashData(values));
                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException($"{fluidName}: SHA-256 does not match {MetadataFile}.");
            }
        }

        var source = root.ValueKind == JsonValueKind.Object
                     && root.TryGetProperty("source", out var sourceElement)
            ? sourceElement
            : default;
        var sourceName = source.ValueKind == JsonValueKind.Object
                         && source.TryGetProperty("name", out var nameElement)
                         && nameElement.ValueKind == JsonValueKind.String
                         && nameElement.GetString() is { Length: > 0 } name
            ? name
            : "fluid overlay";
        var synthetic = source.ValueKind == JsonValueKind.Object
                        && source.TryGetProperty("synthetic", out var syntheticElement)
                        && syntheticElement.ValueKind == JsonValueKind.True;

        return new FluidOverlay(values, starts, ordinals, sourceName, synthetic);
    }

    /// <summary>Copies this region's flow bytes into an ordinal-indexed array.</summary>
    public bool TryCopyRegion(ulong regionIndex, int expectedHexCount, byte[] destination)
    {
        if (destination.Length != expectedHexCount
            || !_regionOrdinals.TryGetValue(regionIndex, out var regionOrdinal))
            return false;

        var start = _regionStarts[regionOrdinal];
        Array.Copy(_values, start, destination, 0, expectedHexCount);
        return true;
    }

    /// <summary>
    /// Builds screen-space river strokes for one loaded region. Strokes whose
    /// downstream neighbour lies in another region are skipped; the neighbouring
    /// region draws its own segment. This keeps the overlay strictly visual.
    /// </summary>
    public IReadOnlyList<FluidStroke> StrokesForRegion(
        ulong regionIndex,
        RegionTile tile,
        IReadOnlyDictionary<ulong, int> centerIndexByCell,
        IReadOnlyList<Vector2> centers,
        Func<Vector2, Vector2> toScreen,
        float cellRadius,
        float scale)
    {
        var values = new byte[tile.HexCount];
        if (!TryCopyRegion(regionIndex, values.Length, values))
            return Array.Empty<FluidStroke>();

        var strokes = new List<FluidStroke>();
        for (var i = 0; i < values.Length; i++)
        {
            var direction = values[i] & DirectionMask;
            var widthClass = values[i] >> WidthShift;
            if (direction is < 1 or > 6 || widthClass == 0
                || !centerIndexByCell.TryGetValue((ulong)tile.Cells[i], out var fromIndex))
                continue;

            var neighbour = tile.Cells[i].GetDirectNeighbour((Direction)direction, 0).Item1;
            if (!centerIndexByCell.TryGetValue((ulong)neighbour, out var downstream)
                || downstream == fromIndex
                || fromIndex >= centers.Count
                || downstream >= centers.Count)
                continue;

            var from = toScreen(centers[fromIndex]);
            var to = toScreen(centers[downstream]);
            var width = Mathf.Max(1f, cellRadius * scale * (0.055f + widthClass * 0.018f));
            strokes.Add(new FluidStroke(from, to, width, RiverColor));
        }

        return strokes;
    }

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
}
