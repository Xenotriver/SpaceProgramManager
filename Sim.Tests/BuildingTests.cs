using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using H3;
using H3.Algorithms;
using Sim.Buildings;
using Sim.Hex;
using Sim.Movement;
using Sim.Site;
using Sim.World;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Tests for building definitions, the registry and placement validation.
///
/// Most tests build a one-region synthetic bake in a temp directory (all flat,
/// buildable, crater-free by default) and mutate its records to exercise each
/// validation rule.
/// </summary>
public class BuildingTests
{
    private const int FineResolution = 6;
    private const int RegionResolution = 3;

    private static readonly SiteScorerConfig Config = new() { BodyRadiusKm = 1737.4 };

    private static readonly (string Id, string DisplayName, int ElevationRank, bool Buildable)[] MarsFixtureBiomes =
    {
        ("mars.lowlands", "Northern Lowlands", 0, true),
        ("mars.midlands", "Midlands", 1, true),
        ("mars.highlands", "Southern Highlands", 2, true),
        ("mars.pole_north", "North Polar Region", 1, true),
        ("mars.pole_south", "South Polar Region", 2, true),
        ("mars.volcanic", "Volcanic Province", 3, true),
        ("mars.canyon", "Canyon System", 1, true),
    };

    private static readonly (string Id, string DisplayName, int ElevationRank, bool Buildable)[] TestFixtureBiomes =
    {
        ("test.buildable", "Buildable", 1, true),
        ("test.nonbuildable", "NonBuildable", 2, false),
    };

    // Same normal region used by WorldLoaderTests; 343 fine hexes.
    private static readonly H3Index NormalRegion =
        HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, 3);

    [Fact]
    public void Registry_RejectsDuplicateIds()
    {
        var definitions = new[] { MakeDef("dup"), MakeDef("dup") };

        var error = Assert.Throws<ArgumentException>(() => new BuildingRegistry(definitions));
        Assert.Contains("Duplicate building id", error.Message);
    }

    [Fact]
    public void Registry_GroupsByCategoryAndSortsById()
    {
        var registry = new BuildingRegistry(new[]
        {
            MakeDef("zeta", BuildingCategory.Power),
            MakeDef("alpha", BuildingCategory.Power),
            MakeDef("beta", BuildingCategory.Habitat),
        });

        Assert.Equal(new[] { "alpha", "zeta" }, registry.GetByCategory(BuildingCategory.Power).Select(d => d.Id));
        Assert.Equal(new[] { "beta" }, registry.GetByCategory(BuildingCategory.Habitat).Select(d => d.Id));
        Assert.Empty(registry.GetByCategory(BuildingCategory.Science));

        Assert.True(registry.TryGet("ALPHA", out var alpha));
        Assert.Equal("alpha", alpha!.Id);
    }

    [Fact]
    public void Placement_SucceedsOnFlatBuildableTerrain()
    {
        using var fixture = new BuildingFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var result = MakeDef("test").CanPlaceAt(tile, fixture.CenterOrdinal, world, Config);

        Assert.True(result.Success, result.Error);
        Assert.Single(result.Footprint);
        Assert.Equal((ulong)tile.Cells[fixture.CenterOrdinal], (ulong)result.Footprint[0]);
        Assert.Empty(result.Warnings);
    }

    [Fact]
    public void Placement_FailsOnNonBuildableBiome()
    {
        using var fixture = new BuildingFixture();
        fixture.Biomes[fixture.CenterOrdinal] = 1; // test.nonbuildable
        fixture.Save();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var result = MakeDef("test").CanPlaceAt(tile, fixture.CenterOrdinal, world, Config);

        Assert.False(result.Success);
        Assert.Contains("not buildable", result.Error);
    }

    [Fact]
    public void Placement_FailsOnExcessSlope()
    {
        using var fixture = new BuildingFixture();
        fixture.Elevations[fixture.NeighborOrdinals[0]] = 500;
        fixture.Save();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var result = MakeDef("slope", footprintRadius: 1, maxSlopeM: 100)
            .CanPlaceAt(tile, fixture.CenterOrdinal, world, Config);

        Assert.False(result.Success);
        Assert.Contains("slope", result.Error);
    }

    [Fact]
    public void Placement_FailsOnMultiCellCrater()
    {
        using var fixture = new BuildingFixture();
        fixture.Craters[fixture.NeighborOrdinals[0]] = CraterKind.MultiCell;
        fixture.Save();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var result = MakeDef("crater", footprintRadius: 1)
            .CanPlaceAt(tile, fixture.CenterOrdinal, world, Config);

        Assert.False(result.Success);
        Assert.Contains("Multi-cell crater", result.Error);
    }

    [Fact]
    public void Placement_FailsWhenFootprintMustBeCraterFree()
    {
        using var fixture = new BuildingFixture();
        fixture.Craters[fixture.CenterOrdinal] = CraterKind.SingleCell;
        fixture.Save();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var result = MakeDef("clean", requireCraterFree: true)
            .CanPlaceAt(tile, fixture.CenterOrdinal, world, Config);

        Assert.False(result.Success);
        Assert.Contains("crater-free", result.Error);
    }

    [Fact]
    public void Placement_FailsOnOccupiedHex()
    {
        using var fixture = new BuildingFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var existing = new BuildingInstance
        {
            DefId = "existing",
            ParentHex = tile.Cells[fixture.CenterOrdinal],
            LocalCenter = LocalGrid.Center,
            LocalFootprint = new[] { LocalGrid.Center },
        };
        var occupancy = new Dictionary<ulong, BuildingInstance>
        {
            [(ulong)tile.Cells[fixture.CenterOrdinal]] = existing,
        };

        var result = MakeDef("new").CanPlaceAt(tile, fixture.CenterOrdinal, world, Config, occupancy);

        Assert.False(result.Success);
        Assert.Contains("already occupied", result.Error);
    }

    [Fact]
    public void Placement_FailsBelowMinimumSiteScore()
    {
        using var fixture = new BuildingFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        // Flat buildable terrain scores ~0.94, so 0.99 cannot be met.
        var result = MakeDef("picky", minSiteScore: 0.99)
            .CanPlaceAt(tile, fixture.CenterOrdinal, world, Config);

        Assert.False(result.Success);
        Assert.Contains("below minimum", result.Error);
    }

    [Fact]
    public void RadiusOneFootprint_IsCenterPlusSixNeighbors()
    {
        using var fixture = new BuildingFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var footprint = MakeDef("large", footprintRadius: 1).BuildFootprint(tile, fixture.CenterOrdinal);

        Assert.Equal(7, footprint.Count);
        Assert.Contains((ulong)tile.Cells[fixture.CenterOrdinal], footprint.Select(hex => (ulong)hex));
    }

    [Fact]
    public void Placement_FailsWhenFootprintCrossesRegionBoundary()
    {
        using var fixture = new BuildingFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var boundary = FindBoundaryOrdinal(tile);
        var result = MakeDef("large", footprintRadius: 1).CanPlaceAt(tile, boundary, world, Config);

        Assert.False(result.Success);
        Assert.Contains("inside this region", result.Error);
    }

    [Fact]
    public void PlacementResult_ImplicitlyConvertsToBool()
    {
        PlacementResult ok = PlacementResult.Ok();
        PlacementResult fail = PlacementResult.Fail("nope");

        Assert.True(ok);
        Assert.False(fail);
        Assert.Equal("nope", fail.Error);
        Assert.Empty(fail.Footprint);
    }

    [Fact]
    public void GetAvailableAtSite_RespectsTechUnlocks()
    {
        using var fixture = new BuildingFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var registry = new BuildingRegistry(new[]
        {
            MakeDef("plain"),
            MakeDef("tech", requiredTech: "tech.x"),
        });

        var locked = registry
            .GetAvailableAtSite(tile, fixture.CenterOrdinal, world, Config, unlockedTechs: new HashSet<string>())
            .Select(def => def.Id)
            .ToList();

        Assert.Equal(new[] { "plain" }, locked);

        var unlocked = registry
            .GetAvailableAtSite(
                tile,
                fixture.CenterOrdinal,
                world,
                Config,
                unlockedTechs: new HashSet<string> { "tech.x" })
            .Select(def => def.Id)
            .OrderBy(id => id)
            .ToList();

        Assert.Equal(new[] { "plain", "tech" }, unlocked);
    }

    [Fact]
    public void MoonBuildings_AreConsistent()
    {
        Assert.Equal(17, MoonBuildings.All.Count);

        var ids = MoonBuildings.All.Select(def => def.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());

        var registry = new BuildingRegistry(MoonBuildings.All);
        Assert.Equal(MoonBuildings.All.Count, registry.All.Count);

        foreach (var category in Enum.GetValues<BuildingCategory>())
        {
            var expected = MoonBuildings.All
                .Where(def => def.Category == category)
                .OrderBy(def => def.Id, StringComparer.Ordinal)
                .Select(def => def.Id);

            Assert.Equal(expected, registry.GetByCategory(category).Select(def => def.Id));
        }

        Assert.All(MoonBuildings.All, def =>
        {
            Assert.False(string.IsNullOrWhiteSpace(def.DisplayName));
            Assert.Contains(def.FootprintRadius, new[] { 0, 1 });
            Assert.NotEmpty(def.Cost);
            Assert.NotNull(def.ThroughputPerHour);
            Assert.InRange(def.MinSiteScore, 0.0, 1.0);
            Assert.True(def.MaxSlopeM > 0);
        });
    }

    [Fact]
    public void MoonBuildings_AllowedBiomeIdsExistInRealBake()
    {
        var root = TryFindRealDataRoot();
        if (root is null)
            return;

        using var world = BodyWorld.Open("moon", root);
        var known = world.Biomes.All.Select(biome => biome.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var def in MoonBuildings.All)
        {
            foreach (var biomeId in def.AllowedBiomeIds.Concat(def.ForbiddenBiomeIds))
                Assert.Contains(biomeId, known);
        }
    }

    [Fact]
    public void BuildingCatalogue_ForMoon_UsesCanonicalDefinitions()
    {
        var defs = BuildingCatalogue.For("MOON");

        Assert.Same(MoonBuildings.All, defs);
        Assert.Equal(MoonBuildings.All.Select(def => def.Id), defs.Select(def => def.Id));
    }

    [Fact]
    public void BuildingCatalogue_ForMars_TranslatesBiomeRestrictions()
    {
        var defs = BuildingCatalogue.For("mars");

        Assert.Equal(MoonBuildings.All.Count, defs.Count);
        Assert.Equal(MoonBuildings.All.Select(def => def.Id), defs.Select(def => def.Id));

        var restricted = defs.Where(def => def.AllowedBiomeIds.Count > 0 || def.ForbiddenBiomeIds.Count > 0)
            .ToDictionary(def => def.Id, StringComparer.Ordinal);

        Assert.Equal(
            new[] { "ice_extractor", "solar_array_large", "solar_array_small", "telescope_array" },
            restricted.Keys.OrderBy(id => id, StringComparer.Ordinal));

        Assert.Equal(
            new[] { "mars.lowlands", "mars.midlands", "mars.highlands" },
            restricted["solar_array_small"].AllowedBiomeIds);
        Assert.Equal(new[] { "mars.lowlands" }, restricted["solar_array_large"].AllowedBiomeIds);
        Assert.Equal(
            new[] { "mars.pole_north", "mars.pole_south" },
            restricted["ice_extractor"].AllowedBiomeIds);
        Assert.Equal(
            new[] { "mars.highlands", "mars.pole_north", "mars.pole_south" },
            restricted["telescope_array"].AllowedBiomeIds);
        Assert.Equal(new[] { "mars.lowlands" }, restricted["telescope_array"].ForbiddenBiomeIds);

        foreach (var def in defs)
        {
            var biomeIds = def.AllowedBiomeIds.Concat(def.ForbiddenBiomeIds).ToList();
            Assert.DoesNotContain(biomeIds, biomeId => biomeId.StartsWith("luna.", StringComparison.Ordinal));
            Assert.All(biomeIds, biomeId => Assert.StartsWith("mars.", biomeId));
        }
    }

    [Fact]
    public void BuildingCatalogue_UnknownBody_Throws()
    {
        var error = Assert.Throws<ArgumentException>(() => BuildingCatalogue.For("venus"));
        Assert.Contains("No building catalogue", error.Message);
    }

    [Fact]
    public void MarsBuildingRestrictions_ApplyAtPlacement()
    {
        using var fixture = new BuildingFixture("mars", MarsFixtureBiomes, 3389.5);
        var config = new SiteScorerConfig { BodyRadiusKm = 3389.5 };
        var defs = BuildingCatalogue.For("mars").ToDictionary(def => def.Id, StringComparer.Ordinal);

        // Code 0 = mars.lowlands; code 3 = mars.pole_north.
        using (var world = fixture.Open())
        {
            var tile = world.GetRegion((ulong)fixture.Region);
            Assert.True(defs["solar_array_small"].CanPlaceAt(tile, fixture.CenterOrdinal, world, config).Success);
            Assert.Contains(
                "not in allowed list",
                defs["ice_extractor"].CanPlaceAt(tile, fixture.CenterOrdinal, world, config).Error);
        }

        fixture.Biomes[fixture.CenterOrdinal] = 3;
        fixture.Save();

        using (var polarWorld = fixture.Open())
        {
            var polarTile = polarWorld.GetRegion((ulong)fixture.Region);
            Assert.True(defs["ice_extractor"].CanPlaceAt(polarTile, fixture.CenterOrdinal, polarWorld, config).Success);
            Assert.Contains(
                "not in allowed list",
                defs["solar_array_small"].CanPlaceAt(polarTile, fixture.CenterOrdinal, polarWorld, config).Error);
        }
    }

    [Fact]
    public void MarsBuildings_AllowedBiomeIdsExistInRealBake()
    {
        var root = TryFindRealDataRoot("mars");
        if (root is null)
            return;

        using var world = BodyWorld.Open("mars", root);
        var known = world.Biomes.All.Select(biome => biome.Id).ToHashSet(StringComparer.Ordinal);

        foreach (var def in BuildingCatalogue.For("mars"))
        {
            foreach (var biomeId in def.AllowedBiomeIds.Concat(def.ForbiddenBiomeIds))
                Assert.Contains(biomeId, known);
        }
    }

    private static BuildingDef MakeDef(
        string id,
        BuildingCategory category = BuildingCategory.Special,
        int footprintRadius = 0,
        double minSiteScore = 0.0,
        string requiredTech = "",
        double maxSlopeM = 500.0,
        bool allowSingleCellCrater = true,
        bool allowMultiCellCrater = false,
        bool requireCraterFree = false)
        => new()
        {
            Id = id,
            DisplayName = id,
            Category = category,
            FootprintRadius = footprintRadius,
            MinSiteScore = minSiteScore,
            RequiredTech = requiredTech,
            MaxSlopeM = maxSlopeM,
            AllowSingleCellCrater = allowSingleCellCrater,
            AllowMultiCellCrater = allowMultiCellCrater,
            RequireCraterFree = requireCraterFree,
            Cost = new Dictionary<string, double> { { "credits", 1.0 } },
            ThroughputPerHour = new Dictionary<string, double>(),
        };

    private static int FindBoundaryOrdinal(RegionTile tile)
    {
        var cells = new HashSet<ulong>(tile.Cells.Select(cell => (ulong)cell));

        for (var i = 0; i < tile.HexCount; i++)
        {
            var ring = tile.Cells[i]
                .GridDiskDistances(1)
                .Where(cell => cell.Distance == 1);

            if (ring.Any(cell => !cells.Contains((ulong)cell.Index)))
                return i;
        }

        throw new InvalidOperationException("Test region has no boundary cell.");
    }

    private static string? TryFindRealDataRoot(string bodyKey = "moon")
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "data", bodyKey, "manifest.json")))
                return Path.Combine(directory.FullName, "data");

            directory = directory.Parent;
        }

        return null;
    }

    /// <summary>
    /// Writes a one-region synthetic bake: all flat, buildable and crater-free by
    /// default. Tests mutate the arrays and call <see cref="Save"/> before opening.
    /// </summary>
    private sealed class BuildingFixture : IDisposable
    {
        public BuildingFixture(
            string bodyKey = "test",
            IReadOnlyList<(string Id, string DisplayName, int ElevationRank, bool Buildable)>? biomes = null,
            double radiusKm = 1737.4)
        {
            BodyKey = bodyKey;
            BiomeDefinitions = biomes ?? TestFixtureBiomes;
            RadiusKm = radiusKm;

            Region = NormalRegion;
            Cells = HexGrid.FineHexesInRegion(Region, FineResolution);
            Biomes = new byte[Cells.Count];
            Elevations = new short[Cells.Count];
            Craters = new CraterKind[Cells.Count];

            (CenterOrdinal, NeighborOrdinals) = FindInteriorCenter(Cells);

            Root = Path.Combine(Path.GetTempPath(), "spm-building-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, BodyKey));

            Save();
        }

        public string BodyKey { get; }

        public double RadiusKm { get; }

        public IReadOnlyList<(string Id, string DisplayName, int ElevationRank, bool Buildable)> BiomeDefinitions { get; }

        public string Root { get; }

        public H3Index Region { get; }

        public IReadOnlyList<H3Index> Cells { get; }

        public byte[] Biomes { get; }

        public short[] Elevations { get; }

        public CraterKind[] Craters { get; }

        /// <summary>Ordinal of a cell whose full 6-neighbour ring lies inside the region.</summary>
        public int CenterOrdinal { get; }

        public IReadOnlyList<int> NeighborOrdinals { get; }

        public BodyWorld Open() => BodyWorld.Open(BodyKey, Root);

        public void Save()
        {
            var length = TileFormat.HeaderBytes + Cells.Count * TileFormat.RecordBytes;

            using (var stream = File.Create(Path.Combine(Root, BodyKey, "fine.bin")))
            {
                var header = new byte[TileFormat.HeaderBytes];
                Encoding.ASCII.GetBytes(TileFormat.Magic, header);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), TileFormat.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), FineResolution);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), RegionResolution);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)Cells.Count);
                stream.Write(header);

                var record = new byte[TileFormat.RecordBytes];
                for (var i = 0; i < Cells.Count; i++)
                {
                    record[0] = Biomes[i];
                    BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(1), Elevations[i]);
                    record[3] = (byte)Craters[i];
                    stream.Write(record);
                }
            }

            var manifest = JsonSerializer.Serialize(new
            {
                format = "spm-region-tiles",
                version = TileFormat.Version,
                generatedUtc = "2026-01-01T00:00:00+00:00",
                body = new { key = BodyKey, displayName = BodyKey, radiusKm = RadiusKm },
                fineResolution = FineResolution,
                regionResolution = RegionResolution,
                recordBytes = TileFormat.RecordBytes,
                tileHeaderBytes = TileFormat.HeaderBytes,
                tilesFile = "fine.bin",
                biomes = BiomeDefinitions
                    .Select((biome, code) => new
                    {
                        code,
                        id = biome.Id,
                        displayName = biome.DisplayName,
                        elevationRank = biome.ElevationRank,
                        buildable = biome.Buildable,
                    })
                    .ToArray(),
                sources = new Dictionary<string, string>(),
                regions = new object[]
                {
                    new
                    {
                        index = ((ulong)Region).ToString(CultureInfo.InvariantCulture),
                        dominantBiome = 0,
                        hexCount = Cells.Count,
                        offset = 0L,
                        length,
                    },
                },
            });

            File.WriteAllText(Path.Combine(Root, BodyKey, "manifest.json"), manifest);
        }

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

        private static (int Center, IReadOnlyList<int> Neighbors) FindInteriorCenter(IReadOnlyList<H3Index> cells)
        {
            var ordinalByHex = new Dictionary<ulong, int>(cells.Count);
            for (var i = 0; i < cells.Count; i++)
                ordinalByHex[(ulong)cells[i]] = i;

            for (var i = 0; i < cells.Count; i++)
            {
                var ring = cells[i]
                    .GridDiskDistances(1)
                    .Where(cell => cell.Distance == 1)
                    .Select(cell => cell.Index)
                    .ToList();

                if (ring.Count != 6)
                    continue;

                var ordinals = new List<int>(6);
                var complete = true;

                foreach (var hex in ring)
                {
                    if (!ordinalByHex.TryGetValue((ulong)hex, out var ordinal))
                    {
                        complete = false;
                        break;
                    }

                    ordinals.Add(ordinal);
                }

                if (complete)
                    return (i, ordinals);
            }

            throw new InvalidOperationException("Test region has no interior cell with a full 6-neighbour ring.");
        }
    }
}
