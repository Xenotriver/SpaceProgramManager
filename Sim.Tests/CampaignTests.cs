using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using H3;
using H3.Algorithms;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Hex;
using Sim.Movement;
using Sim.Site;
using Sim.World;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Tests for the campaign domain layer: site-scoped placement, local-cell
/// occupancy, region installation/validation, and campaign metadata.
/// </summary>
public class CampaignTests
{
    private const int FineResolution = 6;
    private const int RegionResolution = 3;

    private static readonly SiteScorerConfig Config = new() { BodyRadiusKm = 1737.4 };

    private static readonly H3Index NormalRegion =
        HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, 3);

    [Fact]
    public void TryPlace_AddsInstallationAndClaimsLocalCell()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var result = campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, LocalGrid.Center);

        Assert.True(result.Success, result.Error);
        Assert.Equal(new[] { LocalGrid.Center }, result.LocalFootprint);
        Assert.Equal(1, campaign.BuildingCount((ulong)fixture.Region));

        var building = Assert.Single(campaign.Buildings((ulong)fixture.Region));
        Assert.Equal("single", building.DefId);
        Assert.Equal((ulong)tile.Cells[fixture.CenterOrdinal], (ulong)building.ParentHex);
        Assert.Equal(LocalGrid.Center, building.LocalCenter);
        Assert.Equal(new[] { LocalGrid.Center }, building.LocalFootprint);
        Assert.NotEqual(Guid.Empty, building.Id);

        var state = campaign.GetRegion((ulong)fixture.Region);
        Assert.True(state.TryGetAt(tile.Cells[fixture.CenterOrdinal], LocalGrid.Center, out var occupant));
        Assert.Same(building, occupant);
    }

    [Fact]
    public void TryPlace_AllowsTwoInstallationsInOneHex()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var first = campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, new LocalCell(0, 0));
        var second = campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, new LocalCell(1, 0));

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal(2, campaign.BuildingCount((ulong)fixture.Region));

        var site = Assert.Single(campaign.Sites((ulong)fixture.Region).Values);
        Assert.Equal((ulong)tile.Cells[fixture.CenterOrdinal], (ulong)site.ParentHex);
        Assert.Equal(2, site.Installations.Count);
        Assert.Equal(2, site.Occupancy.Count);
        Assert.True(site.TryGetAt(new LocalCell(1, 0), out var occupant));
        Assert.Equal("single", occupant!.DefId);
    }

    [Fact]
    public void TryPlace_RejectsOverlapInsideTheSameSite()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        Assert.True(campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
        var second = campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, LocalGrid.Center);

        Assert.False(second.Success);
        Assert.Contains("already occupied", second.Error);
        Assert.Equal(1, campaign.BuildingCount((ulong)fixture.Region));
    }

    [Fact]
    public void TryPlace_DifferentSitesAreIndependent()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var first = campaign.TryPlace(fixture.ClusterDef, tile, fixture.CenterOrdinal, LocalGrid.Center);
        var second = campaign.TryPlace(fixture.ClusterDef, tile, fixture.NeighborOrdinals[0], LocalGrid.Center);

        Assert.True(first.Success, first.Error);
        Assert.True(second.Success, second.Error);
        Assert.Equal(2, campaign.Sites((ulong)fixture.Region).Count);
    }

    [Fact]
    public void Preview_RejectsRadiusOneFootprintCentredInRingThree()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var preview = campaign.Preview(fixture.ClusterDef, tile, fixture.CenterOrdinal, new LocalCell(3, 0));

        Assert.False(preview.Success);
        Assert.Contains("outside the site grid", preview.Error);
        Assert.Equal(7, preview.LocalFootprint.Count);
        Assert.Empty(campaign.Buildings((ulong)fixture.Region));
    }

    [Fact]
    public void Preview_KeepsLocalFootprintWhenPlacementIsRejected()
    {
        using var fixture = new CampaignFixture();
        fixture.Biomes[fixture.CenterOrdinal] = 1; // non-buildable
        fixture.Save();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var preview = campaign.Preview(fixture.SingleDef, tile, fixture.CenterOrdinal, LocalGrid.Center);

        Assert.False(preview.Success);
        Assert.Contains("not buildable", preview.Error);
        Assert.Equal(new[] { LocalGrid.Center }, preview.LocalFootprint);
        Assert.Empty(campaign.Buildings((ulong)fixture.Region));
    }

    [Fact]
    public void TryRemove_RemovesInstallationByLocalCell()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, new LocalCell(0, 0));
        campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, new LocalCell(1, 0));

        var removed = campaign.TryRemove(tile, fixture.CenterOrdinal, new LocalCell(1, 0), out var building);

        Assert.True(removed);
        Assert.NotNull(building);
        Assert.Equal("single", building!.DefId);
        var site = Assert.Single(campaign.Sites((ulong)fixture.Region).Values);
        Assert.Single(site.Installations);
        Assert.False(site.TryGetAt(new LocalCell(1, 0), out _));

        Assert.True(campaign.TryRemove(tile, fixture.CenterOrdinal, LocalGrid.Center, out _));
        Assert.Empty(campaign.Sites((ulong)fixture.Region));
        Assert.Empty(campaign.Buildings((ulong)fixture.Region));
    }

    [Fact]
    public void Clear_RemovesEveryInstallation()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, new LocalCell(0, 0));
        campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, new LocalCell(1, 0));
        campaign.TryPlace(fixture.SingleDef, tile, fixture.NeighborOrdinals[0], LocalGrid.Center);

        var removed = campaign.Clear(tile);

        Assert.Equal(3, removed);
        Assert.Empty(campaign.Buildings((ulong)fixture.Region));
        Assert.Empty(campaign.Sites((ulong)fixture.Region));
    }

    [Fact]
    public void InstallRegion_RebuildsLocalFootprintFromDefinition()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var stored = new RegionLoadResult(
            (ulong)fixture.Region,
            new[] { Stored("cluster", tile.Cells[fixture.CenterOrdinal], LocalGrid.Center) },
            Array.Empty<string>());

        var report = campaign.InstallRegion(stored, tile);

        Assert.Equal(1, report.Loaded);
        Assert.Equal(0, report.Dropped);
        Assert.Empty(report.Warnings);

        var building = Assert.Single(campaign.Buildings((ulong)fixture.Region));
        Assert.Equal(7, building.LocalFootprint.Count);
        Assert.All(building.LocalFootprint, cell => Assert.True(LocalGrid.IsInside(cell)));
        Assert.True(campaign.IsLoaded((ulong)fixture.Region));
    }

    [Fact]
    public void InstallRegion_KeepsTwoInstallationsInOneSite()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        var parentHex = tile.Cells[fixture.CenterOrdinal];

        var stored = new RegionLoadResult(
            (ulong)fixture.Region,
            new[]
            {
                Stored("cluster", parentHex, new LocalCell(0, 0)),
                Stored("single", parentHex, new LocalCell(2, 0)),
            },
            Array.Empty<string>());

        var report = campaign.InstallRegion(stored, tile);

        Assert.Equal(2, report.Loaded);
        Assert.Equal(0, report.Dropped);
        var site = Assert.Single(campaign.Sites((ulong)fixture.Region).Values);
        Assert.Equal(2, site.Installations.Count);
    }

    [Fact]
    public void InstallRegion_DropsUnknownDefinition()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var stored = new RegionLoadResult(
            (ulong)fixture.Region,
            new[] { Stored("does_not_exist", tile.Cells[fixture.CenterOrdinal], LocalGrid.Center) },
            Array.Empty<string>());

        var report = campaign.InstallRegion(stored, tile);

        Assert.Equal(0, report.Loaded);
        Assert.Equal(1, report.Dropped);
        Assert.Contains(report.Warnings, warning => warning.Contains("unknown building definition"));
    }

    [Fact]
    public void InstallRegion_DropsOverlappingRecordsInOneSite()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        var parentHex = tile.Cells[fixture.CenterOrdinal];

        var stored = new RegionLoadResult(
            (ulong)fixture.Region,
            new[]
            {
                Stored("cluster", parentHex, LocalGrid.Center),
                Stored("single", parentHex, LocalGrid.Center),
            },
            Array.Empty<string>());

        var report = campaign.InstallRegion(stored, tile);

        Assert.Equal(1, report.Loaded);
        Assert.Equal(1, report.Dropped);
        Assert.Contains(report.Warnings, warning => warning.Contains("overlaps"));
    }

    [Fact]
    public void InstallRegion_DropsFootprintOutsideTheLocalGrid()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var stored = new RegionLoadResult(
            (ulong)fixture.Region,
            new[] { Stored("cluster", tile.Cells[fixture.CenterOrdinal], new LocalCell(3, 0)) },
            Array.Empty<string>());

        var report = campaign.InstallRegion(stored, tile);

        Assert.Equal(0, report.Loaded);
        Assert.Contains(report.Warnings, warning => warning.Contains("outside the site grid"));
    }

    [Fact]
    public void InstallRegion_DropsParentHexOutsideTheRegion()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        var outside = new H3Index(1);

        var stored = new RegionLoadResult(
            (ulong)fixture.Region,
            new[] { Stored("single", outside, LocalGrid.Center) },
            Array.Empty<string>());

        var report = campaign.InstallRegion(stored, tile);

        Assert.Equal(0, report.Loaded);
        Assert.Contains(report.Warnings, warning => warning.Contains("not in region"));
    }

    [Fact]
    public void MarkVisited_TracksRegionsOnce()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var campaign = fixture.CreateCampaign(world);
        var before = campaign.Meta.UpdatedUtc;

        campaign.MarkVisited((ulong)fixture.Region);
        var afterFirst = campaign.Meta.UpdatedUtc;
        campaign.MarkVisited((ulong)fixture.Region);

        Assert.Single(campaign.Meta.VisitedRegions);
        Assert.True(afterFirst >= before);
        Assert.Equal(afterFirst, campaign.Meta.UpdatedUtc);
    }

    [Fact]
    public void RegionChanged_FiresOnMutationOnly()
    {
        using var fixture = new CampaignFixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);

        var changes = 0;
        campaign.RegionChanged += _ => changes++;

        campaign.TryPlace(fixture.SingleDef, tile, fixture.CenterOrdinal, LocalGrid.Center);
        campaign.TryRemove(tile, fixture.CenterOrdinal, LocalGrid.Center, out _);
        campaign.Clear(tile);

        Assert.Equal(2, changes);
    }

    private static BuildingInstance Stored(string defId, H3Index parentHex, LocalCell localCenter) => new()
    {
        DefId = defId,
        ParentHex = parentHex,
        LocalCenter = localCenter,
        LocalFootprint = Array.Empty<LocalCell>(),
    };

    private static BuildingDef MakeDef(string id, int footprintRadius = 0)
        => new()
        {
            Id = id,
            DisplayName = id,
            Category = BuildingCategory.Special,
            FootprintRadius = footprintRadius,
            MinSiteScore = 0.0,
            MaxSlopeM = 500.0,
            AllowSingleCellCrater = true,
            AllowMultiCellCrater = false,
            Cost = new Dictionary<string, double> { { "credits", 1.0 } },
            ThroughputPerHour = new Dictionary<string, double>(),
        };

    /// <summary>
    /// One-region synthetic bake (flat, buildable, crater-free) plus the building
    /// catalogue used by these tests.
    /// </summary>
    private sealed class CampaignFixture : IDisposable
    {
        public CampaignFixture()
        {
            Region = NormalRegion;
            Cells = HexGrid.FineHexesInRegion(Region, FineResolution);
            Biomes = new byte[Cells.Count];
            Elevations = new short[Cells.Count];
            Craters = new CraterKind[Cells.Count];

            (CenterOrdinal, NeighborOrdinals) = FindInteriorCenter(Cells);

            SingleDef = MakeDef("single");
            ClusterDef = MakeDef("cluster", footprintRadius: 1);
            Registry = new BuildingRegistry(new[] { SingleDef, ClusterDef });

            Root = Path.Combine(Path.GetTempPath(), "spm-campaign-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, BodyKey));

            Save();
        }

        public string BodyKey => "test";

        public string Root { get; }

        public H3Index Region { get; }

        public IReadOnlyList<H3Index> Cells { get; }

        public byte[] Biomes { get; }

        public short[] Elevations { get; }

        public CraterKind[] Craters { get; }

        public int CenterOrdinal { get; }

        public IReadOnlyList<int> NeighborOrdinals { get; }

        public BuildingDef SingleDef { get; }

        public BuildingDef ClusterDef { get; }

        public BuildingRegistry Registry { get; }

        public BodyWorld Open() => BodyWorld.Open(BodyKey, Root);

        public CampaignState CreateCampaign(BodyWorld world) => new(world, Registry, Config);

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
                body = new { key = BodyKey, displayName = "Test Body", radiusKm = 1737.4 },
                fineResolution = FineResolution,
                regionResolution = RegionResolution,
                recordBytes = TileFormat.RecordBytes,
                tileHeaderBytes = TileFormat.HeaderBytes,
                tilesFile = "fine.bin",
                biomes = new object[]
                {
                    new { code = 0, id = "test.buildable", displayName = "Buildable", elevationRank = 1, buildable = true },
                    new { code = 1, id = "test.nonbuildable", displayName = "NonBuildable", elevationRank = 2, buildable = false },
                },
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
