using H3;
using Sim.Buildings;
using Sim.Campaign;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Tests for the save-slot catalogue: discovery, ordering, the derived
/// <c>slot.json</c> cache, rebuilds, fingerprint mismatches and deletion safety.
/// </summary>
public class CampaignSlotTests : IDisposable
{
    private static readonly BakeFingerprint Fingerprint =
        new("moon", 6, 3, 41162, "2026-11-11T21:48:07+00:00");

    private static readonly DateTimeOffset T0 = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "spm-slot-tests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void List_FindsLegacySlotAndIgnoresOtherDirectories()
    {
        var store = new CampaignStore(Path.Combine(_root, CampaignSlotStore.DefaultSlotId));
        store.SaveMeta(new CampaignMeta(), Fingerprint);
        Directory.CreateDirectory(Path.Combine(_root, "not-a-slot"));
        File.WriteAllText(Path.Combine(_root, "readme.txt"), "ignore me");

        var slots = new CampaignSlotStore(_root).List(Fingerprint);

        var slot = Assert.Single(slots);
        Assert.Equal(CampaignSlotStore.DefaultSlotId, slot.Id);
        Assert.Equal("Slot 1", slot.Name);
        Assert.Equal(SlotState.Ok, slot.State);
        Assert.Equal(0, slot.BuildingCount);
    }

    [Fact]
    public void Create_WritesCampaignAndSummary()
    {
        var catalogue = new CampaignSlotStore(_root);

        var id = catalogue.Create("Test Campaign", new CampaignMeta(), Fingerprint);

        var directory = Path.Combine(_root, id);
        Assert.True(File.Exists(Path.Combine(directory, "campaign.json")));
        Assert.True(File.Exists(Path.Combine(directory, "slot.json")));

        var slot = Assert.Single(catalogue.List(Fingerprint));
        Assert.Equal(id, slot.Id);
        Assert.Equal("Test Campaign", slot.Name);
        Assert.Equal(Fingerprint.BodyKey, slot.BodyKey);
        Assert.Equal(SlotState.Ok, slot.State);
        Assert.True(slot.LastPlayedUtc > T0);
    }

    [Fact]
    public void Summary_RebuildsFromShardsWhenMissing()
    {
        var store = new CampaignStore(Path.Combine(_root, "slot-a"));
        var meta = new CampaignMeta { UpdatedUtc = T0 };
        meta.VisitedRegions.Add(1);
        meta.VisitedRegions.Add(2);
        store.SaveMeta(meta, Fingerprint);
        store.SaveRegion(1, new[] { Building("habitat"), Building("solar_array") });
        store.SaveRegion(2, new[] { Building("rtg_generator") });

        var slot = Assert.Single(new CampaignSlotStore(_root).List(Fingerprint));

        Assert.Equal(3, slot.BuildingCount);
        Assert.Equal(2, slot.VisitedRegionCount);
        Assert.True(File.Exists(store.SummaryPath));
    }

    [Fact]
    public void Summary_IsRebuiltWhenItsTimestampIsStale()
    {
        var store = new CampaignStore(Path.Combine(_root, "slot-a"));
        var meta = new CampaignMeta { UpdatedUtc = T0.AddHours(1) };
        store.SaveMeta(meta, Fingerprint);
        store.SaveRegion(1, new[] { Building("habitat") });

        store.SaveSummary(new SlotSummary
        {
            Id = "slot-a",
            Name = "Stale name",
            UpdatedUtc = T0,
            BuildingCount = 99,
        });

        var slot = Assert.Single(new CampaignSlotStore(_root).List(Fingerprint));

        Assert.Equal(1, slot.BuildingCount);
        Assert.Equal("Stale name", slot.Name);
    }

    [Fact]
    public void Summary_IsTrustedWhileItsTimestampMatches()
    {
        var store = new CampaignStore(Path.Combine(_root, "slot-a"));
        var meta = new CampaignMeta { UpdatedUtc = T0 };
        store.SaveMeta(meta, Fingerprint);
        store.SaveRegion(1, new[] { Building("habitat") });

        // A matching timestamp means the cache is used as-is, even if its count
        // is wrong. Rebuilds happen on the next mutation.
        store.SaveSummary(new SlotSummary
        {
            Id = "slot-a",
            Name = "Cached",
            UpdatedUtc = T0,
            BuildingCount = 7,
        });

        var slot = Assert.Single(new CampaignSlotStore(_root).List(Fingerprint));

        Assert.Equal(7, slot.BuildingCount);
    }

    [Fact]
    public void List_MarksFingerprintMismatchAndKeepsFiles()
    {
        var store = new CampaignStore(Path.Combine(_root, "slot-a"));
        store.SaveMeta(new CampaignMeta(), Fingerprint);

        var otherBake = Fingerprint with { GeneratedUtc = "2026-12-01T00:00:00+00:00" };
        var slot = Assert.Single(new CampaignSlotStore(_root).List(otherBake));

        Assert.Equal(SlotState.Incompatible, slot.State);
        Assert.Contains("created for", slot.Problem);
        Assert.True(File.Exists(store.MetaPath));
    }

    [Fact]
    public void List_MarksCorruptMetaUnreadableAndKeepsIt()
    {
        var directory = Path.Combine(_root, "slot-a");
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, "campaign.json");
        File.WriteAllText(path, "{ not json");

        var slot = Assert.Single(new CampaignSlotStore(_root).List(Fingerprint));

        Assert.Equal(SlotState.Unreadable, slot.State);
        Assert.False(string.IsNullOrWhiteSpace(slot.Problem));
        Assert.Equal("{ not json", File.ReadAllText(path));
    }

    [Fact]
    public void List_OrdersByLastPlayed()
    {
        var catalogue = new CampaignSlotStore(_root);
        var oldId = catalogue.Create("Old", new CampaignMeta(), Fingerprint);
        var newId = catalogue.Create("New", new CampaignMeta(), Fingerprint);
        var oldStore = new CampaignStore(Path.Combine(_root, oldId));
        oldStore.TryReadSummary(out var summary);
        oldStore.SaveSummary(summary! with { LastPlayedUtc = T0 });
        catalogue.TryGet(newId, Fingerprint);

        var slots = catalogue.List(Fingerprint);

        Assert.Equal(newId, slots[0].Id);
        Assert.Equal(oldId, slots[1].Id);
    }

    [Fact]
    public void Delete_RemovesTheSlotDirectory()
    {
        var catalogue = new CampaignSlotStore(_root);
        var id = catalogue.Create("Delete me", new CampaignMeta(), Fingerprint);
        var directory = Path.Combine(_root, id);

        Assert.True(catalogue.Delete(id));
        Assert.False(Directory.Exists(directory));
        Assert.False(catalogue.Delete(id));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("../escape")]
    [InlineData("slot/child")]
    [InlineData(@"slot\child")]
    public void Delete_RejectsIdsOutsideTheRoot(string id)
    {
        var catalogue = new CampaignSlotStore(_root);

        Assert.Throws<ArgumentException>(() => catalogue.Delete(id));
    }

    [Fact]
    public void Summary_RoundTripsViewState()
    {
        var store = new CampaignStore(Path.Combine(_root, "slot-a"));
        store.SaveSummary(new SlotSummary
        {
            Id = "slot-a",
            Name = "Views",
            UpdatedUtc = T0,
            View = new SlotViewState
            {
                Mode = "management",
                GlobeYawDegrees = 120f,
                GlobePitchDegrees = 10f,
                GlobeZoom = 2.5f,
                ColonyRotationSteps = 2,
                Overlay = "buildings",
            },
        });

        Assert.True(store.TryReadSummary(out var summary));

        Assert.Equal("management", summary!.View.Mode);
        Assert.Equal(120f, summary.View.GlobeYawDegrees);
        Assert.Equal(10f, summary.View.GlobePitchDegrees);
        Assert.Equal(2.5f, summary.View.GlobeZoom);
        Assert.Equal(2, summary.View.ColonyRotationSteps);
        Assert.Equal("buildings", summary.View.Overlay);
    }

    [Fact]
    public void TryReadHeader_ReportsFingerprintAndCounts()
    {
        var store = new CampaignStore(Path.Combine(_root, "slot-a"));
        var meta = new CampaignMeta { Credits = 400, UpdatedUtc = T0 };
        meta.VisitedRegions.Add(1);
        store.SaveMeta(meta, Fingerprint);

        Assert.True(store.TryReadHeader(out var header, out var problem));
        Assert.Null(problem);
        Assert.Equal(Fingerprint, header!.Fingerprint);
        Assert.Equal(400, header.Credits);
        Assert.Equal(1, header.VisitedRegionCount);
        Assert.Equal(T0, header.UpdatedUtc);
    }

    private static BuildingInstance Building(string defId) => new()
    {
        DefId = defId,
        CenterHex = new H3Index(1),
        Footprint = Array.Empty<H3Index>(),
    };
}
