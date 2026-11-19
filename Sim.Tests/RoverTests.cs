using System.Buffers.Binary;
using System.Diagnostics;
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
/// M5 C2 rover rules: cross-region routing, garage purchases and travel progress.
/// A two-region synthetic bake keeps these tests independent of real data.
/// </summary>
public class RoverTests
{
    [Fact]
    public void RegionalRoute_CrossesAdjacentRegionsWithMovementCosts()
    {
        using var fixture = new RoverFixture();
        using var world = fixture.Open();

        var plan = RegionalRoutePlanner.Plan(
            world,
            (ulong)fixture.FirstRegion,
            (ulong)fixture.FirstCells[fixture.FirstCenter],
            (ulong)fixture.SecondRegion,
            (ulong)fixture.SecondCells[fixture.SecondCenter],
            CampaignDefaults.DefaultRoverSpeedKmh);

        Assert.NotNull(plan);
        Assert.True(plan.LegCount > 0);
        Assert.True(plan.RegionCrossings >= 1);
        Assert.Equal((ulong)fixture.FirstRegion, plan.StartRegion);
        Assert.Equal((ulong)fixture.SecondRegion, plan.GoalRegion);
        Assert.True(plan.TotalDistanceKm > 0.0);
        Assert.True(plan.TotalHours > 0.0);
        Assert.All(plan.Legs, leg => Assert.True(leg.DistanceKm > 0.0 && leg.Hours > 0.0));
    }

    [Fact]
    public void RegionalRoute_RealBake_AcrossBodyCompletes()
    {
        var root = TryFindRealDataRoot("moon");
        if (root is null)
            return;

        using var world = BodyWorld.Open("moon", root);
        var from = world.Regions[0];
        var to = world.Regions[world.Regions.Count / 2];
        var fromTile = world.GetRegion(from.Index);
        var toTile = world.GetRegion(to.Index);

        var stopwatch = Stopwatch.StartNew();
        var plan = RegionalRoutePlanner.Plan(
            world,
            from.Index,
            (ulong)fromTile.Cells[0],
            to.Index,
            (ulong)toTile.Cells[0],
            CampaignDefaults.DefaultRoverSpeedKmh);
        stopwatch.Stop();

        Assert.NotNull(plan);
        Assert.True(plan.RegionCrossings > 0);
        Assert.True(stopwatch.ElapsedMilliseconds < 10_000, $"route took {stopwatch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void RegionalRoute_SameRegionReusesLocalPlanner()
    {
        using var fixture = new RoverFixture();
        using var world = fixture.Open();

        var plan = RegionalRoutePlanner.Plan(
            world,
            (ulong)fixture.FirstRegion,
            (ulong)fixture.FirstCells[0],
            (ulong)fixture.FirstRegion,
            (ulong)fixture.FirstCells[^1],
            CampaignDefaults.DefaultRoverSpeedKmh);

        Assert.NotNull(plan);
        Assert.Equal(0, plan.RegionCrossings);
        Assert.All(plan.Legs, leg => Assert.Equal((ulong)fixture.FirstRegion, leg.FromRegion));
    }

    [Fact]
    public void RoverPurchase_NeedsOperationalGarageAndStopsAtCapacity()
    {
        using var fixture = new RoverFixture();
        using var world = fixture.Open();
        var campaign = fixture.CreateCampaign(world);
        var tile = world.GetRegion((ulong)fixture.FirstRegion);

        var early = campaign.TryPurchaseRover((ulong)fixture.FirstRegion, out var rover);
        Assert.Equal(RoverActionStatus.NoGarage, early.Status);
        Assert.Null(rover);

        Assert.True(campaign.TryPlace(
            fixture.Garage, tile, fixture.FirstCenter, LocalGrid.Center).Success);
        Assert.Equal(RoverActionStatus.NoGarage, campaign.TryPurchaseRover(
            (ulong)fixture.FirstRegion, out _).Status);

        Assert.True(campaign.Tick(fixture.Garage.BuildTimeHours + 1.0).Changed);
        var garages = campaign.Buildings((ulong)fixture.FirstRegion);
        Assert.NotEmpty(garages);
        Assert.Contains(
            garages,
            building => building.DefId == RoverCatalogue.GarageDefId
                        && building.Status is BuildingStatus.Operational or BuildingStatus.Idle);

        for (var i = 0; i < 4; i++)
        {
            var purchase = campaign.TryPurchaseRover((ulong)fixture.FirstRegion, out var bought);
            Assert.Equal(RoverActionStatus.Purchased, purchase.Status);
            Assert.NotNull(bought);
            Assert.Equal(RoverStatus.Docked, bought!.Status);
            Assert.Equal((ulong)fixture.FirstRegion, bought.LocationRegion);
            Assert.Equal((ulong)fixture.FirstCells[fixture.FirstCenter], bought.LocationHex);
        }

        var full = campaign.TryPurchaseRover((ulong)fixture.FirstRegion, out var overflow);
        Assert.Equal(RoverActionStatus.NoCapacity, full.Status);
        Assert.Null(overflow);
        Assert.Equal(4, campaign.Rovers.Count);
    }

    [Fact]
    public void RoverDispatch_CompletesCrossRegionTravelOnTick()
    {
        using var fixture = new RoverFixture();
        using var world = fixture.Open();
        var campaign = fixture.PurchasedCampaign(world);
        var rover = campaign.Rovers.Single();
        var destinationRegion = (ulong)fixture.SecondRegion;
        var destinationHex = (ulong)fixture.SecondCells[fixture.SecondCenter];

        var dispatch = campaign.DispatchRover(rover.Id, destinationRegion, destinationHex);

        Assert.Equal(RoverActionStatus.Dispatched, dispatch.Status);
        Assert.Equal(RoverStatus.Enroute, rover.Status);
        Assert.Equal(destinationRegion, rover.DestinationRegion);
        Assert.Equal(destinationHex, rover.DestinationHex);
        Assert.NotNull(rover.ArrivalGameHour);
        Assert.NotNull(campaign.RouteForRover(rover.Id));

        var report = campaign.Tick(rover.ArrivalGameHour!.Value - campaign.Meta.GameTimeHours + 0.01);

        Assert.Equal(1, report.Arrived);
        Assert.Equal(RoverStatus.Docked, rover.Status);
        Assert.Equal(destinationRegion, rover.LocationRegion);
        Assert.Equal(destinationHex, rover.LocationHex);
        Assert.Null(rover.DestinationRegion);
        Assert.Null(rover.ArrivalGameHour);
        Assert.Null(campaign.RouteForRover(rover.Id));
    }

    [Fact]
    public void RoverState_EnrouteFieldsRoundTripThroughSave()
    {
        using var fixture = new RoverFixture();
        using var world = fixture.Open();
        var campaign = fixture.PurchasedCampaign(world);
        var rover = campaign.Rovers.Single();

        var destinationRegion = (ulong)fixture.SecondRegion;
        var destinationHex = (ulong)fixture.SecondCells[fixture.SecondCenter];
        Assert.True(campaign.DispatchRover(rover.Id, destinationRegion, destinationHex).Success);

        var store = new CampaignStore(Path.Combine(fixture.Root, "save"));
        var fingerprint = BakeFingerprint.From(world.Manifest);
        store.SaveMeta(campaign.Meta, fingerprint);
        var loaded = store.LoadMeta(fingerprint);

        var saved = Assert.Single(loaded.Rovers);
        Assert.Equal(rover.Id, saved.Id);
        Assert.Equal(RoverStatus.Enroute, saved.Status);
        Assert.Equal(rover.LocationRegion, saved.LocationRegion);
        Assert.Equal(rover.LocationHex, saved.LocationHex);
        Assert.Equal(destinationRegion, saved.DestinationRegion);
        Assert.Equal(destinationHex, saved.DestinationHex);
        Assert.Equal(rover.DepartureGameHour, saved.DepartureGameHour);
        Assert.Equal(rover.ArrivalGameHour, saved.ArrivalGameHour);
    }

    [Fact]
    public void RoverDispatch_RejectsUnknownBusyAndUnreachableTargets()
    {
        using var fixture = new RoverFixture();
        using var world = fixture.Open();
        var campaign = fixture.PurchasedCampaign(world);
        var rover = campaign.Rovers.Single();

        Assert.Equal(
            RoverActionStatus.UnknownRover,
            campaign.DispatchRover("missing", (ulong)fixture.SecondRegion, 1).Status);

        Assert.Equal(
            RoverActionStatus.Unreachable,
            campaign.DispatchRover(rover.Id, 999UL, 1).Status);

        var dispatch = campaign.DispatchRover(
            rover.Id,
            (ulong)fixture.SecondRegion,
            (ulong)fixture.SecondCells[fixture.SecondCenter]);
        Assert.True(dispatch.Success);

        Assert.Equal(
            RoverActionStatus.NotAvailable,
            campaign.DispatchRover(
                rover.Id,
                (ulong)fixture.FirstRegion,
                (ulong)fixture.FirstCells[0]).Status);
    }

    private static string? TryFindRealDataRoot(string bodyKey)
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

    private sealed class RoverFixture : IDisposable
    {
        private const int FineResolution = 6;
        private const int RegionResolution = 3;
        private const string BodyKey = "rover-test";

        public RoverFixture()
        {
            FirstRegion = HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, RegionResolution);
            SecondRegion = HexGrid.Neighbors(FirstRegion)
                .First(candidate => HexGrid.FineHexesInRegion(candidate, FineResolution).Count == 343);

            FirstCells = HexGrid.FineHexesInRegion(FirstRegion, FineResolution);
            SecondCells = HexGrid.FineHexesInRegion(SecondRegion, FineResolution);
            FirstCenter = FindCenter(FirstCells);
            SecondCenter = FindCenter(SecondCells);

            Garage = BuildingCatalogue.For("moon").Single(def => def.Id == RoverCatalogue.GarageDefId);

            Root = Path.Combine(Path.GetTempPath(), "spm-rover-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, BodyKey));
            Save();
        }

        public H3Index FirstRegion { get; }

        public H3Index SecondRegion { get; }

        public IReadOnlyList<H3Index> FirstCells { get; }

        public IReadOnlyList<H3Index> SecondCells { get; }

        public int FirstCenter { get; }

        public int SecondCenter { get; }

        public BuildingDef Garage { get; }

        public string Root { get; }

        public BodyWorld Open() => BodyWorld.Open(BodyKey, Root);

        public CampaignState CreateCampaign(BodyWorld world)
            => new(world, new BuildingRegistry(new[] { Garage }));

        public CampaignState PurchasedCampaign(BodyWorld world)
        {
            var campaign = CreateCampaign(world);
            var tile = world.GetRegion((ulong)FirstRegion);
            Assert.True(campaign.TryPlace(Garage, tile, FirstCenter, LocalGrid.Center).Success);
            campaign.Tick(Garage.BuildTimeHours + 1.0);
            Assert.Equal(
                RoverActionStatus.Purchased,
                campaign.TryPurchaseRover((ulong)FirstRegion, out _).Status);
            return campaign;
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

        private static int FindCenter(IReadOnlyList<H3Index> cells)
        {
            var ordinals = new Dictionary<ulong, int>();
            for (var i = 0; i < cells.Count; i++)
                ordinals[(ulong)cells[i]] = i;

            return Enumerable.Range(0, cells.Count).First(ordinal =>
                cells[ordinal].GridDiskDistances(1)
                    .Where(cell => cell.Distance == 1)
                    .All(cell => ordinals.ContainsKey((ulong)cell.Index)));
        }

        private void Save()
        {
            var regions = new[] { FirstRegion, SecondRegion };
            var lengths = new List<long>();
            long offset = 0;

            using (var stream = File.Create(Path.Combine(Root, BodyKey, "fine.bin")))
            {
                foreach (var region in regions)
                {
                    var cells = HexGrid.FineHexesInRegion(region, FineResolution);
                    var length = TileFormat.HeaderBytes + cells.Count * TileFormat.RecordBytes;
                    lengths.Add(length);

                    var header = new byte[TileFormat.HeaderBytes];
                    Encoding.ASCII.GetBytes(TileFormat.Magic, header);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), TileFormat.Version);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), FineResolution);
                    BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), RegionResolution);
                    BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)cells.Count);
                    stream.Write(header);

                    var record = new byte[TileFormat.RecordBytes];
                    record[0] = 0;
                    for (var i = 0; i < cells.Count; i++)
                    {
                        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(1), 0);
                        record[3] = 0;
                        stream.Write(record);
                    }

                    offset += length;
                }
            }

            var manifest = JsonSerializer.Serialize(new
            {
                format = "spm-region-tiles",
                version = TileFormat.Version,
                generatedUtc = "2026-01-01T00:00:00+00:00",
                body = new { key = BodyKey, displayName = "Rover Test Body", radiusKm = 1737.4 },
                fineResolution = FineResolution,
                regionResolution = RegionResolution,
                recordBytes = TileFormat.RecordBytes,
                tileHeaderBytes = TileFormat.HeaderBytes,
                tilesFile = "fine.bin",
                biomes = new object[]
                {
                    new { code = 0, id = "test.buildable", displayName = "Buildable", elevationRank = 1, buildable = true },
                },
                sources = new Dictionary<string, string>(),
                regions = regions.Select((region, i) => new
                {
                    index = ((ulong)region).ToString(CultureInfo.InvariantCulture),
                    dominantBiome = 0,
                    hexCount = HexGrid.FineHexesInRegion(region, FineResolution).Count,
                    offset = lengths.Take(i).Sum(),
                    length = lengths[i],
                }),
            });

            File.WriteAllText(Path.Combine(Root, BodyKey, "manifest.json"), manifest);
        }
    }
}
