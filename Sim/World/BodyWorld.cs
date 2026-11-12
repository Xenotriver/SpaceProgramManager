using System.Diagnostics.CodeAnalysis;

namespace Sim.World;

/// <summary>
/// Engine-free view of one baked body: the manifest, its biome table and random
/// access to region tiles.
///
/// Region tiles are read on demand from the single concatenated <c>fine.bin</c>;
/// nothing is loaded until <see cref="GetRegion"/> asks for it, so a body with
/// hundreds of thousands of regions costs only the manifest in memory.
/// </summary>
public sealed class BodyWorld : IDisposable
{
    /// <summary>Optional environment variable holding the baked-data root.</summary>
    public const string DataEnvironmentVariable = "SPM_DATA";

    private const int MaxSearchDepth = 10;

    private readonly FileStream _tiles;
    private readonly Dictionary<ulong, RegionEntry> _byIndex;

    private BodyWorld(BodyManifest manifest, string dataDirectory, FileStream tiles)
    {
        Manifest = manifest;
        DataDirectory = dataDirectory;
        _tiles = tiles;
        _byIndex = new Dictionary<ulong, RegionEntry>(manifest.Regions.Count);

        foreach (var region in manifest.Regions)
            _byIndex.Add(region.Index, region);

        Biomes = new BiomeRegistry(manifest.Biomes);
    }

    public BodyManifest Manifest { get; }

    /// <summary>Per-body biome table (code → biome).</summary>
    public BiomeRegistry Biomes { get; }

    /// <summary>Regions in tile-file order.</summary>
    public IReadOnlyList<RegionEntry> Regions => Manifest.Regions;

    public string BodyKey => Manifest.Body.Key;

    public int FineResolution => Manifest.FineResolution;

    public int RegionResolution => Manifest.RegionResolution;

    /// <summary>Directory that held the body folder (i.e. <c>.../data</c>).</summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Opens a body's baked data.  <paramref name="dataRoot"/> defaults to
    /// <see cref="FindDataRoot"/>, which checks <c>SPM_DATA</c> and then walks up
    /// from the app and working directories looking for <c>&lt;root&gt;/&lt;body&gt;/manifest.json</c>.
    /// </summary>
    public static BodyWorld Open(string bodyKey, string? dataRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyKey);

        var root = FindDataRoot(dataRoot, bodyKey);
        var bodyDirectory = Path.Combine(root, bodyKey);
        var manifest = BodyManifest.Load(Path.Combine(bodyDirectory, "manifest.json"));

        if (!string.Equals(manifest.Body.Key, bodyKey, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Manifest in '{bodyDirectory}' describes body '{manifest.Body.Key}', not '{bodyKey}'.");

        var tilesPath = Path.Combine(bodyDirectory, manifest.TilesFile);
        if (!File.Exists(tilesPath))
            throw new FileNotFoundException($"Tile file for body '{bodyKey}' not found.", tilesPath);

        var tiles = new FileStream(
            tilesPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 1,
            FileOptions.RandomAccess);

        try
        {
            ValidateTileFile(tiles, manifest);
            return new BodyWorld(manifest, root, tiles);
        }
        catch
        {
            tiles.Dispose();
            throw;
        }
    }

    /// <summary>Returns the data root that contains <c>&lt;bodyKey&gt;/manifest.json</c>.</summary>
    public static string FindDataRoot(string? dataRoot = null, string bodyKey = "moon")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyKey);

        if (!string.IsNullOrWhiteSpace(dataRoot))
        {
            var explicitRoot = Path.GetFullPath(dataRoot);
            if (HasBody(explicitRoot, bodyKey))
                return explicitRoot;

            throw new DirectoryNotFoundException(
                $"No '{bodyKey}/manifest.json' under '{explicitRoot}'.");
        }

        var fromEnvironment = Environment.GetEnvironmentVariable(DataEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            var root = Path.GetFullPath(fromEnvironment);
            if (HasBody(root, bodyKey))
                return root;
        }

        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            foreach (var candidate in Ancestors(start))
            {
                if (HasBody(candidate, bodyKey))
                    return candidate;
            }
        }

        throw new DirectoryNotFoundException(
            $"Could not find '{bodyKey}/manifest.json'. Set {DataEnvironmentVariable} or pass a data root.");
    }

    /// <summary>Looks up a region's manifest entry.</summary>
    public bool TryGetRegionEntry(ulong regionIndex, [NotNullWhen(true)] out RegionEntry? entry)
        => _byIndex.TryGetValue(regionIndex, out entry);

    /// <summary>Loads a region tile by its H3 index.</summary>
    public RegionTile GetRegion(ulong regionIndex)
    {
        if (!_byIndex.TryGetValue(regionIndex, out var entry))
            throw new KeyNotFoundException($"Region {regionIndex} is not in body '{BodyKey}'.");

        return Load(entry);
    }

    /// <summary>Loads a region tile by its position in the manifest (tile-file order).</summary>
    public RegionTile GetRegionAt(int ordinal)
    {
        if ((uint)ordinal >= (uint)Manifest.Regions.Count)
            throw new ArgumentOutOfRangeException(
                nameof(ordinal), ordinal, $"Body has {Manifest.Regions.Count} regions.");

        return Load(Manifest.Regions[ordinal]);
    }

    /// <summary>Loads a region tile by its H3 index, or returns false if the body has no such region.</summary>
    public bool TryGetRegion(ulong regionIndex, [NotNullWhen(true)] out RegionTile? tile)
    {
        if (_byIndex.TryGetValue(regionIndex, out var entry))
        {
            tile = Load(entry);
            return true;
        }

        tile = null;
        return false;
    }

    public void Dispose() => _tiles.Dispose();

    private RegionTile Load(RegionEntry entry)
    {
        var buffer = new byte[entry.Length];
        var read = 0;

        while (read < buffer.Length)
        {
            var chunk = RandomAccess.Read(_tiles.SafeFileHandle, buffer.AsSpan(read), entry.Offset + read);
            if (chunk <= 0)
                throw new InvalidDataException(
                    $"Region {entry.Index}: tile file ended after {read} of {buffer.Length} B.");

            read += chunk;
        }

        return new RegionTile(entry, buffer, FineResolution, RegionResolution);
    }

    private static void ValidateTileFile(FileStream tiles, BodyManifest manifest)
    {
        long expectedOffset = 0;

        foreach (var region in manifest.Regions)
        {
            if (region.Offset != expectedOffset)
                throw new InvalidDataException(
                    $"Region {region.Index}: offset {region.Offset}, expected {expectedOffset} (tiles must be contiguous).");

            expectedOffset += region.Length;
        }

        if (tiles.Length != expectedOffset)
            throw new InvalidDataException(
                $"Tile file is {tiles.Length} B, manifest describes {expectedOffset} B.");
    }

    private static bool HasBody(string directory, string bodyKey)
        => File.Exists(Path.Combine(directory, bodyKey, "manifest.json"));

    private static IEnumerable<string> Ancestors(string start)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(start));

        for (var depth = 0; depth < MaxSearchDepth && directory is not null; depth++)
        {
            yield return directory.FullName;
            directory = directory.Parent;
        }
    }
}
