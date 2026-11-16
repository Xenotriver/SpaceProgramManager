using System.Globalization;

namespace Sim.Campaign;

/// <summary>
/// Catalogue of save slots under one save root. A slot is a direct child
/// directory containing <c>campaign.json</c>; <c>slot.json</c> inside it is a
/// rebuildable cache. Engine-free so the front end never parses saves itself.
/// </summary>
public sealed class CampaignSlotStore
{
    /// <summary>Directory name used by the pre-M2 default save, still supported.</summary>
    public const string DefaultSlotId = "slot1";

    public CampaignSlotStore(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);

        RootDirectory = Path.GetFullPath(rootDirectory);
    }

    /// <summary>Directory holding every slot.</summary>
    public string RootDirectory { get; }

    /// <summary>Directory of the legacy default slot.</summary>
    public string DefaultSlotDirectory => Path.Combine(RootDirectory, DefaultSlotId);

    /// <summary>Lists readable slots, newest last-played first.</summary>
    public IReadOnlyList<SlotSummary> List(BakeFingerprint expected)
    {
        var slots = new List<SlotSummary>();

        if (!Directory.Exists(RootDirectory))
            return slots;

        foreach (var directory in Directory.EnumerateDirectories(RootDirectory))
        {
            var id = Path.GetFileName(directory);
            if (string.IsNullOrWhiteSpace(id))
                continue;

            var summary = ReadSummary(id, expected, heal: true);
            if (summary is not null)
                slots.Add(summary);
        }

        return slots
            .OrderByDescending(slot => slot.LastPlayedUtc)
            .ThenBy(slot => slot.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Returns a slot's summary, rebuilding <c>slot.json</c> if needed.</summary>
    public SlotSummary? TryGet(string id, BakeFingerprint expected)
        => ReadSummary(SafeId(id), expected, heal: true);

    /// <summary>
    /// Creates a new slot directory, writes its campaign metadata and summary,
    /// and returns the generated slot id.
    /// </summary>
    public string Create(string name, CampaignMeta meta, BakeFingerprint expected)
    {
        ArgumentNullException.ThrowIfNull(meta);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        Directory.CreateDirectory(RootDirectory);

        var id = NewId();
        var directory = Path.Combine(RootDirectory, id);
        Directory.CreateDirectory(directory);

        var store = new CampaignStore(directory);
        store.SaveMeta(meta, expected);

        var now = DateTimeOffset.UtcNow;
        var summary = new SlotSummary
        {
            Id = id,
            Name = name.Trim(),
            Path = directory,
            CreatedUtc = meta.CreatedUtc,
            UpdatedUtc = meta.UpdatedUtc,
            LastPlayedUtc = now,
            BuildingCount = 0,
            VisitedRegionCount = meta.VisitedRegions.Count,
            BodyKey = expected.BodyKey,
            Credits = meta.Credits,
        };

        store.SaveSummary(summary);
        return id;
    }

    /// <summary>Deletes a slot directory and everything in it.</summary>
    public bool Delete(string id)
    {
        var directory = Path.Combine(RootDirectory, SafeId(id));

        if (!Directory.Exists(directory))
            return false;

        Directory.Delete(directory, recursive: true);
        return true;
    }

    /// <summary>Writes a summary for a slot that already exists.</summary>
    public void SaveSummary(string id, SlotSummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);

        new CampaignStore(Path.Combine(RootDirectory, SafeId(id))).SaveSummary(summary);
    }

    /// <summary>Default display name for a slot without a summary.</summary>
    public static string DefaultName(string id)
        => string.Equals(id, DefaultSlotId, StringComparison.Ordinal) ? "Slot 1" : id;

    private SlotSummary? ReadSummary(string id, BakeFingerprint expected, bool heal)
    {
        var directory = Path.Combine(RootDirectory, id);
        var store = new CampaignStore(directory);

        if (!store.Exists)
            return null;

        var readable = store.TryReadHeader(out var header, out var problem);
        var state = readable
            ? header!.Fingerprint == expected ? SlotState.Ok : SlotState.Incompatible
            : SlotState.Unreadable;

        store.TryReadSummary(out var cached);
        var name = cached?.Name ?? DefaultName(id);

        // The summary is trusted while its timestamp matches campaign.json.
        if (cached is not null && readable && cached.UpdatedUtc == header!.UpdatedUtc)
        {
            return cached with
            {
                Id = id,
                Name = name,
                Path = directory,
                State = state,
                Problem = state == SlotState.Ok ? null : problem ?? FingerprintProblem(header.Fingerprint, expected),
            };
        }

        var counts = store.CountStoredBuildings();
        var summary = new SlotSummary
        {
            Id = id,
            Name = name,
            Path = directory,
            CreatedUtc = header?.CreatedUtc ?? cached?.CreatedUtc ?? default,
            UpdatedUtc = header?.UpdatedUtc ?? cached?.UpdatedUtc ?? default,
            LastPlayedUtc = cached?.LastPlayedUtc ?? header?.UpdatedUtc ?? default,
            BuildingCount = counts.Buildings,
            VisitedRegionCount = header?.VisitedRegionCount ?? cached?.VisitedRegionCount ?? 0,
            BodyKey = header?.Fingerprint.BodyKey ?? cached?.BodyKey ?? string.Empty,
            Credits = header?.Credits ?? cached?.Credits ?? 0,
            State = state,
            Problem = state == SlotState.Ok
                ? null
                : problem ?? FingerprintProblem(header!.Fingerprint, expected),
            View = cached?.View ?? new SlotViewState(),
        };

        if (heal && state != SlotState.Unreadable)
            TrySave(store, summary);

        return summary;
    }

    private static void TrySave(CampaignStore store, SlotSummary summary)
    {
        try
        {
            store.SaveSummary(summary);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The cache is optional; a failed write only costs a rebuild later.
        }
    }

    private static string FingerprintProblem(BakeFingerprint stored, BakeFingerprint expected)
        => $"save was created for [{stored}], but the loaded bake is [{expected}]";

    private static string SafeId(string id)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        if (id is "." or ".."
            || Path.IsPathRooted(id)
            || !string.Equals(Path.GetFileName(id), id, StringComparison.Ordinal))
            throw new ArgumentException($"'{id}' is not a valid slot id.", nameof(id));

        return id;
    }

    private string NewId()
    {
        var stem = "slot-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var id = stem;

        for (var suffix = 2; Directory.Exists(Path.Combine(RootDirectory, id)); suffix++)
            id = stem + "-" + suffix.ToString(CultureInfo.InvariantCulture);

        return id;
    }
}
