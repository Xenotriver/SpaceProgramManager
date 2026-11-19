namespace Sim.Campaign;

/// <summary>Lifecycle of a campaign contract.</summary>
public enum ContractStatus
{
    Active,
    Completed,
    Failed,
}

/// <summary>Current state of a rover in the campaign fleet.</summary>
public enum RoverStatus
{
    Idle,
    Docked,
    Enroute,
    Disabled,
}

/// <summary>
/// Persistent progress for one accepted contract. Definition ids and rewards stay
/// in static registries; the save stores only what the player's campaign changed.
/// </summary>
public sealed class CampaignContract
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    public string DefId { get; set; } = string.Empty;

    public ContractStatus Status { get; set; } = ContractStatus.Active;

    public double AcceptedGameHour { get; set; }

    public double? DeadlineGameHour { get; set; }

    /// <summary>Resource or counter id → delivered amount.</summary>
    public Dictionary<string, double> Progress { get; init; } = new(StringComparer.Ordinal);

    public bool RewardClaimed { get; set; }
}

/// <summary>An event awaiting a player choice.</summary>
public sealed class PendingCampaignEvent
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    public string DefId { get; set; } = string.Empty;

    public double RaisedGameHour { get; set; }

    /// <summary>Decimal H3 region index, when the event is region-scoped.</summary>
    public ulong? TargetRegion { get; set; }

    public List<string> Choices { get; init; } = new();
}

/// <summary>One bounded entry in the campaign's event history.</summary>
public sealed class CampaignEventLogEntry
{
    public double GameHour { get; set; }

    public string DefId { get; set; } = string.Empty;

    public string? Choice { get; set; }

    public string? Summary { get; set; }
}

/// <summary>
/// Persistent state for one rover. Rovers are campaign-wide because they move
/// between region shards that are loaded on demand.
/// </summary>
public sealed class RoverState
{
    public string Id { get; set; } = Guid.NewGuid().ToString("D");

    public string Name { get; set; } = "Rover";

    public string DefId { get; set; } = string.Empty;

    public RoverStatus Status { get; set; } = RoverStatus.Idle;

    public double Health { get; set; } = CampaignDefaults.DefaultRoverHealth;

    public double SpeedKmh { get; set; } = CampaignDefaults.DefaultRoverSpeedKmh;

    /// <summary>Decimal H3 region index where the rover currently sits.</summary>
    public ulong? LocationRegion { get; set; }

    /// <summary>Decimal H3 fine hex inside the current region.</summary>
    public ulong? LocationHex { get; set; }

    /// <summary>Decimal H3 region index the rover is driving toward.</summary>
    public ulong? DestinationRegion { get; set; }

    /// <summary>Decimal H3 fine hex at the destination.</summary>
    public ulong? DestinationHex { get; set; }

    public double? DepartureGameHour { get; set; }

    public double? ArrivalGameHour { get; set; }

    /// <summary>Resource id → amount carried by the rover.</summary>
    public Dictionary<string, double> Cargo { get; init; } = new(StringComparer.Ordinal);
}
