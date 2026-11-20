using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using H3;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Hex;
using Sim.Site;
using Sim.World;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// M5 C3 tests: static contract/event catalogues, partial-delivery payouts,
/// deadline settlement, event cooldowns and v4 persistence of generated state.
/// </summary>
public class MissionTests : IDisposable
{
    private const int FineResolution = 6;
    private const int RegionResolution = 3;
    private const string BodyKey = "mission-test";

    private readonly Fixture _fixture = new();

    [Fact]
    public void Catalogues_HaveUniqueIdsAndChoices()
    {
        Assert.Equal(ContractCatalogue.All.Count, ContractCatalogue.All.Select(item => item.Id).Distinct().Count());
        Assert.Equal(EventCatalogue.All.Count, EventCatalogue.All.Select(item => item.Id).Distinct().Count());
        Assert.All(ContractCatalogue.All, contract =>
        {
            Assert.NotEmpty(contract.Targets);
            Assert.True(contract.CreditsReward > 0.0);
            Assert.True(contract.DurationHours > 0.0);
        });
        Assert.All(EventCatalogue.All, item =>
        {
            Assert.NotEmpty(item.Choices);
            Assert.Equal(item.Choices.Count, item.Choices.Select(choice => choice.Id).Distinct().Count());
        });
    }

    [Fact]
    public void TryAcceptContract_AddsActiveContractWithDeadline()
    {
        var campaign = CreateCampaign();

        var result = campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var contract = Assert.Single(campaign.ActiveContracts);

        Assert.Equal(ContractActionStatus.Accepted, result.Status);
        Assert.Equal(ContractCatalogue.OxygenResupplyId, contract.DefId);
        Assert.Equal(168.0, contract.DeadlineGameHour - contract.AcceptedGameHour);
        Assert.False(contract.RewardClaimed);
    }

    [Fact]
    public void TryAcceptContract_RefusesDuplicatesAndUnknownDefinitions()
    {
        var campaign = CreateCampaign();
        Assert.True(campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId).Success);

        var duplicate = campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var unknown = campaign.TryAcceptContract("no_such_contract");

        Assert.Equal(ContractActionStatus.AlreadyAccepted, duplicate.Status);
        Assert.Equal(ContractActionStatus.UnknownContract, unknown.Status);
        Assert.Single(campaign.Meta.Contracts);
    }

    [Fact]
    public void TryAcceptContract_RequiresTechForLockedOffers()
    {
        var campaign = CreateCampaign();

        var locked = campaign.TryAcceptContract(ContractCatalogue.RefinedMetalsOrderId);
        Assert.Equal(ContractActionStatus.TechLocked, locked.Status);
        Assert.DoesNotContain(ContractCatalogue.RefinedMetalsOrderId,
            campaign.AvailableContracts.Select(item => item.Id));

        campaign.UnlockTech("smelting");
        Assert.Contains(ContractCatalogue.RefinedMetalsOrderId,
            campaign.AvailableContracts.Select(item => item.Id));
        Assert.True(campaign.TryAcceptContract(ContractCatalogue.RefinedMetalsOrderId).Success);
    }

    [Fact]
    public void TryDeliverContract_ConsumesStockpileAndCompletesAllTargets()
    {
        var campaign = CreateCampaign();
        campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var contract = Assert.Single(campaign.ActiveContracts);

        var oxygen = campaign.TryDeliverContract(contract.Id);
        var water = campaign.TryDeliverContract(contract.Id);

        Assert.Equal(ContractActionStatus.Delivered, oxygen.Status);
        Assert.Equal(ContractActionStatus.Delivered, water.Status);
        Assert.Equal(380.0, campaign.Meta.Stockpile["oxygen"]);
        Assert.Equal(140.0, campaign.Meta.Stockpile["water"]);
        Assert.Equal(ContractStatus.Completed, contract.Status);
        Assert.Equal(120.0, contract.Progress["oxygen"]);
        Assert.Equal(60.0, contract.Progress["water"]);
        Assert.Empty(campaign.ActiveContracts);
    }

    [Fact]
    public void TryDeliverContract_RefusesEmptyStockAndUnknownCounters()
    {
        var campaign = CreateCampaign();
        campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var contract = Assert.Single(campaign.ActiveContracts);

        var unknown = campaign.TryDeliverContract(contract.Id, "unobtainium");
        campaign.Meta.Stockpile["oxygen"] = 0.0;
        var empty = campaign.TryDeliverContract(contract.Id, "oxygen");

        Assert.Equal(ContractActionStatus.UnknownCounter, unknown.Status);
        Assert.Equal(ContractActionStatus.InsufficientStock, empty.Status);
        Assert.False(contract.Progress.ContainsKey("oxygen"));
    }

    [Fact]
    public void TryClaimReward_PaysPartialPayoutAndClosesContract()
    {
        var campaign = CreateCampaign();
        campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var contract = Assert.Single(campaign.ActiveContracts);
        campaign.TryDeliverContract(contract.Id, "oxygen");

        var result = campaign.TryClaimReward(contract.Id);

        Assert.Equal(ContractActionStatus.Settled, result.Status);
        Assert.Equal(800_000.0, result.Credits, 2);
        Assert.Equal(8.0, result.Science, 2);
        Assert.Equal(10_800_000.0, campaign.Meta.Credits, 2);
        Assert.Equal(8.0, campaign.Meta.SciencePoints, 2);
        Assert.Equal(ContractStatus.Failed, contract.Status);
        Assert.True(contract.RewardClaimed);
        Assert.Equal(ContractActionStatus.AlreadyClaimed, campaign.TryClaimReward(contract.Id).Status);
    }

    [Fact]
    public void TryClaimReward_PaysCompletionBonusWhenEveryTargetIsMet()
    {
        var campaign = CreateCampaign();
        campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var contract = Assert.Single(campaign.ActiveContracts);
        campaign.TryDeliverContract(contract.Id, "oxygen");
        campaign.TryDeliverContract(contract.Id, "water");

        var result = campaign.TryClaimReward(contract.Id);

        Assert.Equal(ContractActionStatus.Claimed, result.Status);
        Assert.Equal(1_320_000.0, result.Credits, 2);
        Assert.Equal(12.0, result.Science, 2);
        Assert.Equal(ContractStatus.Completed, contract.Status);
    }

    [Fact]
    public void Tick_SettlesExpiredContractWithoutDoublePaying()
    {
        var campaign = CreateCampaign();
        campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        var contract = Assert.Single(campaign.ActiveContracts);
        contract.DeadlineGameHour = campaign.Meta.GameTimeHours + 0.1;
        var credits = campaign.Meta.Credits;

        campaign.Tick(0.25);

        Assert.Equal(ContractStatus.Failed, contract.Status);
        Assert.True(contract.RewardClaimed);
        Assert.Equal(credits, campaign.Meta.Credits);
        Assert.Equal(ContractActionStatus.AlreadyClaimed, campaign.TryClaimReward(contract.Id).Status);
    }

    [Fact]
    public void TryRaiseRandomEvent_WaitsForEarliestHourAndHonoursCooldown()
    {
        var campaign = CreateCampaign();

        Assert.Equal(EventActionStatus.NothingRaised, campaign.TryRaiseRandomEvent().Status);

        campaign.Meta.GameTimeHours = 25.0;
        Assert.True(campaign.TryRaiseRandomEvent().Success);
        var pending = Assert.Single(campaign.Meta.PendingEvents);
        Assert.Equal(97.0, campaign.Meta.EventCooldownGameHour!.Value);

        Assert.Equal(EventActionStatus.NothingRaised, campaign.TryRaiseRandomEvent().Status);
        Assert.Single(campaign.Meta.PendingEvents);

        campaign.Meta.GameTimeHours = 98.0;
        Assert.Equal(EventActionStatus.NothingRaised, campaign.TryRaiseRandomEvent().Status);
        Assert.Equal("an event is already waiting", campaign.TryRaiseRandomEvent().Message);
        Assert.True(EventCatalogue.TryGet(pending.DefId, out _));
    }

    [Fact]
    public void ResolveEvent_AppliesEffectsAndLogsOnce()
    {
        var campaign = CreateCampaign();
        campaign.Meta.GameTimeHours = 25.0;
        campaign.TryRaiseRandomEvent();
        var pending = Assert.Single(campaign.Meta.PendingEvents);
        Assert.True(EventCatalogue.TryGet(pending.DefId, out var def));
        var choice = def!.Choices[0];
        var credits = campaign.Meta.Credits;
        var science = campaign.Meta.SciencePoints;

        var result = campaign.TryResolveEvent(pending.Id, choice.Id);

        Assert.Equal(EventActionStatus.Resolved, result.Status);
        Assert.Equal(credits + choice.Effect.Credits, campaign.Meta.Credits);
        Assert.Equal(science + choice.Effect.Science, campaign.Meta.SciencePoints);
        Assert.Empty(campaign.Meta.PendingEvents);
        var entry = Assert.Single(campaign.Meta.EventLog);
        Assert.Equal(pending.DefId, entry.DefId);
        Assert.Equal(choice.Id, entry.Choice);
        Assert.Equal(EventActionStatus.UnknownEvent,
            campaign.TryResolveEvent(pending.Id, choice.Id).Status);
    }

    [Fact]
    public void Missions_RoundTripThroughV4Save()
    {
        using var world = _fixture.Open();
        var campaign = CreateCampaign(world);
        campaign.TryAcceptContract(ContractCatalogue.OxygenResupplyId);
        campaign.TryDeliverContract(campaign.ActiveContracts[0].Id, "oxygen");
        campaign.Meta.GameTimeHours = 25.0;
        campaign.TryRaiseRandomEvent();

        var store = new CampaignStore(_fixture.Root);
        store.SaveMeta(campaign.Meta, BakeFingerprint.From(world.Manifest));
        var loaded = store.LoadMeta(BakeFingerprint.From(world.Manifest));

        var contract = Assert.Single(loaded.Contracts);
        Assert.Equal(ContractCatalogue.OxygenResupplyId, contract.DefId);
        Assert.Equal(120.0, contract.Progress["oxygen"]);
        var pending = Assert.Single(loaded.PendingEvents);
        Assert.Equal(campaign.Meta.PendingEvents[0].Choices, pending.Choices);
        Assert.Equal(campaign.Meta.EventCooldownGameHour, loaded.EventCooldownGameHour);
    }

    private CampaignState CreateCampaign(BodyWorld? world = null)
    {
        world ??= _fixture.Open();
        return new CampaignState(world, new BuildingRegistry(Array.Empty<BuildingDef>()));
    }

    public void Dispose() => _fixture.Dispose();

    /// <summary>Tiny one-region deterministic bake; mission logic only needs a world handle.</summary>
    private sealed class Fixture : IDisposable
    {
        public Fixture()
        {
            Region = HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, RegionResolution);
            var cells = HexGrid.FineHexesInRegion(Region, FineResolution);
            var length = TileFormat.HeaderBytes + cells.Count * TileFormat.RecordBytes;

            Root = Path.Combine(Path.GetTempPath(), "spm-mission-tests", Guid.NewGuid().ToString("N"));
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
                record[0] = 0;
                foreach (var _ in cells)
                    stream.Write(record);
            }

            var manifest = JsonSerializer.Serialize(new
            {
                format = "spm-region-tiles",
                version = TileFormat.Version,
                generatedUtc = "2026-01-01T00:00:00+00:00",
                body = new { key = BodyKey, displayName = "Mission Test Body", radiusKm = 1737.4 },
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
