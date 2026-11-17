namespace Sim.Campaign;

/// <summary>How a slot's <c>campaign.json</c> relates to the loaded bake.</summary>
public enum SlotState
{
    /// <summary>Readable and created against the loaded bake.</summary>
    Ok,

    /// <summary>Readable, but its bake fingerprint does not match the loaded one.</summary>
    Incompatible,

    /// <summary>Present but its metadata cannot be understood.</summary>
    Unreadable,
}

/// <summary>
/// Last view the player had open in a slot. UI state only: it never affects game
/// rules and losing it only costs a camera reset.
/// </summary>
public sealed record SlotViewState
{
    /// <summary>One of <c>globe</c>, <c>region</c>, <c>management</c>, <c>site</c>.</summary>
    public string Mode { get; init; } = "globe";

    /// <summary>Ordinal of the fine hex whose site is open; null outside site view.</summary>
    public int? SelectedHexOrdinal { get; init; }

    /// <summary>Globe yaw in degrees.</summary>
    public float GlobeYawDegrees { get; init; } = -90f;

    /// <summary>Globe pitch in degrees.</summary>
    public float GlobePitchDegrees { get; init; } = -14.3f;

    /// <summary>Globe zoom factor.</summary>
    public float GlobeZoom { get; init; } = 1f;

    /// <summary>Colony-camera rotation in 60-degree steps.</summary>
    public int ColonyRotationSteps { get; init; }

    /// <summary>Overlay id: <c>biome</c>, <c>terrain</c>, <c>siteScore</c>, <c>buildings</c>.</summary>
    public string Overlay { get; init; } = "terrain";
}

/// <summary>
/// A save slot as the front end needs to see it. Built from <c>campaign.json</c>
/// plus the derived <c>slot.json</c> cache; never authoritative for game state.
/// </summary>
public sealed record SlotSummary
{
    /// <summary>Directory name under the save root.</summary>
    public required string Id { get; init; }

    /// <summary>Display name; falls back to the directory name.</summary>
    public required string Name { get; init; }

    /// <summary>Absolute slot directory.</summary>
    public string Path { get; init; } = string.Empty;

    /// <summary>Campaign creation timestamp.</summary>
    public DateTimeOffset CreatedUtc { get; init; }

    /// <summary>Last time the campaign changed.</summary>
    public DateTimeOffset UpdatedUtc { get; init; }

    /// <summary>Last time the slot was opened, used for last-played ordering.</summary>
    public DateTimeOffset LastPlayedUtc { get; init; }

    /// <summary>Buildings across all stored region shards.</summary>
    public int BuildingCount { get; init; }

    /// <summary>Regions opened at least once.</summary>
    public int VisitedRegionCount { get; init; }

    /// <summary>Body the campaign belongs to.</summary>
    public string BodyKey { get; init; } = string.Empty;

    /// <summary>Treasury, shown as a list detail.</summary>
    public double Credits { get; init; }

    /// <summary>Compatibility with the loaded bake.</summary>
    public SlotState State { get; init; }

    /// <summary>Human-readable reason when <see cref="State"/> is not <see cref="SlotState.Ok"/>.</summary>
    public string? Problem { get; init; }

    /// <summary>Camera/mode snapshot for Continue.</summary>
    public SlotViewState View { get; init; } = new();
}
