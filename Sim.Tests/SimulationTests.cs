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
using Sim.Time;
using Sim.World;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// M1 simulation tests: the clock, seeded RNG, construction lifecycle, economy,
/// power idling, tech enforcement and deterministic replay.
/// </summary>
public class SimulationTests
{
    [Fact]
    public void Clock_AdvancesInFixedSteps_AndPauses()
    {
        var clock = new SimulationClock();
        clock.Reset(100.0);
        clock.SetSpeed(1);

        Assert.Equal(0, clock.Advance(0.2));
        Assert.Equal(1, clock.Advance(0.1));
        Assert.Equal(SimulationClock.FixedStepHours, clock.GameTimeHours - 100.0);
        Assert.Equal(2, clock.Advance(0.5));
        Assert.Equal(3 * SimulationClock.FixedStepHours, clock.GameTimeHours - 100.0);

        clock.SetSpeed(4);
        Assert.Equal(4, clock.Advance(0.25));

        clock.SetSpeed(0);
        Assert.Equal(0, clock.Advance(1.0));
        Assert.Equal(7 * SimulationClock.FixedStepHours, clock.GameTimeHours - 100.0);
        Assert.Equal(0.0, clock.PendingHours);
    }

    [Fact]
    public void Random_SameSeedAndState_ProducesSameSequence()
    {
        var first = new DeterministicRandom(12345);
        var second = new DeterministicRandom(12345);

        var expected = Enumerable.Range(0, 8).Select(_ => first.NextDouble()).ToList();
        var actual = Enumerable.Range(0, 8).Select(_ => second.NextDouble()).ToList();

        Assert.Equal(expected, actual);
        Assert.All(expected, value => Assert.InRange(value, 0.0, 1.0));

        second.State = first.State;
        Assert.Equal(first.NextULong(), second.NextULong());
    }

    [Fact]
    public void Construction_PlannedToUnderConstructionToOperational()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);

        var generator = MakeDef("generator", buildHours: 2.0, powerKw: -10.0);
        var habitat = MakeDef("habitat", buildHours: 4.0, powerKw: 5.0);
        var campaign = fixture.CreateCampaign(world, generator, habitat);

        Assert.True(campaign.TryPlace(generator, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
        Assert.True(campaign.TryPlace(habitat, tile, fixture.NeighborOrdinals[0], LocalGrid.Center).Success);

        Assert.All(campaign.Buildings((ulong)fixture.Region),
            building => Assert.Equal(BuildingStatus.Planned, building.Status));

        campaign.Tick(1.0);
        var underConstruction = campaign.Buildings((ulong)fixture.Region);
        Assert.All(underConstruction, building =>
            Assert.Equal(BuildingStatus.UnderConstruction, building.Status));
        Assert.Equal(0.5, underConstruction[0].Progress, 3);
        Assert.Equal(0.25, underConstruction[1].Progress, 3);

        campaign.Tick(1.0);
        Assert.Equal(BuildingStatus.Operational, campaign.Buildings((ulong)fixture.Region)[0].Status);
        Assert.Equal(2.0, campaign.Buildings((ulong)fixture.Region)[0].ConstructionCompleteTime);

        campaign.Tick(2.0);
        var completed = campaign.Buildings((ulong)fixture.Region);
        Assert.All(completed, building =>
        {
            Assert.Equal(BuildingStatus.Operational, building.Status);
            Assert.Equal(1.0, building.Progress);
        });
    }

    [Fact]
    public void PowerDeficit_IdlesConsumer_UntilGeneratorComesOnline()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var region = (ulong)fixture.Region;

        var habitat = MakeDef("habitat", powerKw: 5.0,
            throughput: new Dictionary<string, double> { ["oxygen"] = -0.1 });
        var generator = MakeDef("generator", powerKw: -10.0,
            throughput: new Dictionary<string, double> { ["power"] = 10.0 });
        var campaign = fixture.CreateCampaign(world, habitat, generator);

        Assert.True(campaign.TryPlace(habitat, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);

        campaign.Tick(0.25);
        var consumer = Assert.Single(campaign.Buildings(region));
        Assert.Equal(BuildingStatus.Idle, consumer.Status);
        Assert.Contains("power deficit", consumer.StatusReason);
        Assert.True(campaign.GetEconomy(region).HasDeficit);

        Assert.True(campaign.TryPlace(generator, tile, fixture.NeighborOrdinals[0], LocalGrid.Center).Success);

        campaign.Tick(0.25);
        Assert.Equal(BuildingStatus.Operational, consumer.Status);
        Assert.Equal(string.Empty, consumer.StatusReason);
        Assert.False(campaign.GetEconomy(region).HasDeficit);
    }

    [Fact]
    public void Placement_ChargesExactCost_AndRefusesUnaffordableBuildings()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        var def = MakeDef("costly", cost: 10.0);

        campaign.Meta.Credits = 5.0;
        var refused = campaign.TryPlace(def, tile, fixture.CenterOrdinal, LocalGrid.Center);
        Assert.False(refused.Success);
        Assert.Contains("not enough credits", refused.Error);
        Assert.Equal(5.0, campaign.Meta.Credits);
        Assert.Empty(campaign.Buildings((ulong)fixture.Region));

        campaign.Meta.Credits = 10.0;
        Assert.True(campaign.TryPlace(def, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
        Assert.Equal(0.0, campaign.Meta.Credits);
        Assert.Single(campaign.Buildings((ulong)fixture.Region));
    }

    [Fact]
    public void TechRequirement_IsEnforcedInCampaignPreview()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var campaign = fixture.CreateCampaign(world);
        var def = MakeDef("restricted", requiredTech: "nuclear_power");

        var locked = campaign.Preview(def, tile, fixture.CenterOrdinal, LocalGrid.Center);
        Assert.False(locked.Success);
        Assert.Contains("requires tech", locked.Error);

        Assert.True(campaign.UnlockTech("nuclear_power"));
        Assert.True(campaign.Preview(def, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
        Assert.True(
            campaign.TryPlace(def, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
    }

    [Fact]
    public void TechPurchase_SpendsScienceAndUnlocksTech()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var campaign = fixture.CreateCampaign(world);
        campaign.Meta.SciencePoints = 100.0;

        var result = campaign.TryPurchaseTech("nuclear_power");

        Assert.Equal(TechPurchaseStatus.Purchased, result.Status);
        Assert.True(result.Success);
        Assert.Equal(60.0, campaign.Meta.SciencePoints);
        Assert.True(campaign.IsTechUnlocked("nuclear_power"));
        Assert.Contains("Nuclear Power", result.Message);
    }

    [Fact]
    public void TechPurchase_RefusesUnknownAndDuplicateTechs()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var campaign = fixture.CreateCampaign(world);
        campaign.Meta.SciencePoints = 500.0;

        var unknown = campaign.TryPurchaseTech("not_a_tech");
        Assert.Equal(TechPurchaseStatus.UnknownTech, unknown.Status);
        Assert.Equal(500.0, campaign.Meta.SciencePoints);

        Assert.True(campaign.TryPurchaseTech("nuclear_power").Success);
        var duplicate = campaign.TryPurchaseTech("nuclear_power");
        Assert.Equal(TechPurchaseStatus.AlreadyUnlocked, duplicate.Status);
        Assert.Equal(460.0, campaign.Meta.SciencePoints);
    }

    [Fact]
    public void TechPurchase_RequiresPrerequisitesBeforeSpendingScience()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var campaign = fixture.CreateCampaign(world);
        campaign.Meta.SciencePoints = 500.0;

        var locked = campaign.TryPurchaseTech("deep_space_comms");
        Assert.Equal(TechPurchaseStatus.PrerequisitesMissing, locked.Status);
        Assert.Contains("radio_astronomy", locked.Message);
        Assert.Equal(500.0, campaign.Meta.SciencePoints);
        Assert.False(campaign.IsTechUnlocked("deep_space_comms"));

        Assert.True(campaign.TryPurchaseTech("nuclear_power").Success);
        Assert.True(campaign.TryPurchaseTech("radio_astronomy").Success);
        Assert.True(campaign.TryPurchaseTech("deep_space_comms").Success);
        Assert.Equal(190.0, campaign.Meta.SciencePoints);
    }

    [Fact]
    public void TechPurchase_RefusesInsufficientScienceWithoutChangingState()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var campaign = fixture.CreateCampaign(world);
        campaign.Meta.SciencePoints = 39.5;

        var result = campaign.TryPurchaseTech("nuclear_power");

        Assert.Equal(TechPurchaseStatus.InsufficientScience, result.Status);
        Assert.Contains("40", result.Message);
        Assert.Equal(39.5, campaign.Meta.SciencePoints);
        Assert.False(campaign.IsTechUnlocked("nuclear_power"));
    }

    [Fact]
    public void SameSeedAndTickSequence_ProducesSameState()
    {
        using var fixture = new Fixture();
        using var world = fixture.Open();
        var tile = world.GetRegion((ulong)fixture.Region);
        var def = MakeDef("habitat", buildHours: 3.0, powerKw: 5.0,
            throughput: new Dictionary<string, double> { ["oxygen"] = -0.1 });
        var first = fixture.CreateCampaign(world, def);
        var second = fixture.CreateCampaign(world, def);

        first.Meta.RngSeed = second.Meta.RngSeed = 99;
        first.Meta.RngState = second.Meta.RngState = 99;
        first.RestoreMeta(first.Meta);
        second.RestoreMeta(second.Meta);

        Assert.True(first.TryPlace(def, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
        Assert.True(second.TryPlace(def, tile, fixture.CenterOrdinal, LocalGrid.Center).Success);
        first.NextRandom();
        second.NextRandom();

        first.Tick(0.25);
        second.Tick(0.25);
        first.Tick(1.75);
        second.Tick(1.75);

        Assert.Equal(first.Meta.GameTimeHours, second.Meta.GameTimeHours);
        Assert.Equal(first.Meta.Credits, second.Meta.Credits);
        Assert.Equal(first.Meta.RngState, second.Meta.RngState);
        Assert.Equal(
            first.Meta.Stockpile.OrderBy(pair => pair.Key),
            second.Meta.Stockpile.OrderBy(pair => pair.Key));
        Assert.Equal(
            first.Buildings((ulong)fixture.Region).Select(b => (b.Status, b.Progress, b.StatusReason)),
            second.Buildings((ulong)fixture.Region).Select(b => (b.Status, b.Progress, b.StatusReason)));
    }

    [Fact]
    public void PentagonCell_HasExactlyFiveNeighbours()
    {
        var pentagon = H3Index.GetPentagons(6).First();

        Assert.True(pentagon.IsPentagon);
        var neighbours = pentagon.GridDiskDistances(1)
            .Where(cell => cell.Distance == 1)
            .ToList();

        Assert.Equal(5, neighbours.Count);
        Assert.Equal(5, neighbours.Select(cell => (ulong)cell.Index).Distinct().Count());
    }

    private static BuildingDef MakeDef(
        string id,
        double buildHours = 0.0,
        double powerKw = 0.0,
        IReadOnlyDictionary<string, double>? throughput = null,
        string requiredTech = "",
        double cost = 1.0)
        => new()
        {
            Id = id,
            DisplayName = id,
            Category = BuildingCategory.Special,
            MinSiteScore = 0.0,
            MaxSlopeM = 500.0,
            AllowSingleCellCrater = true,
            Cost = new Dictionary<string, double> { ["credits"] = cost },
            BuildTimeHours = buildHours,
            PowerKw = powerKw,
            ThroughputPerHour = throughput ?? new Dictionary<string, double>(),
            RequiredTech = requiredTech,
        };

    private sealed class Fixture : IDisposable
    {
        private const int FineResolution = 6;
        private const int RegionResolution = 3;
        private const string BodyKey = "sim-test";

        public Fixture()
        {
            Region = HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, RegionResolution);
            Cells = HexGrid.FineHexesInRegion(Region, FineResolution);

            var ordinalByHex = new Dictionary<ulong, int>();
            for (var i = 0; i < Cells.Count; i++)
                ordinalByHex[(ulong)Cells[i]] = i;

            CenterOrdinal = Enumerable.Range(0, Cells.Count).First(ordinal =>
                Cells[ordinal].GridDiskDistances(1)
                    .Where(cell => cell.Distance == 1)
                    .All(cell => ordinalByHex.ContainsKey((ulong)cell.Index)));

            NeighborOrdinals = Cells[CenterOrdinal].GridDiskDistances(1)
                .Where(cell => cell.Distance == 1)
                .Select(cell => ordinalByHex[(ulong)cell.Index])
                .ToList();

            Registry = new BuildingRegistry(Array.Empty<BuildingDef>());

            Root = Path.Combine(Path.GetTempPath(), "spm-sim-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, BodyKey));
            Save();
        }

        public H3Index Region { get; }

        public IReadOnlyList<H3Index> Cells { get; }

        public int CenterOrdinal { get; }

        public IReadOnlyList<int> NeighborOrdinals { get; }

        public BuildingRegistry Registry { get; private set; }

        public string Root { get; }

        public BodyWorld Open() => BodyWorld.Open(BodyKey, Root);

        public CampaignState CreateCampaign(BodyWorld world, params BuildingDef[] defs)
        {
            if (defs.Length > 0)
                Registry = new BuildingRegistry(defs);

            return new CampaignState(world, Registry);
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

        private void Save()
        {
            var biomes = new byte[Cells.Count];
            var elevations = new short[Cells.Count];
            var craters = new CraterKind[Cells.Count];
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
                record[0] = 0; // buildable biome code
                for (var i = 0; i < Cells.Count; i++)
                {
                    BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(1), elevations[i]);
                    record[3] = (byte)craters[i];
                    stream.Write(record);
                }
            }

            var manifest = JsonSerializer.Serialize(new
            {
                format = "spm-region-tiles",
                version = TileFormat.Version,
                generatedUtc = "2026-01-01T00:00:00+00:00",
                body = new { key = BodyKey, displayName = "Sim Test Body", radiusKm = 1737.4 },
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
    }
}
