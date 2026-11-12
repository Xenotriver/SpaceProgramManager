using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using H3;
using Sim.Hex;
using Sim.Movement;
using Sim.World;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Tests for the baked region-tile loader (<see cref="BodyWorld"/>).
///
/// Most tests build a tiny synthetic bake in a temp directory so they always run.
/// The <c>RealBake_*</c> tests additionally read <c>data/moon</c> when it exists and
/// are no-ops otherwise (the bake is gitignored).
/// </summary>
public class WorldLoaderTests
{
    private const int FineResolution = 6;
    private const int RegionResolution = 3;

    // Same normal region used by ChildOrderTests; 343 fine hexes.
    private static readonly H3Index NormalRegion =
        HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, 3);

    private static readonly H3Index PentagonRegion = H3Index.GetPentagons(RegionResolution).First();

    [Fact]
    public void Open_ReadsManifestAndBiomeTable()
    {
        using var fixture = new BakedFixture();

        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        Assert.Equal("moon", world.BodyKey);
        Assert.Equal(FineResolution, world.FineResolution);
        Assert.Equal(RegionResolution, world.RegionResolution);
        Assert.Equal(2, world.Regions.Count);
        Assert.Equal(10, world.Biomes.Count);
        Assert.Equal("luna.mare", world.Biomes[0].Id);
        Assert.Equal(0, world.Biomes[0].ElevationRank);
        Assert.True(world.Biomes[0].Buildable);
        Assert.Equal("luna.pole_south_highlands", world.Biomes[9].Id);
        Assert.True(world.Biomes.TryGetById("luna.midlands", out var midlands));
        Assert.Equal(2, midlands.ElevationRank);
    }

    [Fact]
    public void GetRegion_NormalRegion_Has343HexesInH3ChildOrder()
    {
        using var fixture = new BakedFixture();
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        var tile = world.GetRegion((ulong)NormalRegion);

        Assert.Equal(343, tile.HexCount);
        Assert.Equal((ulong)NormalRegion, (ulong)tile.Region);
        Assert.Equal(
            HexGrid.FineHexesInRegion(NormalRegion, FineResolution).Select(cell => (ulong)cell),
            tile.Cells.Select(cell => (ulong)cell));
    }

    [Fact]
    public void GetRegion_PentagonRegion_Has286Hexes()
    {
        using var fixture = new BakedFixture();
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        var tile = world.GetRegion((ulong)PentagonRegion);

        Assert.Equal(286, tile.HexCount);
        Assert.True(tile.Region.IsPentagon);
    }

    [Fact]
    public void RegionTile_ExposesBiomeElevationAndCrater()
    {
        using var fixture = new BakedFixture();
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        var tile = world.GetRegion((ulong)NormalRegion);

        Assert.Equal(0, tile.BiomeCode(0));
        Assert.Equal((short)-9067, tile.ElevationM(0));
        Assert.Equal(CraterKind.None, tile.Crater(0));

        Assert.Equal(1, tile.BiomeCode(1));
        Assert.Equal((short)-9066, tile.ElevationM(1));
        Assert.Equal(CraterKind.SingleCell, tile.Crater(1));

        Assert.Equal(2, tile.BiomeCode(2));
        Assert.Equal(CraterKind.MultiCell, tile.Crater(2));

        Assert.Equal(2, tile.BiomeCode(342));
        Assert.Equal("luna.midlands", tile.Biome(342, world.Biomes).Id);
        Assert.Equal("luna.highlands", tile.Biome(3, world.Biomes).Id);
    }

    [Fact]
    public void GetRegionAt_UsesManifestOrder()
    {
        using var fixture = new BakedFixture();
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        Assert.Equal((ulong)NormalRegion, (ulong)world.GetRegionAt(0).Region);
        Assert.Equal((ulong)PentagonRegion, (ulong)world.GetRegionAt(1).Region);
    }

    [Fact]
    public void TryGetRegion_ReturnsFalseForUnknownRegion()
    {
        using var fixture = new BakedFixture();
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        Assert.False(world.TryGetRegion(12345, out var tile));
        Assert.Null(tile);
        Assert.Throws<KeyNotFoundException>(() => world.GetRegion(12345));
    }

    [Fact]
    public void Open_RejectsTruncatedTileFile()
    {
        using var fixture = new BakedFixture();
        fixture.TruncateTiles(10);

        var error = Assert.Throws<InvalidDataException>(() => BodyWorld.Open(fixture.BodyKey, fixture.Root));
        Assert.Contains("manifest describes", error.Message);
    }

    [Fact]
    public void GetRegion_RejectsCorruptHeaderMagic()
    {
        using var fixture = new BakedFixture();
        fixture.OverwriteTiles(0, "XXXX"u8);
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        var error = Assert.Throws<InvalidDataException>(() => world.GetRegion((ulong)NormalRegion));
        Assert.Contains("magic", error.Message);
    }

    [Fact]
    public void GetRegion_RejectsHexCountMismatch()
    {
        using var fixture = new BakedFixture();
        fixture.OverwriteTiles(12, BitConverter.GetBytes((uint)300));
        using var world = BodyWorld.Open(fixture.BodyKey, fixture.Root);

        var error = Assert.Throws<InvalidDataException>(() => world.GetRegion((ulong)NormalRegion));
        Assert.Contains("tile holds 300", error.Message);
    }

    [Fact]
    public void Manifest_RejectsUnsupportedVersion()
    {
        using var fixture = new BakedFixture();
        fixture.RewriteManifest(version: 99);

        var error = Assert.Throws<InvalidDataException>(() => BodyWorld.Open(fixture.BodyKey, fixture.Root));
        Assert.Contains("unsupported format version", error.Message);
    }

    [Fact]
    public void Manifest_RejectsInconsistentRegionLength()
    {
        using var fixture = new BakedFixture();
        fixture.RewriteManifest(regionLengthDelta: 4);

        var error = Assert.Throws<InvalidDataException>(() => BodyWorld.Open(fixture.BodyKey, fixture.Root));
        Assert.Contains("need", error.Message);
    }

    [Fact]
    public void Manifest_RejectsNonContiguousOffsets()
    {
        using var fixture = new BakedFixture();
        fixture.RewriteManifest(secondRegionOffsetDelta: 8);

        var error = Assert.Throws<InvalidDataException>(() => BodyWorld.Open(fixture.BodyKey, fixture.Root));
        Assert.Contains("contiguous", error.Message);
    }

    [Fact]
    public void FindDataRoot_UsesExplicitRootAndRejectsWrongOne()
    {
        using var fixture = new BakedFixture();

        Assert.Equal(fixture.Root, BodyWorld.FindDataRoot(fixture.Root, fixture.BodyKey));

        var empty = Path.Combine(fixture.Root, "empty");
        Directory.CreateDirectory(empty);
        Assert.Throws<DirectoryNotFoundException>(() => BodyWorld.FindDataRoot(empty, fixture.BodyKey));
    }

    [Fact]
    public void RealBake_Moon_ManifestIsSelfConsistent()
    {
        var root = TryFindRealDataRoot();
        if (root is null)
            return;

        using var world = BodyWorld.Open("moon", root);

        Assert.Equal(41_162, world.Regions.Count);
        Assert.Equal(10, world.Biomes.Count);
        Assert.Equal(6, world.FineResolution);
        Assert.Equal(3, world.RegionResolution);

        var biomeIds = world.Biomes.All.Select(biome => biome.Id).ToList();
        Assert.Equal(10, biomeIds.Distinct().Count());
        Assert.All(world.Biomes.All, biome => Assert.InRange(biome.ElevationRank, 0, 3));

        Assert.Equal(12, world.Regions.Count(region => region.HexCount == 286));
        Assert.All(world.Regions, region =>
        {
            Assert.Contains(region.HexCount, new[] { 286, 343 });
            Assert.InRange(region.DominantBiomeCode, (byte)0, (byte)9);
            Assert.Equal(TileFormat.HeaderBytes + region.HexCount * TileFormat.RecordBytes, region.Length);
        });
    }

    [Fact]
    public void RealBake_Moon_ReadsSampleRegions()
    {
        var root = TryFindRealDataRoot();
        if (root is null)
            return;

        using var world = BodyWorld.Open("moon", root);

        // Fixed sample spread across the tile file: first, last, and every 4096th.
        var ordinals = Enumerable.Range(0, world.Regions.Count).Where(i => i % 4096 == 0).Append(world.Regions.Count - 1);

        foreach (var ordinal in ordinals)
        {
            var tile = world.GetRegionAt(ordinal);

            Assert.Equal(world.Regions[ordinal].HexCount, tile.HexCount);
            Assert.Equal(world.Regions[ordinal].DominantBiomeCode, tile.DominantBiomeCode);

            for (var i = 0; i < tile.HexCount; i++)
            {
                var record = tile[i];
                Assert.InRange(record.BiomeCode, (byte)0, (byte)9);
                Assert.InRange(record.ElevationM, (short)-9067, (short)10698);
                Assert.True(Enum.IsDefined(record.Crater));
            }
        }
    }

    private static string? TryFindRealDataRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "data", "moon", "manifest.json")))
                return Path.Combine(directory.FullName, "data");

            directory = directory.Parent;
        }

        // Local/CI opt-in: fail instead of silently skipping when the bake is required.
        if (Environment.GetEnvironmentVariable("SPM_REQUIRE_DATA") == "1")
            throw new DirectoryNotFoundException("SPM_REQUIRE_DATA=1 but data/moon/manifest.json was not found.");

        return null;
    }

    /// <summary>
    /// Writes a two-region synthetic bake: one normal region (343 hexes) and one
    /// pentagon region (286), with deterministic biome/elevation/crater values.
    /// </summary>
    private sealed class BakedFixture : IDisposable
    {
        public BakedFixture()
        {
            Root = Path.Combine(Path.GetTempPath(), "spm-loader-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, BodyKey));

            var normal = BuildRegion(NormalRegion, i => (byte)(i % 10));
            var pentagon = BuildRegion(PentagonRegion, _ => (byte)3);
            var regions = new[]
            {
                normal with { Offset = 0 },
                pentagon with { Offset = normal.Length },
            };

            Regions = regions;
            WriteTiles(regions);
            WriteManifest(regions);
        }

        public string BodyKey => "moon";

        public string Root { get; }

        private IReadOnlyList<RegionBlock> Regions { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Temp cleanup is best-effort.
            }
        }

        public void TruncateTiles(int bytes)
        {
            var path = Path.Combine(Root, BodyKey, "fine.bin");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Write);
            stream.SetLength(stream.Length - bytes);
        }

        public void OverwriteTiles(long offset, ReadOnlySpan<byte> bytes)
        {
            var path = Path.Combine(Root, BodyKey, "fine.bin");
            using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
            stream.Position = offset;
            stream.Write(bytes);
        }

        public void RewriteManifest(
            int version = 1,
            int regionLengthDelta = 0,
            int secondRegionOffsetDelta = 0)
        {
            var regions = Regions
                .Select((region, i) => new ManifestRegion
                {
                    Index = region.Index.ToString(CultureInfo.InvariantCulture),
                    DominantBiome = 4,
                    HexCount = region.HexCount,
                    Offset = region.Offset + (i == 1 ? secondRegionOffsetDelta : 0),
                    Length = region.Length + (i == 0 ? regionLengthDelta : 0),
                })
                .ToList();

            WriteManifestJson(version, regions);
        }

        private void WriteTiles(IReadOnlyList<RegionBlock> regions)
        {
            using var stream = File.Create(Path.Combine(Root, BodyKey, "fine.bin"));

            foreach (var region in regions)
            {
                var header = new byte[TileFormat.HeaderBytes];
                Encoding.ASCII.GetBytes(TileFormat.Magic, header);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), TileFormat.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), FineResolution);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), RegionResolution);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)region.HexCount);
                stream.Write(header);

                var record = new byte[TileFormat.RecordBytes];
                for (var i = 0; i < region.HexCount; i++)
                {
                    record[0] = region.Biomes[i];
                    BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(1), region.Elevations[i]);
                    record[3] = (byte)region.Craters[i];
                    stream.Write(record);
                }
            }
        }

        private void WriteManifest(IReadOnlyList<RegionBlock> regions)
        {
            var entries = regions
                .Select(region => new ManifestRegion
                {
                    Index = region.Index.ToString(CultureInfo.InvariantCulture),
                    DominantBiome = 4,
                    HexCount = region.HexCount,
                    Offset = region.Offset,
                    Length = region.Length,
                })
                .ToList();

            WriteManifestJson(TileFormat.Version, entries);
        }

        private void WriteManifestJson(int version, List<ManifestRegion> regions)
        {
            var json = JsonSerializer.Serialize(new
            {
                format = "spm-region-tiles",
                version,
                generatedUtc = "2026-11-12T00:00:00+00:00",
                body = new { key = BodyKey, displayName = "Moon", radiusKm = 1737.4 },
                fineResolution = FineResolution,
                regionResolution = RegionResolution,
                recordBytes = TileFormat.RecordBytes,
                tileHeaderBytes = TileFormat.HeaderBytes,
                tilesFile = "fine.bin",
                biomes = TestBiomes(),
                sources = new Dictionary<string, string>(),
                regions,
            });

            File.WriteAllText(Path.Combine(Root, BodyKey, "manifest.json"), json);
        }

        private static object[] TestBiomes() =>
        [
            new { code = 0, id = "luna.mare", displayName = "Mare", elevationRank = 0, buildable = true },
            new { code = 1, id = "luna.lowlands", displayName = "Lowlands", elevationRank = 1, buildable = true },
            new { code = 2, id = "luna.midlands", displayName = "Midlands", elevationRank = 2, buildable = true },
            new { code = 3, id = "luna.highlands", displayName = "Highlands", elevationRank = 3, buildable = true },
            new { code = 4, id = "luna.pole_north_lowlands", displayName = "North Pole Lowlands", elevationRank = 1, buildable = true },
            new { code = 5, id = "luna.pole_north_midlands", displayName = "North Pole Midlands", elevationRank = 2, buildable = true },
            new { code = 6, id = "luna.pole_north_highlands", displayName = "North Pole Highlands", elevationRank = 3, buildable = true },
            new { code = 7, id = "luna.pole_south_lowlands", displayName = "South Pole Lowlands", elevationRank = 1, buildable = true },
            new { code = 8, id = "luna.pole_south_midlands", displayName = "South Pole Midlands", elevationRank = 2, buildable = true },
            new { code = 9, id = "luna.pole_south_highlands", displayName = "South Pole Highlands", elevationRank = 3, buildable = true },
        ];

        private static RegionBlock BuildRegion(H3Index region, Func<int, byte> biome)
        {
            var cells = HexGrid.FineHexesInRegion(region, FineResolution);
            var biomes = new byte[cells.Count];
            var elevations = new short[cells.Count];
            var craters = new CraterKind[cells.Count];

            for (var i = 0; i < cells.Count; i++)
            {
                biomes[i] = biome(i);
                elevations[i] = (short)(-9067 + i);
                craters[i] = (CraterKind)(i % 3);
            }

            return new RegionBlock((ulong)region, biomes, elevations, craters);
        }

        private sealed record RegionBlock(ulong Index, byte[] Biomes, short[] Elevations, CraterKind[] Craters)
        {
            public int HexCount => Biomes.Length;

            public int Length => TileFormat.HeaderBytes + HexCount * TileFormat.RecordBytes;

            public long Offset { get; init; }
        }

        private sealed class ManifestRegion
        {
            public string Index { get; set; } = string.Empty;

            public int DominantBiome { get; set; }

            public int HexCount { get; set; }

            public long Offset { get; set; }

            public int Length { get; set; }
        }
    }
}
