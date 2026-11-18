using System.Text;
using System.Text.Json;
using H3;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Hex;
using Sim.Site;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Tests for the engine-free campaign store: JSON round-trips, schema guards,
/// atomic-write backups and non-fatal record problems.
/// </summary>
public class CampaignStoreTests : IDisposable
{
    private static readonly BakeFingerprint Fingerprint =
        new("moon", 6, 3, 41162, "2026-11-11T21:48:07+00:00");

    private static readonly H3Index CenterHex =
        HexGrid.FromLatLon(0.41590091884368713, 0.23004777276206262, 6);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "spm-store-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void Meta_RoundTrips()
    {
        var store = new CampaignStore(_root);
        var meta = new CampaignMeta
        {
            GameTimeHours = 123.5,
            Credits = 2_500_000,
            ActiveRegionIndex = 589971619905011711,
        };
        meta.Stockpile["metals"] = 12.5;
        meta.VisitedRegions.Add(589971619905011711);
        meta.VisitedRegions.Add(589971619905011712);

        store.SaveMeta(meta, Fingerprint);
        var loaded = store.LoadMeta(Fingerprint);

        Assert.Equal(meta.GameTimeHours, loaded.GameTimeHours);
        Assert.Equal(meta.Credits, loaded.Credits);
        Assert.Equal(meta.ActiveRegionIndex, loaded.ActiveRegionIndex);
        Assert.Equal(12.5, loaded.Stockpile["metals"]);
        Assert.Equal(meta.VisitedRegions.OrderBy(i => i), loaded.VisitedRegions.OrderBy(i => i));
        Assert.Equal(meta.CreatedUtc, loaded.CreatedUtc);
    }

    [Fact]
    public void Meta_RejectsFingerprintMismatch()
    {
        var store = new CampaignStore(_root);
        store.SaveMeta(new CampaignMeta(), Fingerprint);

        var wrong = Fingerprint with { RegionCount = Fingerprint.RegionCount + 1 };
        var error = Assert.Throws<InvalidDataException>(() => store.LoadMeta(wrong));

        Assert.Contains("loaded bake", error.Message);
    }

    [Fact]
    public void Meta_RejectsCrossBodyFingerprintWithoutWritingShard()
    {
        var store = new CampaignStore(_root);
        store.SaveMeta(new CampaignMeta(), Fingerprint);
        var before = File.ReadAllBytes(store.MetaPath);

        var marsBake = Fingerprint with
        {
            BodyKey = "mars",
            GeneratedUtc = "2026-11-18T19:35:34+00:00",
        };
        var error = Assert.Throws<InvalidDataException>(() => store.LoadMeta(marsBake));

        Assert.Contains("moon", error.Message);
        Assert.Contains("mars", error.Message);
        Assert.Equal(before, File.ReadAllBytes(store.MetaPath));
        Assert.Empty(store.ListRegionIndices());
        Assert.False(Directory.Exists(Path.Combine(store.RootDirectory, "regions")));
    }

    [Fact]
    public void Meta_RejectsUnsupportedVersion()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.MetaPath, """
            { "format": "spm-campaign", "version": 99, "bodyKey": "moon" }
            """);

        var error = Assert.Throws<InvalidDataException>(() => store.LoadMeta(Fingerprint));

        Assert.Contains("not supported", error.Message);
    }

    [Fact]
    public void Meta_RejectsUnknownFormat()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.MetaPath, """
            { "format": "other-save", "version": 1 }
            """);

        var error = Assert.Throws<InvalidDataException>(() => store.LoadMeta(Fingerprint));

        Assert.Contains("expected format", error.Message);
    }

    [Fact]
    public void Meta_MigratesV1SaveToCurrentDefaults()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(_root);
        File.WriteAllText(store.MetaPath, $$"""
            {
              "format": "spm-campaign",
              "version": 1,
              "bodyKey": "{{Fingerprint.BodyKey}}",
              "fineResolution": {{Fingerprint.FineResolution}},
              "regionResolution": {{Fingerprint.RegionResolution}},
              "regionCount": {{Fingerprint.RegionCount}},
              "generatedUtc": "{{Fingerprint.GeneratedUtc}}",
              "createdUtc": "2026-11-11T21:48:07+00:00",
              "updatedUtc": "2026-11-11T22:00:00+00:00",
              "gameTimeHours": 12.5,
              "credits": 0,
              "stockpile": {},
              "unlockedTechs": [],
              "visitedRegions": ["123"],
              "activeRegion": "123"
            }
            """);

        var loaded = store.LoadMeta(Fingerprint);

        Assert.Equal(12.5, loaded.GameTimeHours);
        Assert.Equal(CampaignDefaults.StartingCredits, loaded.Credits);
        Assert.Equal(123UL, Assert.Single(loaded.VisitedRegions));
        Assert.Equal(123UL, loaded.ActiveRegionIndex);
        Assert.Equal(CampaignDefaults.StartingStockpile["oxygen"], loaded.Stockpile["oxygen"]);
        Assert.Equal(
            CampaignDefaults.DeterministicSeed(loaded.CreatedUtc),
            loaded.RngSeed);
        Assert.Equal(loaded.RngSeed, loaded.RngState);
    }

    [Fact]
    public void Meta_RoundTripsSimulationFields()
    {
        var store = new CampaignStore(_root);
        var meta = new CampaignMeta
        {
            SciencePoints = 321.5,
            RngSeed = 12345,
            RngState = 67890,
        };
        meta.UnlockedTechs.Add("nuclear_power");

        store.SaveMeta(meta, Fingerprint);
        var loaded = store.LoadMeta(Fingerprint);

        Assert.Equal(meta.SciencePoints, loaded.SciencePoints);
        Assert.Equal(meta.RngSeed, loaded.RngSeed);
        Assert.Equal(meta.RngState, loaded.RngState);
        Assert.Equal(new[] { "nuclear_power" }, loaded.UnlockedTechs);
    }

    [Fact]
    public void Region_RoundTripsBuildings()
    {
        var store = new CampaignStore(_root);
        var id = Guid.NewGuid();
        var building = new BuildingInstance
        {
            Id = id,
            DefId = "rtg_generator",
            ParentHex = CenterHex,
            LocalCenter = LocalGrid.Center,
            LocalFootprint = new[] { LocalGrid.Center },
            Progress = 0.75,
            Health = 42.0,
            Status = BuildingStatus.Operational,
            Paused = true,
            StatusReason = "maintenance unpaid",
            ConstructionStartTime = 10.0,
            ConstructionCompleteTime = 34.0,
        };
        building.StoredResources["power"] = 3.5;
        building.State["fuel"] = 12.0;
        building.State["mode"] = "idle";
        building.State["sealed"] = true;

        store.SaveRegion(123, new[] { building });
        var loaded = store.LoadRegion(123);

        Assert.Empty(loaded.Warnings);
        var restored = Assert.Single(loaded.Buildings);
        Assert.Equal(id, restored.Id);
        Assert.Equal("rtg_generator", restored.DefId);
        Assert.Equal((ulong)CenterHex, (ulong)restored.ParentHex);
        Assert.Equal(LocalGrid.Center, restored.LocalCenter);
        Assert.Empty(restored.LocalFootprint);
        Assert.Equal(0.75, restored.Progress);
        Assert.Equal(42.0, restored.Health);
        Assert.Equal(BuildingStatus.Operational, restored.Status);
        Assert.True(restored.Paused);
        Assert.Equal("maintenance unpaid", restored.StatusReason);
        Assert.Equal(10.0, restored.ConstructionStartTime);
        Assert.Equal(34.0, restored.ConstructionCompleteTime);
        Assert.Equal(3.5, restored.StoredResources["power"]);
        Assert.Equal(12.0, restored.State["fuel"]);
        Assert.Equal("idle", restored.State["mode"]);
        Assert.Equal(true, restored.State["sealed"]);
    }

    [Fact]
    public void Region_KeepsUnknownDefinitionIds()
    {
        var store = new CampaignStore(_root);
        store.SaveRegion(123, new[]
        {
            new BuildingInstance
            {
                DefId = "from_a_mod",
                ParentHex = CenterHex,
                LocalCenter = LocalGrid.Center,
                LocalFootprint = Array.Empty<LocalCell>(),
            },
        });

        var loaded = store.LoadRegion(123);

        Assert.Equal("from_a_mod", Assert.Single(loaded.Buildings).DefId);
    }

    [Fact]
    public void Region_MissingShardIsEmpty()
    {
        var store = new CampaignStore(_root);

        var loaded = store.LoadRegion(999);

        Assert.Empty(loaded.Buildings);
        Assert.Empty(loaded.Warnings);
        Assert.Equal(999UL, loaded.RegionIndex);
    }

    [Fact]
    public void Region_KeepsPreviousFileAsBackup()
    {
        var store = new CampaignStore(_root);
        store.SaveRegion(123, new[]
        {
            new BuildingInstance
            {
                DefId = "first",
                ParentHex = CenterHex,
                LocalCenter = LocalGrid.Center,
                LocalFootprint = Array.Empty<LocalCell>(),
            },
        });
        store.SaveRegion(123, new[]
        {
            new BuildingInstance
            {
                DefId = "second",
                ParentHex = CenterHex,
                LocalCenter = LocalGrid.Center,
                LocalFootprint = Array.Empty<LocalCell>(),
            },
        });

        var backup = File.ReadAllText(store.RegionPath(123) + ".bak", Encoding.UTF8);
        var current = File.ReadAllText(store.RegionPath(123), Encoding.UTF8);

        Assert.Contains("first", backup);
        Assert.Contains("second", current);
    }

    [Fact]
    public void Region_RejectsCorruptJson()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), "{ not json");

        var error = Assert.Throws<InvalidDataException>(() => store.LoadRegion(123));

        Assert.Contains("invalid JSON", error.Message);
    }

    [Fact]
    public void Region_RejectsMismatchedRegionIndex()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), """
            { "format": "spm-campaign", "version": 1, "region": "456", "buildings": [] }
            """);

        var error = Assert.Throws<InvalidDataException>(() => store.LoadRegion(123));

        Assert.Contains("does not match", error.Message);
    }

    [Fact]
    public void Region_DropsUnsupportedStateValues()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), $$"""
            {
              "format": "spm-campaign",
              "version": 1,
              "region": "123",
              "buildings": [
                {
                  "defId": "odd",
                  "centerHex": "{{(ulong)CenterHex}}",
                  "status": "Operational",
                  "state": { "nested": { "a": 1 }, "fine": 2.0 }
                }
              ]
            }
            """);

        var loaded = store.LoadRegion(123);

        var building = Assert.Single(loaded.Buildings);
        Assert.Equal(2.0, building.State["fine"]);
        Assert.False(building.State.ContainsKey("nested"));
        Assert.Contains(loaded.Warnings, warning => warning.Contains("dropped state 'nested'"));
    }

    [Fact]
    public void SaveRegion_CreatesDirectories()
    {
        var store = new CampaignStore(_root);
        store.SaveRegion(123, Array.Empty<BuildingInstance>());

        Assert.True(File.Exists(store.RegionPath(123)));
        Assert.False(File.Exists(store.RegionPath(123) + ".tmp"));
    }

    [Fact]
    public void DeleteRegion_MissingShardAndBackupIsNoOp()
    {
        var store = new CampaignStore(Path.Combine(_root, "fresh"));

        store.DeleteRegion(123);

        Assert.False(Directory.Exists(Path.Combine(store.RootDirectory, "regions")));
    }

    [Fact]
    public void DeleteRegion_RemovesShardAndBackup()
    {
        var store = new CampaignStore(_root);
        store.SaveRegion(123, Array.Empty<BuildingInstance>());
        store.SaveRegion(123, Array.Empty<BuildingInstance>());

        store.DeleteRegion(123);

        Assert.False(File.Exists(store.RegionPath(123)));
        Assert.False(File.Exists(store.RegionPath(123) + ".bak"));
    }

    [Fact]
    public void DeleteAllRegions_RemovesShardsAndBackups()
    {
        var store = new CampaignStore(_root);
        store.SaveRegion(1, Array.Empty<BuildingInstance>());
        store.SaveRegion(1, Array.Empty<BuildingInstance>());
        store.SaveRegion(2, Array.Empty<BuildingInstance>());

        var removed = store.DeleteAllRegions();

        Assert.Equal(2, removed);
        Assert.False(File.Exists(store.RegionPath(1)));
        Assert.False(File.Exists(store.RegionPath(1) + ".bak"));
        Assert.False(File.Exists(store.RegionPath(2)));
    }

    [Fact]
    public void Region_V3RoundTripsTwoInstallationsInOneSite()
    {
        var store = new CampaignStore(_root);
        var parent = new H3Index(12345);
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var buildings = new[]
        {
            new BuildingInstance
            {
                Id = firstId,
                DefId = "rtg_generator",
                ParentHex = parent,
                LocalCenter = new LocalCell(0, 0),
                LocalFootprint = new[] { LocalGrid.Center },
            },
            new BuildingInstance
            {
                Id = secondId,
                DefId = "habitat",
                ParentHex = parent,
                LocalCenter = new LocalCell(1, 0),
                LocalFootprint = new[] { new LocalCell(1, 0) },
            },
        };

        store.SaveRegion(123, buildings);
        var json = File.ReadAllText(store.RegionPath(123));
        Assert.Contains("\"version\": 3", json);
        Assert.Contains("\"sites\"", json);
        Assert.Contains("\"localCenter\"", json);
        Assert.DoesNotContain("\"footprint\"", json);

        var loaded = store.LoadRegion(123);

        Assert.Empty(loaded.Warnings);
        Assert.Equal(2, loaded.Buildings.Count);
        Assert.Equal(firstId, loaded.Buildings[0].Id);
        Assert.Equal(new LocalCell(0, 0), loaded.Buildings[0].LocalCenter);
        Assert.Equal(secondId, loaded.Buildings[1].Id);
        Assert.Equal(new LocalCell(1, 0), loaded.Buildings[1].LocalCenter);
        Assert.All(loaded.Buildings, building =>
        {
            Assert.Equal((ulong)parent, (ulong)building.ParentHex);
            Assert.Empty(building.LocalFootprint);
        });
        Assert.Equal(2, store.CountStoredBuildings().Buildings);
    }

    [Fact]
    public void Region_V3MissingSitesIsRejected()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), """
            { "format": "spm-campaign", "version": 3, "region": "123" }
            """);

        var error = Assert.Throws<InvalidDataException>(() => store.LoadRegion(123));

        Assert.Contains("missing 'sites'", error.Message);
    }

    [Fact]
    public void Region_V3LegacyBuildingsAreRejected()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), $$"""
            {
              "format": "spm-campaign",
              "version": 3,
              "region": "123",
              "sites": [],
              "buildings": [
                { "defId": "rtg_generator", "centerHex": "{{(ulong)CenterHex}}" }
              ]
            }
            """);

        var error = Assert.Throws<InvalidDataException>(() => store.LoadRegion(123));

        Assert.Contains("legacy 'buildings'", error.Message);
    }

    [Fact]
    public void Region_V3NullInstallationsAreTolerated()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), $$"""
            {
              "format": "spm-campaign",
              "version": 3,
              "region": "123",
              "sites": [
                { "parentHex": "{{(ulong)CenterHex}}", "installations": null }
              ]
            }
            """);

        var loaded = store.LoadRegion(123);

        Assert.Empty(loaded.Buildings);
        Assert.Single(loaded.Warnings);
        Assert.Contains("missing installations", loaded.Warnings[0]);
    }

    [Fact]
    public void Region_MigratesV2ShardToOneSitePerCentreHex()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        var secondHex = (ulong)CenterHex + 1;
        File.WriteAllText(store.RegionPath(123), $$"""
            {
              "format": "spm-campaign",
              "version": 2,
              "region": "123",
              "buildings": [
                {
                  "id": "11111111-1111-1111-1111-111111111111",
                  "defId": "rtg_generator",
                  "centerHex": "{{(ulong)CenterHex}}",
                  "progress": 0.75,
                  "health": 42.0,
                  "status": "Operational",
                  "paused": true,
                  "statusReason": "maintenance unpaid",
                  "storedResources": { "power": 3.5 },
                  "constructionStartTime": 10.0,
                  "constructionCompleteTime": 34.0,
                  "state": { "fuel": 12.0, "mode": "idle", "sealed": true }
                },
                {
                  "defId": "habitat",
                  "centerHex": "{{secondHex}}",
                  "status": "Idle"
                }
              ]
            }
            """);

        var loaded = store.LoadRegion(123);

        Assert.Empty(loaded.Warnings);
        Assert.Equal(2, loaded.Buildings.Count);
        var generator = loaded.Buildings[0];
        Assert.Equal(Guid.Parse("11111111-1111-1111-1111-111111111111"), generator.Id);
        Assert.Equal("rtg_generator", generator.DefId);
        Assert.Equal((ulong)CenterHex, (ulong)generator.ParentHex);
        Assert.Equal(LocalGrid.Center, generator.LocalCenter);
        Assert.Equal(0.75, generator.Progress);
        Assert.Equal(42.0, generator.Health);
        Assert.Equal(BuildingStatus.Operational, generator.Status);
        Assert.True(generator.Paused);
        Assert.Equal("maintenance unpaid", generator.StatusReason);
        Assert.Equal(3.5, generator.StoredResources["power"]);
        Assert.Equal(10.0, generator.ConstructionStartTime);
        Assert.Equal(34.0, generator.ConstructionCompleteTime);
        Assert.Equal(12.0, generator.State["fuel"]);
        Assert.Equal("idle", generator.State["mode"]);
        Assert.Equal(true, generator.State["sealed"]);
        Assert.Equal(secondHex, (ulong)loaded.Buildings[1].ParentHex);
        Assert.Equal(BuildingStatus.Idle, loaded.Buildings[1].Status);
        Assert.Equal(LocalGrid.Center, loaded.Buildings[1].LocalCenter);

        // A subsequent save upgrades the shard without losing either site.
        store.SaveRegion(123, loaded.Buildings);
        var upgraded = store.LoadRegion(123);
        Assert.Equal(2, upgraded.Buildings.Count);
        Assert.Contains("\"version\": 3", File.ReadAllText(store.RegionPath(123)));
    }

    [Fact]
    public void Region_MigratesV1ShardWithTheSameShape()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), $$"""
            {
              "format": "spm-campaign",
              "version": 1,
              "region": "123",
              "buildings": [
                { "defId": "old_generator", "centerHex": "{{(ulong)CenterHex}}", "status": "Operational" }
              ]
            }
            """);

        var loaded = store.LoadRegion(123);

        var building = Assert.Single(loaded.Buildings);
        Assert.Equal("old_generator", building.DefId);
        Assert.Equal((ulong)CenterHex, (ulong)building.ParentHex);
        Assert.Equal(LocalGrid.Center, building.LocalCenter);
        Assert.Equal(BuildingStatus.Operational, building.Status);
    }

    [Fact]
    public void Region_RejectsUnsupportedVersion()
    {
        var store = new CampaignStore(_root);
        Directory.CreateDirectory(Path.Combine(_root, "regions"));
        File.WriteAllText(store.RegionPath(123), $$"""
            { "format": "spm-campaign", "version": 99, "region": "123" }
            """);

        var error = Assert.Throws<InvalidDataException>(() => store.LoadRegion(123));

        Assert.Contains("not supported", error.Message);
    }

    [Fact]
    public void SlotView_RoundTripsSiteSelection()
    {
        var store = new CampaignStore(_root);
        store.SaveSummary(new SlotSummary
        {
            Id = "slot",
            Name = "Slot",
            View = new SlotViewState { Mode = "site", SelectedHexOrdinal = 7 },
        });

        Assert.True(store.TryReadSummary(out var summary));
        Assert.Equal("site", summary!.View.Mode);
        Assert.Equal(7, summary.View.SelectedHexOrdinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
                Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
            // Temp cleanup is best-effort.
        }
    }
}
