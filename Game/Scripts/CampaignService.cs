using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using Sim.Campaign;
using Sim.World;

/// <summary>
/// Connects the engine-free campaign to a save slot: loads campaign metadata,
/// installs a region shard before the view opens it, and writes every change back
/// immediately. All game-rule validation stays in <see cref="CampaignState"/>.
/// </summary>
public sealed class CampaignService
{
    private readonly BodyWorld _world;
    private readonly CampaignState _campaign;
    private readonly BakeFingerprint _fingerprint;
    private readonly bool _fresh;
    private readonly List<string> _warnings = new();

    public CampaignService(BodyWorld world, CampaignState campaign, string saveRoot, bool enabled, bool fresh)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentException.ThrowIfNullOrWhiteSpace(saveRoot);

        _world = world;
        _campaign = campaign;
        _fingerprint = BakeFingerprint.From(world.Manifest);
        _fresh = fresh;

        SaveRoot = Path.GetFullPath(saveRoot);
        Store = enabled ? new CampaignStore(SaveRoot) : null;

        campaign.RegionChanged += OnRegionChanged;
    }

    /// <summary>Directory of the active save slot.</summary>
    public string SaveRoot { get; }

    /// <summary>Store for the active slot, or null when saving is off or unsafe.</summary>
    public CampaignStore? Store { get; private set; }

    /// <summary>One-line save state for the HUD.</summary>
    public string Status { get; private set; } = "save · idle";

    /// <summary>Non-fatal load and save problems, already reported to the HUD.</summary>
    public IReadOnlyList<string> Warnings => _warnings;

    /// <summary>Raised whenever <see cref="Status"/> changes.</summary>
    public event Action? StatusChanged;

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
            SetStatus("save · new campaign");
            return;
        }

        try
        {
            var meta = Store.LoadMeta(_fingerprint);
            _campaign.RestoreMeta(meta);
            SetStatus($"save · loaded · {meta.VisitedRegions.Count} region(s)");
            GD.Print($"campaign: loaded {Store.RootDirectory}");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            // Never overwrite a save we cannot understand.
            Store = null;
            Warn($"campaign not loaded: {ex.Message}");
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
                Warn($"region {regionIndex}: {ex.Message}");
            }
        }

        var report = _campaign.InstallRegion(stored, tile);

        foreach (var warning in report.Warnings)
            Warn($"region {regionIndex}: {warning}");

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
    }

    /// <summary>Writes campaign metadata; region shards are already current.</summary>
    public void Flush()
    {
        if (Store is null)
            return;

        if (SaveMeta())
            GD.Print($"campaign: saved {Store.RootDirectory}");
    }

    private void OnRegionChanged(ulong regionIndex)
    {
        if (Store is null)
            return;

        try
        {
            var count = _campaign.BuildingCount(regionIndex);

            if (count == 0)
                Store.DeleteRegion(regionIndex);
            else
                Store.SaveRegion(regionIndex, _campaign.Buildings(regionIndex));

            Store.SaveMeta(_campaign.Meta, _fingerprint);

            var action = count == 0 ? "cleared" : $"saved · {count} building(s)";
            SetStatus($"save · {action} · {DateTime.Now:HH:mm:ss}");
            GD.Print($"save: region {regionIndex} · {count} building(s)");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Warn($"save failed: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
        }
    }

    private void StartFresh()
    {
        try
        {
            var removed = Store!.DeleteAllRegions();
            SaveMeta();

            SetStatus(removed > 0
                ? $"save · new campaign · cleared {removed} shard(s)"
                : "save · new campaign");
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Warn($"could not reset slot: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
        }
    }

    private bool SaveMeta()
    {
        if (Store is null)
            return false;

        try
        {
            Store.SaveMeta(_campaign.Meta, _fingerprint);
            return true;
        }
        catch (Exception ex) when (IsStorageError(ex))
        {
            Warn($"save failed: {ex.Message}");
            SetStatus($"save · failed · {ex.Message}");
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

    private void Warn(string message)
    {
        _warnings.Add(message);
        GD.PushWarning($"campaign: {message}");
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
