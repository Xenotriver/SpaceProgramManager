using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using H3;
using H3.Algorithms;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Hex;
using Sim.Site;
using Sim.World;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// M5 C4 tests: static failure causes, repair billing/completion, event-linked
/// damage, hazard guards and v4 shard round-trips of typed failure records.
/// </summary>
public class FailureTests : IDisposable
{
    private const int FineResolution = 6;
    private const int RegionResolution = 3;
    private const string BodyKey = "failure-test";

    private readonly Fixture _fixture = new();

    [Fact]
    public void Catalogue_HasUniqueCausesAndValidRepairs()
    {
        Assert.Equal(FailureCatalogue.All.Count,
            FailureCatalogue.All.Select(item => item.Id).Distinct().Count());
        Assert.All(FailureCatalogue.All, cause =>
        {
            Assert.True(cause.MinSeverity >= 0.0 && cause.MinSeverity <= cause.MaxSeverity);
            Assert.True(cause.MaxSeverity <= 1.0);
            Assert.True(cause.RepairHours > 0.0);
            Assert.True(cause.RepairCost.GetValueOrDefault("credits") > 0.0);
            var severity = FailureCatalogue.RollSeverity(cause, 0.5);
            Assert.InRange(severity, cause.MinSeverity, cause.MaxSeverity);
        });
    }

    [Fact]
    public void TryDamageBuilding_RequiresAnOperationalInstallation()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign, tickToOperational: false);

        var early = campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.DustIntrusionId);
        Assert.Equal(MaintenanceActionStatus.NotOperational, early.Status);

        campaign.Tick(0.25);
        var damaged = campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.DustIntrusionId, 0.4);

        Assert.Equal(MaintenanceActionStatus.Damaged, damaged.Status);
        Assert.Equal(BuildingStatus.Damaged, building.Status);
        Assert.NotNull(building.Failure);
        Assert.Equal(FailureCatalogue.DustIntrusionId, building.Failure!.Cause);
        Assert.Equal(0.4, building.Failure.Severity, 2);
        Assert.Equal(60.0, building.Health, 2);
        Assert.Single(campaign.DamagedInstallations);
    }

    [Fact]
    public void TryDamageBuilding_HighSeverityDestroysAndStillAllowsRepair()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);

        var result = campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.MicrometeoriteId, 0.95);

        Assert.Equal(MaintenanceActionStatus.Damaged, result.Status);
        Assert.Equal(BuildingStatus.Destroyed, building.Status);
        Assert.Equal(5.0, building.Health, 2);
        Assert.Equal(MaintenanceActionStatus.RepairStarted,
            campaign.TryStartRepair(region, building.Id).Status);
    }

    [Fact]
    public void TryDamageBuilding_RefusesUnknownCausesAndDuplicates()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);

        var unknown = campaign.TryDamageBuilding(region, building.Id, "no_such_cause");
        Assert.True(campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.PowerSurgeId, 0.3).Success);
        var duplicate = campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.PowerSurgeId, 0.3);

        Assert.Equal(MaintenanceActionStatus.UnknownCause, unknown.Status);
        Assert.Equal(MaintenanceActionStatus.AlreadyDamaged, duplicate.Status);
        Assert.Null(building.Failure?.RepairStartGameHour);
    }

    [Fact]
    public void TryStartRepair_PaysTheScaledBillAndSchedulesCompletion()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);
        campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.PowerSurgeId, 0.5);
        var credits = campaign.Meta.Credits;
        var electronics = campaign.Meta.Stockpile["electronics"];
        var cause = FailureCatalogue.PowerSurge;
        var expected = cause.RepairCost["credits"] * (0.5 + 0.5);

        var result = campaign.TryStartRepair(region, building.Id);

        Assert.Equal(MaintenanceActionStatus.RepairStarted, result.Status);
        Assert.Equal(expected, result.Credits, 2);
        Assert.Equal(credits - expected, campaign.Meta.Credits, 2);
        Assert.Equal(electronics - cause.RepairCost["electronics"], campaign.Meta.Stockpile["electronics"], 2);
        Assert.Equal(campaign.Meta.GameTimeHours, building.Failure!.RepairStartGameHour);
        Assert.Equal(BuildingStatus.Damaged, building.Status);
        Assert.Contains("repairing", building.StatusReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Tick_CompletesRepairAndRestoresTheInstallation()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);
        campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.DustIntrusionId, 0.4);
        var duration = FailureCatalogue.RepairHours(
            FailureCatalogue.DustIntrusion, building.Failure!);
        Assert.True(campaign.TryStartRepair(region, building.Id).Success);

        campaign.Tick(duration - 0.1);
        Assert.Equal(BuildingStatus.Damaged, building.Status);

        campaign.Tick(0.25);
        Assert.Equal(BuildingStatus.Operational, building.Status);
        Assert.Equal(100.0, building.Health, 2);
        Assert.Equal(string.Empty, building.StatusReason);
        Assert.NotNull(building.Failure?.RepairCompleteGameHour);
        Assert.Empty(campaign.DamagedInstallations);
        Assert.Equal(MaintenanceActionStatus.Healthy, campaign.TryStartRepair(region, building.Id).Status);
    }

    [Fact]
    public void TryStartRepair_RefusesRepeatsAndUnaffordableBills()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);
        campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.MicrometeoriteId, 0.6);
        campaign.Meta.Credits = 0.0;

        var poor = campaign.TryStartRepair(region, building.Id);
        Assert.Equal(MaintenanceActionStatus.CannotAfford, poor.Status);

        campaign.Meta.Credits = 10_000_000.0;
        Assert.True(campaign.TryStartRepair(region, building.Id).Success);
        Assert.Equal(MaintenanceActionStatus.AlreadyRepairing,
            campaign.TryStartRepair(region, building.Id).Status);
    }

    [Fact]
    public void ResolveEvent_WithFailureCause_DamagesAnInstallationInTheRegion()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);
        var pending = new PendingCampaignEvent
        {
            DefId = EventCatalogue.EquipmentFaultId,
            TargetRegion = region,
        };
        campaign.Meta.PendingEvents.Add(pending);

        var result = campaign.TryResolveEvent(pending.Id, "push_on");

        Assert.Equal(EventActionStatus.Resolved, result.Status);
        Assert.Equal(BuildingStatus.Damaged, building.Status);
        Assert.Equal(FailureCatalogue.PowerSurgeId, building.Failure?.Cause);
        Assert.Contains("Power surge", Assert.Single(campaign.Meta.EventLog).Summary ?? string.Empty,
            StringComparison.Ordinal);
    }

    [Fact]
    public void ResolveEvent_SafeChoice_DoesNotDamageAnInstallation()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);
        var pending = new PendingCampaignEvent
        {
            DefId = EventCatalogue.EquipmentFaultId,
            TargetRegion = region,
        };
        campaign.Meta.PendingEvents.Add(pending);

        Assert.True(campaign.TryResolveEvent(pending.Id, "inspect").Success);

        Assert.Equal(BuildingStatus.Operational, building.Status);
        Assert.Null(building.Failure);
    }

    [Fact]
    public void Hazard_SkipsPausedInstallations()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, _) = PlaceOperational(world, campaign);
        building.Paused = true;

        campaign.Tick(2000.0);

        Assert.Null(building.Failure);
        Assert.Equal(BuildingStatus.Operational, building.Status);
    }

    [Fact]
    public void Failure_RoundTripsThroughV4Shard()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        var (_, building, region) = PlaceOperational(world, campaign);
        campaign.TryDamageBuilding(region, building.Id, FailureCatalogue.LifeSupportLeakId, 0.45);
        campaign.TryStartRepair(region, building.Id);

        var store = new CampaignStore(_fixture.Root);
        store.SaveRegion(region, campaign.Buildings(region));
        var loaded = store.LoadRegion(region);

        var restored = Assert.Single(loaded.Buildings);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(building.Id, restored.Id);
        Assert.Equal(BuildingStatus.Damaged, restored.Status);
        Assert.Equal(FailureCatalogue.LifeSupportLeakId, restored.Failure?.Cause);
        Assert.Equal(0.45, restored.Failure!.Severity, 2);
        Assert.Equal(building.Failure!.RepairStartGameHour, restored.Failure.RepairStartGameHour);
    }

    private CampaignState CreateCampaign(BodyWorld world)
        => new(world, new BuildingRegistry(new[]
        {
            new BuildingDef
            {
                Id = "generator",
                DisplayName = "Generator",
                Category = BuildingCategory.Power,
                Cost = new Dictionary<string, double> { ["credits"] = 1.0 },
                BuildTimeHours = 0.25,
                ThroughputPerHour = new Dictionary<string, double>(),
            },
        }));

    private (CampaignState Campaign, BuildingInstance Building, ulong Region) PlaceOperational(
        BodyWorld world,
        CampaignState campaign,
        bool tickToOperational = true)
    {
        var region = (ulong)_fixture.Region;
        var tile = world.GetRegion(region);
        var def = campaign.Registry.TryGet("generator", out var found) ? found : null;
        Assert.NotNull(def);

        Assert.True(campaign.TryPlace(def!, tile, _fixture.CenterOrdinal, LocalGrid.Center).Success);
        if (tickToOperational)
            campaign.Tick(0.25);

        return (campaign, Assert.Single(campaign.Buildings(region)), region);
    }

    public void Dispose() => _fixture.Dispose();

    /// <summary>Tiny one-region deterministic bake; failure logic only needs a world handle.</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Region = HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, RegionResolution);
            var cells = HexGrid.FineHexesInRegion(Region, FineResolution);
            var length = TileFormat.HeaderBytes + cells.Count * TileFormat.RecordBytes;

            var ordinalByHex = new Dictionary<ulong, int>();
            for (var i = 0; i < cells.Count; i++)
                ordinalByHex[(ulong)cells[i]] = i;
            CenterOrdinal = Enumerable.Range(0, cells.Count).First(ordinal =>
                cells[ordinal].GridDiskDistances(1)
                    .Where(cell => cell.Distance == 1)
                    .All(cell => ordinalByHex.ContainsKey((ulong)cell.Index)));

            Root = Path.Combine(Path.GetTempPath(), "spm-failure-tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, BodyKey));

            using (var stream = File.Create(Path.Combine(Root, BodyKey, "fine.bin")))
            {
                var header = new byte[TileFormat.HeaderBytes];
                Encoding.ASCII.GetBytes(TileFormat.Magic, header);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(4), TileFormat.Version);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(6), FineResolution);
                BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(8), RegionResolution);
                BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(12), (uint)cells.Count);
                stream.Write(header);

                var record = new byte[TileFormat.RecordBytes];
                stream.Write(record);
                for (var i = 1; i < cells.Count; i++)
                    stream.Write(record);
            }

            var manifest = JsonSerializer.Serialize(new
            {
                format = "spm-region-tiles",
                version = TileFormat.Version,
                generatedUtc = "2026-01-01T00:00:00+00:00",
                body = new { key = BodyKey, displayName = "Failure Test Body", radiusKm = 1737.4 },
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
                        hexCount = cells.Count,
                        offset = 0L,
                        length,
                    },
                },
            });
            File.WriteAllText(Path.Combine(Root, BodyKey, "manifest.json"), manifest);
        }

        public H3Index Region { get; }

        public int CenterOrdinal { get; }

        public string Root { get; }

        public BodyWorld Open() => BodyWorld.Open(BodyKey, Root);

        public void Dispose()
        {
            try
            {
                Directory.Delete(Root, recursive: true);
            }
            catch (IOException)
            {
                // Best-effort cleanup.
            }
        }
    }
}
