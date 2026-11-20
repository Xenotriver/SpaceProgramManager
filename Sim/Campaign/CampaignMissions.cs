namespace Sim.Campaign;

/// <summary>
/// M5 C3 gameplay: accept/deliver/claim contracts and raise/resolve random events.
/// Everything mutates <see cref="CampaignState.Meta"/> through the fields already
/// frozen in save v4, so no migration or fingerprint change is involved.
/// </summary>
public sealed partial class CampaignState
{
    /// <summary>Unaccepted offers whose technology prerequisites are met.</summary>
    public IReadOnlyList<ContractDefinition> AvailableContracts => ContractCatalogue.All
        .Where(def => !Meta.Contracts.Any(item => item.DefId == def.Id))
        .Where(def => IsTechUnlocked(def.RequiredTech ?? string.Empty))
        .ToArray();

    /// <summary>Contracts still open for delivery or an early settlement.</summary>
    public IReadOnlyList<CampaignContract> ActiveContracts => Meta.Contracts
        .Where(item => item.Status == ContractStatus.Active)
        .OrderBy(item => item.Id, StringComparer.Ordinal)
        .ToArray();

    /// <summary>Accepts a static contract definition into the campaign state.</summary>
    public ContractActionResult TryAcceptContract(string defId)
    {
        if (!ContractCatalogue.TryGet(defId, out var def) || def is null)
            return new ContractActionResult(
                ContractActionStatus.UnknownContract, $"unknown contract: {defId}");

        if (Meta.Contracts.Any(item => item.DefId == def.Id))
            return new ContractActionResult(
                ContractActionStatus.AlreadyAccepted, $"{def.DisplayName} is already on the books");

        if (!IsTechUnlocked(def.RequiredTech ?? string.Empty))
            return new ContractActionResult(
                ContractActionStatus.TechLocked,
                $"{def.DisplayName} requires tech: {def.RequiredTech}");

        Meta.Contracts.Add(new CampaignContract
        {
            DefId = def.Id,
            AcceptedGameHour = Meta.GameTimeHours,
            DeadlineGameHour = Meta.GameTimeHours + def.DurationHours,
        });
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        return new ContractActionResult(
            ContractActionStatus.Accepted, $"accepted {def.DisplayName}");
    }

    /// <summary>
    /// Delivers stockpile to one contract counter. With no counter named, the
    /// first short counter (ordered by id) is used. Partial deliveries are kept.
    /// </summary>
    public ContractActionResult TryDeliverContract(string contractId, string? counterId = null)
    {
        var contract = FindContract(contractId);
        if (contract is null || !ContractCatalogue.TryGet(contract.DefId, out var def) || def is null)
            return new ContractActionResult(
                ContractActionStatus.UnknownContract, $"unknown contract: {contractId}");

        if (contract.Status != ContractStatus.Active)
            return new ContractActionResult(
                ContractActionStatus.NotActive, $"{def.DisplayName} is {contract.Status.ToString().ToLowerInvariant()}");

        var counter = counterId ?? ContractCatalogue.NextCounter(contract, def);
        if (counter is null || !def.Targets.TryGetValue(counter, out var target) || target <= 0.0)
            return new ContractActionResult(
                ContractActionStatus.UnknownCounter, $"unknown delivery counter: {counter}");

        var remaining = target - contract.Progress.GetValueOrDefault(counter);
        if (remaining <= 1e-9)
            return new ContractActionResult(
                ContractActionStatus.NothingToDeliver, $"{counter} is already fully delivered");

        var stored = Meta.Stockpile.GetValueOrDefault(counter);
        if (stored <= 1e-9)
            return new ContractActionResult(
                ContractActionStatus.InsufficientStock, $"no {counter} in the stockpile");

        var amount = Math.Min(remaining, stored);
        Meta.Stockpile[counter] = Math.Max(0.0, stored - amount);
        contract.Progress[counter] = Math.Min(target, contract.Progress.GetValueOrDefault(counter) + amount);
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;

        if (ContractCatalogue.CompletionFraction(contract, def) >= 1.0 - 1e-9)
            contract.Status = ContractStatus.Completed;

        return new ContractActionResult(
            ContractActionStatus.Delivered,
            $"delivered {amount:0.#} {counter} to {def.DisplayName}");
    }

    /// <summary>
    /// Closes a contract and pays proportionally to what was delivered. A fully
    /// met contract also earns its completion bonus; a partial one is a write-off
    /// with reduced credit and science rewards.
    /// </summary>
    public ContractActionResult TryClaimReward(string contractId)
    {
        var contract = FindContract(contractId);
        if (contract is null || !ContractCatalogue.TryGet(contract.DefId, out var def) || def is null)
            return new ContractActionResult(
                ContractActionStatus.UnknownContract, $"unknown contract: {contractId}");

        if (contract.RewardClaimed)
            return new ContractActionResult(
                ContractActionStatus.AlreadyClaimed, $"{def.DisplayName} was already settled");

        if (contract.Status == ContractStatus.Failed)
            return new ContractActionResult(
                ContractActionStatus.NotActive, $"{def.DisplayName} was already settled");

        return Settle(contract, def);
    }

    /// <summary>Rolls a random event, honouring the earliest hour and cooldown.</summary>
    public EventActionResult TryRaiseRandomEvent()
    {
        if (Meta.PendingEvents.Count > 0)
            return new EventActionResult(EventActionStatus.NothingRaised, "an event is already waiting");

        var now = Meta.GameTimeHours;
        if (now + 1e-9 < CampaignDefaults.EventEarliestGameHour)
            return new EventActionResult(EventActionStatus.NothingRaised, "too early for random events");

        if (Meta.EventCooldownGameHour is { } cooldown && now + 1e-9 < cooldown)
            return new EventActionResult(EventActionStatus.NothingRaised, "event cooldown is active");

        var def = EventCatalogue.Pick(NextRandom());
        ulong? target = null;
        if (def.TargetsRegion)
        {
            var regions = Meta.VisitedRegions.OrderBy(index => index).ToArray();
            if (regions.Length > 0)
                target = regions[(int)Math.Floor(NextRandom() * regions.Length) % regions.Length];
        }

        Meta.PendingEvents.Add(new PendingCampaignEvent
        {
            DefId = def.Id,
            RaisedGameHour = now,
            TargetRegion = target,
            Choices = def.Choices.Select(choice => choice.Id).ToList(),
        });
        Meta.EventCooldownGameHour = now + def.CooldownHours;
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        return new EventActionResult(EventActionStatus.Raised, $"{def.DisplayName} needs a decision");
    }

    /// <summary>Applies one choice, appends the bounded log and clears the pending event.</summary>
    public EventActionResult TryResolveEvent(string eventId, string choiceId)
    {
        var pending = Meta.PendingEvents.FirstOrDefault(item => item.Id == eventId);
        if (pending is null || !EventCatalogue.TryGet(pending.DefId, out var def) || def is null)
            return new EventActionResult(EventActionStatus.UnknownEvent, $"unknown event: {eventId}");

        var choice = def.Choices.FirstOrDefault(item => item.Id == choiceId);
        if (choice is null)
            return new EventActionResult(EventActionStatus.UnknownChoice, $"unknown choice: {choiceId}");

        var effect = choice.Effect;
        Meta.Credits = Math.Max(0.0, Meta.Credits + effect.Credits);
        Meta.SciencePoints = Math.Max(0.0, Meta.SciencePoints + effect.Science);
        foreach (var (resource, delta) in effect.Stockpile)
            Meta.Stockpile[resource] = Math.Max(0.0, Meta.Stockpile.GetValueOrDefault(resource) + delta);

        Meta.AppendEventLog(new CampaignEventLogEntry
        {
            GameHour = Meta.GameTimeHours,
            DefId = def.Id,
            Choice = choice.Id,
            Summary = effect.Summary,
        });
        Meta.PendingEvents.Remove(pending);
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        return new EventActionResult(EventActionStatus.Resolved, $"{def.DisplayName}: {effect.Summary}");
    }

    /// <summary>Called from each simulation step; settles contracts past deadline.</summary>
    internal void SettleExpiredContracts()
    {
        var now = Meta.GameTimeHours;
        foreach (var contract in Meta.Contracts
                     .Where(item => item.Status == ContractStatus.Active
                         && !item.RewardClaimed
                         && item.DeadlineGameHour is { } deadline
                         && deadline <= now)
                     .OrderBy(item => item.Id, StringComparer.Ordinal)
                     .ToList())
        {
            if (ContractCatalogue.TryGet(contract.DefId, out var def) && def is not null)
                Settle(contract, def);
        }
    }

    private ContractActionResult Settle(CampaignContract contract, ContractDefinition def)
    {
        var fraction = ContractCatalogue.CompletionFraction(contract, def);
        var full = fraction >= 1.0 - 1e-9;
        var credits = def.CreditsReward * fraction
            + (full ? def.CreditsReward * def.CompletionBonus : 0.0);
        var science = def.ScienceReward * fraction;

        contract.Status = full ? ContractStatus.Completed : ContractStatus.Failed;
        contract.RewardClaimed = true;
        Meta.Credits += credits;
        Meta.SciencePoints += science;
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;

        return new ContractActionResult(
            full ? ContractActionStatus.Claimed : ContractActionStatus.Settled,
            full
                ? $"{def.DisplayName} complete · {credits:N0} cr · {science:0.#} science"
                : $"{def.DisplayName} closed at {fraction:P0} · {credits:N0} cr · {science:0.#} science",
            credits,
            science);
    }

    private CampaignContract? FindContract(string contractId)
        => Meta.Contracts.FirstOrDefault(item => item.Id == contractId);
}
