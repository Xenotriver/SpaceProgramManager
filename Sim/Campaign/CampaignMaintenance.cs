using Sim.Buildings;

namespace Sim.Campaign;

/// <summary>An installation with an active failure, paired with its region for the UI.</summary>
public sealed record DamagedInstallation(ulong RegionIndex, BuildingInstance Building);

/// <summary>
/// M5 C4 gameplay: damage installations, pay for repairs and complete them in
/// <see cref="CampaignState.Tick"/>. All state goes through the typed v4
/// <see cref="BuildingFailure"/> record already carried by region shards.
/// </summary>
public sealed partial class CampaignState
{
    /// <summary>Unresolved failures in loaded regions, stable order for display.</summary>
    public IReadOnlyList<DamagedInstallation> DamagedInstallations => _regions
        .OrderBy(item => item.Key)
        .SelectMany(item => item.Value.Buildings
            .Where(building => building.Failure is { RepairCompleteGameHour: null })
            .OrderBy(building => building.Id.ToString("D"), StringComparer.Ordinal)
            .Select(building => new DamagedInstallation(item.Key, building)))
        .ToArray();

    /// <summary>
    /// Applies a static failure cause to an operational installation. When no
    /// severity is given the campaign RNG rolls inside the cause's own range.
    /// </summary>
    public MaintenanceActionResult TryDamageBuilding(
        ulong regionIndex,
        Guid buildingId,
        string causeId,
        double? severity = null)
    {
        if (!_regions.TryGetValue(regionIndex, out var region))
            return new MaintenanceActionResult(
                MaintenanceActionStatus.UnknownRegion, $"region {regionIndex} is not loaded");

        var building = region.Buildings.FirstOrDefault(item => item.Id == buildingId);
        if (building is null)
            return new MaintenanceActionResult(
                MaintenanceActionStatus.UnknownBuilding, $"unknown installation: {buildingId}");

        if (building.Failure is { RepairCompleteGameHour: null })
            return new MaintenanceActionResult(
                MaintenanceActionStatus.AlreadyDamaged, $"{DisplayName(building)} is already being handled");

        if (building.Status is not (BuildingStatus.Operational or BuildingStatus.Idle))
            return new MaintenanceActionResult(
                MaintenanceActionStatus.NotOperational,
                $"{DisplayName(building)} is {building.Status.ToString().ToLowerInvariant()}");

        if (!FailureCatalogue.TryGet(causeId, out var cause) || cause is null)
            return new MaintenanceActionResult(
                MaintenanceActionStatus.UnknownCause, $"unknown failure cause: {causeId}");

        var rolled = severity ?? FailureCatalogue.RollSeverity(cause, NextRandom());
        var clamped = Math.Clamp(double.IsFinite(rolled) ? rolled : cause.MinSeverity, 0.0, 1.0);
        var destroyed = clamped >= CampaignDefaults.FailureDestroyedSeverity;

        building.Failure = new BuildingFailure
        {
            Cause = cause.Id,
            Severity = clamped,
            StartedGameHour = Meta.GameTimeHours,
        };
        building.Health = Math.Max(0.0, (_registry.TryGet(building.DefId, out var def) && def is not null
            ? def.MaxHealth
            : 100.0) * (1.0 - clamped));
        building.Status = destroyed ? BuildingStatus.Destroyed : BuildingStatus.Damaged;
        building.StatusReason = $"{cause.DisplayName} · severity {clamped:P0}";
        Touch(regionIndex);

        return new MaintenanceActionResult(
            MaintenanceActionStatus.Damaged,
            $"{DisplayName(building)}: {cause.DisplayName} · {(destroyed ? "destroyed" : "damaged")} at {clamped:P0}");
    }

    /// <summary>
    /// Starts a repair using the failure's severity-scaled bill. The installation
    /// stays damaged until <see cref="Tick"/> reaches the completion hour.
    /// </summary>
    public MaintenanceActionResult TryStartRepair(ulong regionIndex, Guid buildingId)
    {
        if (!_regions.TryGetValue(regionIndex, out var region))
            return new MaintenanceActionResult(
                MaintenanceActionStatus.UnknownRegion, $"region {regionIndex} is not loaded");

        var building = region.Buildings.FirstOrDefault(item => item.Id == buildingId);
        if (building is null)
            return new MaintenanceActionResult(
                MaintenanceActionStatus.UnknownBuilding, $"unknown installation: {buildingId}");

        var failure = building.Failure;
        if (failure is null || failure.RepairCompleteGameHour is not null)
            return new MaintenanceActionResult(
                MaintenanceActionStatus.Healthy, $"{DisplayName(building)} has no open failure");

        if (failure.RepairStartGameHour is not null)
            return new MaintenanceActionResult(
                MaintenanceActionStatus.AlreadyRepairing, $"{DisplayName(building)} is already being repaired");

        if (!FailureCatalogue.TryGet(failure.Cause, out var cause) || cause is null)
            return new MaintenanceActionResult(
                MaintenanceActionStatus.UnknownCause, $"unknown failure cause: {failure.Cause}");

        var cost = FailureCatalogue.RepairCost(cause, failure);
        if (!TryPay(cost, out var error))
            return new MaintenanceActionResult(MaintenanceActionStatus.CannotAfford, error);

        failure.RepairStartGameHour = Meta.GameTimeHours;
        building.StatusReason = $"repairing {cause.DisplayName} · {FailureCatalogue.RepairHours(cause, failure):0.#} h";
        Touch(regionIndex);

        return new MaintenanceActionResult(
            MaintenanceActionStatus.RepairStarted,
            $"repairing {DisplayName(building)} · {cost.GetValueOrDefault("credits"):N0} cr",
            cost.GetValueOrDefault("credits"));
    }

    /// <summary>Completes due repairs and rolls the per-building hazard.</summary>
    internal void AdvanceMaintenance(RegionState region, double hours)
    {
        var now = Meta.GameTimeHours;
        var repaired = 0;

        foreach (var building in region.Buildings)
        {
            var failure = building.Failure;
            if (failure?.RepairStartGameHour is not { } start || failure.RepairCompleteGameHour is not null)
                continue;

            FailureCatalogue.TryGet(failure.Cause, out var cause);
            var duration = cause is not null
                ? FailureCatalogue.RepairHours(cause, failure)
                : 24.0 * (0.5 + failure.Severity);

            if (now + 1e-9 < start + duration)
                continue;

            failure.RepairCompleteGameHour = now;
            building.Health = cause is not null && _registry.TryGet(building.DefId, out var def) && def is not null
                ? def.MaxHealth
                : 100.0;
            building.Status = BuildingStatus.Operational;
            building.StatusReason = string.Empty;
            repaired++;
        }

        if (repaired > 0)
            Touch(region.RegionIndex);

        if (hours <= 0.0)
            return;

        foreach (var building in region.Buildings.ToArray())
        {
            if (building.Status is not (BuildingStatus.Operational or BuildingStatus.Idle) || building.Paused)
                continue;

            if (NextRandom() >= CampaignDefaults.HazardPerBuildingHour * hours)
                continue;

            var cause = FailureCatalogue.Pick(NextRandom());
            TryDamageBuilding(region.RegionIndex, building.Id, cause.Id);
        }
    }

    private string DisplayName(BuildingInstance building)
        => _registry.TryGet(building.DefId, out var def) && def is not null
            ? def.DisplayName
            : building.DefId;
}
