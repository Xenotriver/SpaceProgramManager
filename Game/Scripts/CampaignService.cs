using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using Sim.Campaign;
using Sim.World;

/// <summary>
/// Connects the engine-free campaign to one save slot: loads campaign metadata,
/// installs a region shard before the view opens it, writes every change back
/// immediately, and keeps the derived <c>slot.json</c> summary current. All
/// game-rule validation stays in <see cref="CampaignState"/>.
/// </summary>
public sealed class CampaignService
{
    private readonly BodyWorld _world;
    private readonly CampaignState _campaign;
    private readonly BakeFingerprint _fingerprint;
    private readonly bool _fresh;
    private readonly List<string> _warnings = new();
    private readonly Dictionary<ulong, int> _regionCounts = new();
    private readonly HashSet<ulong> _builtRegions = new();
    private string _slotName;
    private int _buildingTotal;
    private DateTimeOffset _lastPlayedUtc = DateTimeOffset.UtcNow;
    private SlotViewState _viewState = new();

    public CampaignService(
        BodyWorld world,
        CampaignState campaign,
        string saveRoot,
        string slotId,
        string? slotName,
        bool enabled,
        bool fresh)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(slotId);

        _world = world;
        _campaign = campaign;
        _fingerprint = BakeFingerprint.From(world.Manifest);
        _fresh = fresh;
        _slotName = string.IsNullOrWhiteSpace(slotName)
            ? CampaignSlotStore.DefaultName(slotId)
            : slotName;

        SlotId = slotId;
        SaveRoot = Path.GetFullPath(saveRoot);
        Store = enabled ? new CampaignStore(SaveRoot) : null;

        campaign.RegionChanged += OnRegionChanged;
    }

    /// <summary>Directory of the active save slot.</summary>
    public string SaveRoot { get; }

    /// <summary>Slot directory name.</summary>
    public string SlotId { get; }

    /// <summary>Display name of the active slot.</summary>
    public string SlotName => _slotName;

    /// <summary>Store for the active slot, or null when saving is off or unsafe.</summary>
    public CampaignStore? Store { get; private set; }

    /// <summary>Camera/mode snapshot read from <c>slot.json</c>.</summary>
    public SlotViewState ViewState => _viewState;

    /// <summary>One-line save state for the HUD.</summary>
    public string Status { get; private set; } = "save · idle";

    /// <summary>Non-fatal load and save problems, already reported to the HUD.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>
    /// Regions known to hold buildings, including shards not open this session.
    /// Drives the globe's occupied-region markers.
    /// </summary>
    public IReadOnlyCollection<ulong> BuiltRegions => _builtRegions;

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

    /// <summary>Raised for problems the player should see (message, isError).</summary>
    public event Action<string, bool>? Notified;

    /// <summary>
    /// Loads the slot, if it exists and matches the loaded bake. A save that cannot
    /// be understood is left untouched and saving is disabled for the session.
    /// </summary>
    public void Load()
    {
        if (Store is null)
        {
            SetStatus("save · off");
            return;
        }

        if (_fresh)
        {
            StartFresh();
            return;
        }

        if (!Store.Exists)
        {
            SaveMeta();
            SaveSummary();
            SetStatus("save · new campaign");
            return;
        }

        try
        {
            var meta = Store.LoadMeta(_fingerprint);
            _campaign.RestoreMeta(meta);
            _lastPlayedUtc = DateTimeOffset.UtcNow;

            if (Store.TryReadSummary(out var summary))
            {
                _viewState = summary!.View;
                _buildingTotal = summary.BuildingCount;
                if (!string.IsNullOrWhiteSpace(summary.Name))
                    _slotName = summary.Name;
            }
            else
            {
                _buildingTotal = Store.CountStoredBuildings().Buildings;
            }

            SaveSummary();
            SetStatus($"save · loaded · {meta.VisitedRegions.Count} region(s)");
            GD.Print($"campaign: loaded {Store.RootDirectory}");

            _builtRegions.Clear();
            foreach (var index in Store.ListRegionIndices())
                _builtRegions.Add(index);
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // Never overwrite a save we cannot understand.
            Store = null;
            Notify($"campaign not loaded: {ex.Message}");
            SetStatus("save · disabled · incompatible");
        }
    }

    /// <summary>
    /// Installs a region's stored buildings the first time it is opened. Must run
    /// before a view loads the region so it never sees a half-populated state.
    /// </summary>
    public RegionLoadReport? EnsureRegionLoaded(ulong regionIndex)
    {
        if (_campaign.IsLoaded(regionIndex))
            return null;

        if (!_world.TryGetRegionEntry(regionIndex, out _))
            return null;

        var tile = _world.GetRegion(regionIndex);
        var stored = RegionLoadResult.Empty(regionIndex);

        if (Store is not null && !_fresh)
        {
            try
            {
                stored = Store.LoadRegion(regionIndex);
            }
            catch (Exception ex) when (IsStorageError(ex))
            {
                Quarantine(regionIndex);
                Notify($"region {regionIndex}: {ex.Message}");
            }
        }

        var report = _campaign.InstallRegion(stored, tile);

        // The summary counted stored buildings; reconcile with what the live
        // region accepted (dropped records are no longer in play).
        _regionCounts[regionIndex] = report.Loaded;
        _buildingTotal += report.Loaded - stored.Buildings.Count;

        if (report.Loaded > 0)
            _builtRegions.Add(regionIndex);
        else
            _builtRegions.Remove(regionIndex);

        foreach (var warning in report.Warnings)
            Notify($"region {regionIndex}: {warning}");

        if (report.Dropped > 0)
            SetStatus($"save · recovered · {report.Loaded} kept · {report.Dropped} dropped");

        return report;
    }

    /// <summary>Records which region is open so it can be restored next launch.</summary>
    public void SetActiveRegion(ulong? regionIndex)
    {
        if (_campaign.Meta.ActiveRegionIndex == regionIndex)
            return;

        _campaign.Meta.ActiveRegionIndex = regionIndex;
        _campaign.Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        SaveMeta();
        SaveSummary();
    }

    /// <summary>Stores the camera/mode snapshot used by Continue.</summary>
    public void SetViewState(SlotViewState view)
    {
        ArgumentNullException.ThrowIfNull(view);

        _viewState = view;
    }

    /// <summary>Writes metadata and the summary now. Returns false on failure.</summary>
    public bool SaveNow()
    {
        if (Store is null)
        {
            Notify("saving is disabled for this run");
            return false;
        }

        if (!SaveMeta() || !SaveSummary())
            return false;

        SetStatus($"save · manual · {DateTime.Now:HH:mm:ss}");
        return true;
    }

    /// <summary>Writes campaign metadata and the summary; region shards are current.</summary>
    public void Flush()
    {
        if (Store is null)
            return;

        if (SaveMeta())
        {
            SaveSummary();
            GD.Print($"campaign: saved {Store.RootDirectory}");
        }
    }

    /// <summary>
    /// Persists simulation results: every loaded region shard plus time, treasury
    /// and stockpile. Called on a timer while the clock runs and on pause/exit, so
    /// a crash loses at most a few seconds of game time.
    /// </summary>
    public bool SaveSimulation()
    {
        if (Store is null)
            return false;

        try
        {
            foreach (var regionIndex in _campaign.LoadedRegions.OrderBy(index => index))
            {
                var count = _campaign.BuildingCount(regionIndex);
                if (count == 0)
                {
                    Store.DeleteRegion(regionIndex);
                    _builtRegions.Remove(regionIndex);
                }
                else
                {
                    Store.SaveRegion(regionIndex, _campaign.Buildings(regionIndex));
                    _builtRegions.Add(regionIndex);
                }

                _regionCounts[regionIndex] = count;
            }

            if (!SaveMeta())
                return false;

            SaveSummary();
            SetStatus($"save · autosaved · {DateTime.Now:HH:mm:ss}");
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Notify($"save failed: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
            return false;
        }
    }

    private void OnRegionChanged(ulong regionIndex)
    {
        if (Store is null)
            return;

        try
        {
            var count = _campaign.BuildingCount(regionIndex);
            var previous = _regionCounts.TryGetValue(regionIndex, out var known) ? known : 0;
            _buildingTotal += count - previous;
            _regionCounts[regionIndex] = count;

            if (count == 0)
            {
                Store.DeleteRegion(regionIndex);
                _builtRegions.Remove(regionIndex);
            }
            else
            {
                Store.SaveRegion(regionIndex, _campaign.Buildings(regionIndex));
                _builtRegions.Add(regionIndex);
            }

            Store.SaveMeta(_campaign.Meta, _fingerprint);
            SaveSummary();

            var action = count == 0 ? "cleared" : $"saved · {count} building(s)";
            SetStatus($"save · {action} · {DateTime.Now:HH:mm:ss}");
            GD.Print($"save: region {regionIndex} · {count} building(s)");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Notify($"save failed: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
        }
    }

    private void StartFresh()
    {
        try
        {
            var removed = Store!.DeleteAllRegions();
            _buildingTotal = 0;
            _regionCounts.Clear();
            _builtRegions.Clear();
            SaveMeta();
            SaveSummary();

            SetStatus(removed > 0
                ? $"save · new campaign · cleared {removed} shard(s)"
                : "save · new campaign");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Notify($"could not reset slot: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
        }
    }

    private bool SaveMeta()
    {
        if (Store is null)
            return false;

        try
        {
            _campaign.Meta.UpdatedUtc = DateTimeOffset.UtcNow;
            Store.SaveMeta(_campaign.Meta, _fingerprint);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Notify($"save failed: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
            return false;
        }
    }

    private bool SaveSummary()
    {
        if (Store is null)
            return false;

        try
        {
            Store.SaveSummary(new SlotSummary
            {
                Id = SlotId,
                Name = _slotName,
                Path = SaveRoot,
                CreatedUtc = _campaign.Meta.CreatedUtc,
                UpdatedUtc = _campaign.Meta.UpdatedUtc,
                LastPlayedUtc = _lastPlayedUtc,
                BuildingCount = _buildingTotal,
                VisitedRegionCount = _campaign.Meta.VisitedRegions.Count,
                BodyKey = _fingerprint.BodyKey,
                Credits = _campaign.Meta.Credits,
                View = _viewState,
            });
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Notify($"slot summary failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>Moves an unreadable shard aside so a later save cannot overwrite it.</summary>
    private void Quarantine(ulong regionIndex)
    {
        if (Store is null)
            return;

        var path = Store.RegionPath(regionIndex);
        if (!File.Exists(path))
            return;

        try
        {
            File.Move(path, path + ".corrupt", overwrite: true);
        }
        catch (IOException)
        {
            // Quarantine is best-effort; the warning already tells the player.
        }
    }

    private void Notify(string message)
    {
        _warnings.Add(message);
        GD.PushWarning($"campaign: {message}");
        Notified?.Invoke(message, true);
    }

    private void SetStatus(string status)
    {
        if (Status == status)
            return;

        Status = status;
        StatusChanged?.Invoke();
    }

    private static bool IsStorageError(Exception ex)
        => ex is InvalidDataException or IOException or UnauthorizedAccessException;
}
