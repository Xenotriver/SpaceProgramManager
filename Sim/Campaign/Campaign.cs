using H3;
using Sim.Buildings;
using Sim.Movement;
using Sim.Site;
using Sim.Time;
using Sim.World;

namespace Sim.Campaign;

/// <summary>
/// The single source of truth for what the player has built. Views render it and
/// call <see cref="TryPlace"/> / <see cref="TryRemove"/>; they never own building
/// state themselves, so the region overlay and the close-up management view can
/// both place without duplicating logic.
///
/// Engine-free: no Godot types, no file I/O (that is <c>CampaignStore</c>).
/// </summary>
public sealed partial class CampaignState
{
    private readonly BodyWorld _world;
    private readonly BuildingRegistry _registry;
    private readonly SiteScorerConfig _siteConfig;
    private readonly Dictionary<ulong, RegionState> _regions = new();
    private readonly HashSet<ulong> _loaded = new();
    private readonly Dictionary<string, RegionalRoutePlan> _roverRoutes =
        new(StringComparer.Ordinal);

    public CampaignState(BodyWorld world, BuildingRegistry registry, SiteScorerConfig? siteConfig = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(registry);

        _world = world;
        _registry = registry;
        _siteConfig = siteConfig ?? new SiteScorerConfig { BodyRadiusKm = world.Manifest.Body.RadiusKm };
        Random = new DeterministicRandom(Meta.RngSeed);
    }

    /// <summary>Campaign-wide state (clock, treasury, visited regions).</summary>
    public CampaignMeta Meta { get; private set; } = new();

    /// <summary>Seeded RNG for future reliability/event rolls; state is persisted.</summary>
    public DeterministicRandom Random { get; private set; }

    /// <summary>Replaces campaign-wide state with a snapshot read from storage.</summary>
    public void RestoreMeta(CampaignMeta meta)
    {
        ArgumentNullException.ThrowIfNull(meta);

        Meta = meta;
        Random = new DeterministicRandom(meta.RngSeed);
        Random.State = meta.RngState == 0 ? meta.RngSeed : meta.RngState;
    }

    /// <summary>Draws a number and records the new RNG position for the next save.</summary>
    public double NextRandom()
    {
        var value = Random.NextDouble();
        Meta.RngState = Random.State;
        return value;
    }

    /// <summary>Marks a technology as available to this campaign.</summary>
    public bool UnlockTech(string techId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(techId);

        if (!Meta.UnlockedTechs.Add(techId))
            return false;

        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        return true;
    }

    /// <summary>True when the campaign may build something requiring this tech.</summary>
    public bool IsTechUnlocked(string techId)
        => string.IsNullOrEmpty(techId) || Meta.UnlockedTechs.Contains(techId);

    /// <summary>
    /// Researches a tech immediately by spending accumulated science points.
    /// This is the only gameplay path that charges for tech; <see cref="UnlockTech"/>
    /// stays as the low-level marker used by code, tests and migrations.
    /// </summary>
    public TechPurchaseResult TryPurchaseTech(string techId)
    {
        if (!TechCatalogue.TryGet(techId, out var def) || def is null)
            return new TechPurchaseResult(
                TechPurchaseStatus.UnknownTech, $"unknown tech: {techId}");

        if (IsTechUnlocked(techId))
            return new TechPurchaseResult(
                TechPurchaseStatus.AlreadyUnlocked, $"{def.DisplayName} already researched");

        var missing = def.Prerequisites.Where(id => !IsTechUnlocked(id)).ToList();
        if (missing.Count > 0)
        {
            return new TechPurchaseResult(
                TechPurchaseStatus.PrerequisitesMissing,
                $"requires tech: {string.Join(", ", missing)}");
        }

        if (Meta.SciencePoints + 1e-9 < def.ScienceCost)
        {
            return new TechPurchaseResult(
                TechPurchaseStatus.InsufficientScience,
                $"need {def.ScienceCost:0.#} science (have {Meta.SciencePoints:0.#})");
        }

        Meta.SciencePoints = Math.Max(0.0, Meta.SciencePoints - def.ScienceCost);
        UnlockTech(techId);
        return new TechPurchaseResult(
            TechPurchaseStatus.Purchased, $"{def.DisplayName} researched");
    }

    /// <summary>Campaign-wide rover fleet; the save owns every field.</summary>
    public IReadOnlyList<RoverState> Rovers => Meta.Rovers;

    /// <summary>
    /// Buys a rover at an operational garage in <paramref name="regionIndex"/>.
    /// New rovers are docked at the garage hex and have no cargo.
    /// </summary>
    public RoverActionResult TryPurchaseRover(
        ulong regionIndex,
        out RoverState? rover,
        double? speedKmh = null)
    {
        rover = null;

        if (!_regions.TryGetValue(regionIndex, out var region))
            return new RoverActionResult(RoverActionStatus.NoGarage, "no garage in this region");

        var garages = region.Buildings
            .Where(building => building.DefId == RoverCatalogue.GarageDefId
                               && !building.Paused
                               && building.Status is BuildingStatus.Operational or BuildingStatus.Idle)
            .ToList();

        if (garages.Count == 0)
            return new RoverActionResult(RoverActionStatus.NoGarage, "no operational rover garage here");

        var capacity = 0;
        foreach (var garage in garages)
        {
            if (_registry.TryGet(garage.DefId, out var def) && def is not null)
                capacity += (int)def.StorageCapacity.GetValueOrDefault("rover_slots", 0.0);
        }

        var stationed = Meta.Rovers.Count(item => item.LocationRegion == regionIndex);
        if (stationed >= capacity)
            return new RoverActionResult(RoverActionStatus.NoCapacity, "garage is full");

        var model = RoverCatalogue.Mk1;
        if (!TryPay(model.Cost, out var error))
            return new RoverActionResult(RoverActionStatus.CannotAfford, error);

        var speed = speedKmh ?? model.SpeedKmh;
        if (speed <= 0.0)
            speed = model.SpeedKmh;

        rover = new RoverState
        {
            Id = Guid.NewGuid().ToString("D"),
            Name = $"Rover {Meta.Rovers.Count + 1}",
            DefId = model.Id,
            Status = RoverStatus.Docked,
            Health = model.Health,
            SpeedKmh = speed,
            LocationRegion = regionIndex,
            LocationHex = (ulong)garages[0].ParentHex,
        };

        Meta.Rovers.Add(rover);
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        return new RoverActionResult(RoverActionStatus.Purchased, $"{rover.Name} ready at garage");
    }

    /// <summary>
    /// Sends an available rover from its current hex to a fine hex in
    /// <paramref name="toRegion"/>. Travel finishes during <see cref="Tick"/>.
    /// </summary>
    public RoverActionResult DispatchRover(string roverId, ulong toRegion, ulong toHex)
    {
        var rover = Meta.Rovers.FirstOrDefault(item => item.Id == roverId);
        if (rover is null)
            return new RoverActionResult(RoverActionStatus.UnknownRover, "unknown rover");

        if (rover.Status is not (RoverStatus.Idle or RoverStatus.Docked) || rover.Health <= 0.0)
            return new RoverActionResult(RoverActionStatus.NotAvailable, $"{rover.Name} is not available");

        if (rover.LocationRegion is not { } fromRegion || rover.LocationHex is not { } fromHex)
            return new RoverActionResult(RoverActionStatus.WrongLocation, $"{rover.Name} has no location");

        var plan = RegionalRoutePlanner.Plan(
            _world, fromRegion, fromHex, toRegion, toHex, rover.SpeedKmh);
        if (plan is null)
            return new RoverActionResult(RoverActionStatus.Unreachable, "destination is unreachable");

        rover.Status = RoverStatus.Enroute;
        rover.DepartureGameHour = Meta.GameTimeHours;
        rover.ArrivalGameHour = Meta.GameTimeHours + plan.TotalHours;
        rover.DestinationRegion = toRegion;
        rover.DestinationHex = toHex;
        _roverRoutes[rover.Id] = plan;
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;

        return new RoverActionResult(
            RoverActionStatus.Dispatched,
            $"{rover.Name} en route · {plan.TotalDistanceKm:0.#} km · {plan.TotalHours:0.#} h");
    }

    /// <summary>
    /// Cached or rebuilt route for an en-route rover. Used by the area view to
    /// draw its track; it is derived from save fields and never persisted.
    /// </summary>
    public RegionalRoutePlan? RouteForRover(string roverId)
    {
        var rover = Meta.Rovers.FirstOrDefault(item => item.Id == roverId);
        if (rover?.Status != RoverStatus.Enroute
            || rover.LocationRegion is not { } fromRegion
            || rover.LocationHex is not { } fromHex
            || rover.DestinationRegion is not { } toRegion
            || rover.DestinationHex is not { } toHex)
            return null;

        if (_roverRoutes.TryGetValue(roverId, out var cached)
            && cached.StartRegion == fromRegion
            && cached.StartHex == fromHex
            && cached.GoalRegion == toRegion
            && cached.GoalHex == toHex)
            return cached;

        var rebuilt = RegionalRoutePlanner.Plan(
            _world, fromRegion, fromHex, toRegion, toHex, rover.SpeedKmh);
        if (rebuilt is not null)
            _roverRoutes[roverId] = rebuilt;

        return rebuilt;
    }

    /// <summary>Scoring configuration used for placement validation.</summary>
    public SiteScorerConfig SiteConfig => _siteConfig;

    /// <summary>Building catalogue.</summary>
    public BuildingRegistry Registry => _registry;

    /// <summary>Raised after a region's buildings change (placement, removal, clear).</summary>
    public event Action<ulong>? RegionChanged;

    /// <summary>Regions whose stored buildings have been installed or modified this session.</summary>
    public IReadOnlyCollection<ulong> LoadedRegions => _loaded;

    /// <summary>True once a region has been installed from storage or mutated.</summary>
    public bool IsLoaded(ulong regionIndex) => _loaded.Contains(regionIndex);

    /// <summary>Returns the region's state, creating an empty one on first access.</summary>
    public RegionState GetRegion(ulong regionIndex)
    {
        if (!_regions.TryGetValue(regionIndex, out var state))
            _regions[regionIndex] = state = new RegionState(regionIndex);

        return state;
    }

    /// <summary>Buildings in a region (empty for regions not opened yet).</summary>
    public IReadOnlyList<BuildingInstance> Buildings(ulong regionIndex)
        => _regions.TryGetValue(regionIndex, out var state)
            ? state.Buildings
            : Array.Empty<BuildingInstance>();

    /// <summary>Number of buildings in a region.</summary>
    public int BuildingCount(ulong regionIndex) => Buildings(regionIndex).Count;

    /// <summary>Records that the player has opened a region.</summary>
    public void MarkVisited(ulong regionIndex)
    {
        if (Meta.VisitedRegions.Add(regionIndex))
            Meta.UpdatedUtc = DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Validates a site placement and returns its local footprint for the ghost.
    /// The footprint is always included, so a rejected preview can be drawn red.
    /// </summary>
    public PlacementResult Preview(BuildingDef def, RegionTile tile, int hexOrdinal, LocalCell localCenter)
    {
        ArgumentNullException.ThrowIfNull(def);
        ArgumentNullException.ThrowIfNull(tile);

        var footprint = LocalGrid.Footprint(localCenter, def.FootprintRadius);
        if ((uint)hexOrdinal >= (uint)tile.HexCount)
            return PlacementResult.FailLocal($"Hex ordinal {hexOrdinal} out of range (0..{tile.HexCount - 1})", footprint);

        if (!IsTechUnlocked(def.RequiredTech))
            return PlacementResult.FailLocal($"requires tech: {def.RequiredTech}", footprint);

        var parentHex = tile.Cells[hexOrdinal];
        var score = tile.ScoreSite(hexOrdinal, _world, _siteConfig);
        if (!score.Factors.Buildable)
            return PlacementResult.FailLocal("Site is not buildable", footprint);

        var composite = score.Factors.Composite(_siteConfig.Weights);
        if (composite < def.MinSiteScore)
            return PlacementResult.FailLocal($"Site score {composite:P0} below minimum {def.MinSiteScore:P0}", footprint);

        var biomeCode = tile.BiomeCode(hexOrdinal);
        if (!_world.Biomes.TryGet(biomeCode, out var biome))
            return PlacementResult.FailLocal($"Unknown biome code {biomeCode}", footprint);

        if (def.AllowedBiomeIds.Count > 0 && !def.AllowedBiomeIds.Contains(biome.Id))
            return PlacementResult.FailLocal($"Biome {biome.Id} not in allowed list", footprint);

        if (def.ForbiddenBiomeIds.Contains(biome.Id))
            return PlacementResult.FailLocal($"Biome {biome.Id} is forbidden", footprint);

        foreach (var cell in footprint)
        {
            if (!LocalGrid.IsInside(cell))
                return PlacementResult.FailLocal("Footprint extends outside the site grid", footprint);
        }

        SiteState? site = null;
        if (_regions.TryGetValue(tile.Entry.Index, out var region))
            region.TryGetSite(parentHex, out site);

        if (site is not null)
        {
            foreach (var cell in footprint)
            {
                if (site.TryGetAt(cell, out var occupant) && occupant is not null)
                    return PlacementResult.FailLocal($"Cell {cell} already occupied by {occupant.DefId}", footprint);
            }
        }

        var warnings = new List<string>();
        if (def.Prerequisites.Count > 0)
        {
            var present = new HashSet<string>(
                (site?.Installations ?? Array.Empty<BuildingInstance>()).Select(b => b.DefId),
                StringComparer.Ordinal);
            var missing = def.Prerequisites.Where(p => !present.Contains(p)).ToList();
            if (missing.Count > 0)
                warnings.Add($"Missing prerequisites: {string.Join(", ", missing)}");
        }

        return PlacementResult.OkLocal(footprint, warnings);
    }

    /// <summary>Validates and, on success, pays for and places an installation in a site.</summary>
    public PlacementResult TryPlace(BuildingDef def, RegionTile tile, int hexOrdinal, LocalCell localCenter)
    {
        var result = Preview(def, tile, hexOrdinal, localCenter);
        if (!result.Success)
            return result;

        if (!TryPay(def.Cost, out var paymentError))
            return PlacementResult.FailLocal(paymentError, result.LocalFootprint);

        var regionIndex = tile.Entry.Index;
        var operational = def.BuildTimeHours <= 0.0;

        GetRegion(regionIndex).Add(new BuildingInstance
        {
            DefId = def.Id,
            ParentHex = tile.Cells[hexOrdinal],
            LocalCenter = localCenter,
            LocalFootprint = result.LocalFootprint,
            Status = operational ? BuildingStatus.Operational : BuildingStatus.Planned,
            Progress = operational ? 1.0 : 0.0,
            ConstructionStartTime = Meta.GameTimeHours,
            ConstructionCompleteTime = operational ? Meta.GameTimeHours : null,
        });

        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        Touch(regionIndex);
        return result;
    }

    /// <summary>
    /// Pays a building cost from credits and the stockpile. Nothing is deducted
    /// when any line is short, so a refused placement changes no state.
    /// </summary>
    private bool TryPay(IReadOnlyDictionary<string, double> cost, out string error)
    {
        foreach (var (resource, amount) in cost)
        {
            if (amount <= 0.0)
                continue;

            if (string.Equals(resource, "credits", StringComparison.Ordinal))
            {
                if (Meta.Credits + 1e-9 < amount)
                {
                    error = $"not enough credits: {amount:N0} needed, {Meta.Credits:N0} available";
                    return false;
                }

                continue;
            }

            var stored = Meta.Stockpile.TryGetValue(resource, out var value) ? value : 0.0;
            if (stored + 1e-9 < amount)
            {
                error = $"not enough {resource}: {amount:N1} needed, {stored:N1} available";
                return false;
            }
        }

        foreach (var (resource, amount) in cost)
        {
            if (amount <= 0.0)
                continue;

            if (string.Equals(resource, "credits", StringComparison.Ordinal))
            {
                Meta.Credits = Math.Max(0.0, Meta.Credits - amount);
                continue;
            }

            Meta.Stockpile[resource] = Math.Max(0.0, Meta.Stockpile.GetValueOrDefault(resource) - amount);
        }

        error = string.Empty;
        return true;
    }

    /// <summary>Removes the installation occupying a local cell of a site.</summary>
    public bool TryRemove(RegionTile tile, int hexOrdinal, LocalCell localCell, out BuildingInstance? removed)
    {
        ArgumentNullException.ThrowIfNull(tile);

        removed = null;
        if ((uint)hexOrdinal >= (uint)tile.HexCount)
            return false;

        var regionIndex = tile.Entry.Index;
        var state = GetRegion(regionIndex);

        if (!state.TryGetAt(tile.Cells[hexOrdinal], localCell, out removed) || removed is null)
            return false;

        state.Remove(removed);
        Touch(regionIndex);
        return true;
    }

    /// <summary>Removes every building in the region and returns how many were removed.</summary>
    public int Clear(RegionTile tile)
    {
        ArgumentNullException.ThrowIfNull(tile);

        var regionIndex = tile.Entry.Index;
        var removed = GetRegion(regionIndex).Clear();

        if (removed > 0)
            Touch(regionIndex);

        return removed;
    }

    /// <summary>Returns the installation occupying a local cell of a site, if any.</summary>
    public bool TryGetAt(RegionTile tile, int hexOrdinal, LocalCell localCell, out BuildingInstance? building)
    {
        ArgumentNullException.ThrowIfNull(tile);

        building = null;
        if ((uint)hexOrdinal >= (uint)tile.HexCount)
            return false;

        return GetRegion(tile.Entry.Index).TryGetAt(tile.Cells[hexOrdinal], localCell, out building);
    }

    /// <summary>Sites in a region, keyed by parent fine hex.</summary>
    public IReadOnlyDictionary<ulong, SiteState> Sites(ulong regionIndex)
        => _regions.TryGetValue(regionIndex, out var region)
            ? region.Sites
            : new Dictionary<ulong, SiteState>();

    /// <summary>
    /// Validates stored installations against the live tile and installs them.
    /// Local footprints are rebuilt from the definition, so a save only needs the
    /// parent hex and local centre; records that no longer fit are dropped.
    /// </summary>
    public RegionLoadReport InstallRegion(RegionLoadResult stored, RegionTile tile)
    {
        ArgumentNullException.ThrowIfNull(stored);
        ArgumentNullException.ThrowIfNull(tile);

        var regionIndex = tile.Entry.Index;
        if (stored.RegionIndex != regionIndex)
            throw new ArgumentException(
                $"Stored region {stored.RegionIndex} does not match tile {regionIndex}.", nameof(stored));

        var warnings = new List<string>(stored.Warnings);
        var accepted = new List<BuildingInstance>(stored.Buildings.Count);
        var claimed = new Dictionary<ulong, Dictionary<LocalCell, BuildingInstance>>();
        var ordinals = BuildOrdinalMap(tile);

        foreach (var candidate in stored.Buildings)
        {
            if (!_registry.TryGet(candidate.DefId, out var def) || def is null)
            {
                warnings.Add($"{candidate.DefId}: unknown building definition");
                continue;
            }

            var parentHex = (ulong)candidate.ParentHex;
            if (!ordinals.TryGetValue(parentHex, out var hexOrdinal))
            {
                warnings.Add($"{def.Id}: parent hex {parentHex} is not in region {regionIndex}");
                continue;
            }

            var footprint = LocalGrid.Footprint(candidate.LocalCenter, def.FootprintRadius);
            if (footprint.Any(cell => !LocalGrid.IsInside(cell)))
            {
                warnings.Add($"{def.Id} at {parentHex}: footprint outside the site grid");
                continue;
            }

            if (!claimed.TryGetValue(parentHex, out var occupancy))
                claimed[parentHex] = occupancy = new Dictionary<LocalCell, BuildingInstance>();

            BuildingInstance? clash = null;
            foreach (var cell in footprint)
            {
                if (occupancy.TryGetValue(cell, out var other))
                {
                    clash = other;
                    break;
                }
            }

            if (clash is not null)
            {
                warnings.Add($"{def.Id} at {parentHex}: overlaps {clash.DefId} in the same site");
                continue;
            }

            var building = candidate with
            {
                Id = candidate.Id == Guid.Empty ? Guid.NewGuid() : candidate.Id,
                ParentHex = tile.Cells[hexOrdinal],
                LocalFootprint = footprint,
            };

            accepted.Add(building);
            foreach (var cell in footprint)
                occupancy[cell] = building;
        }

        GetRegion(regionIndex).ReplaceAll(accepted);
        _loaded.Add(regionIndex);
        MarkVisited(regionIndex);

        return new RegionLoadReport(
            regionIndex,
            accepted.Count,
            stored.Buildings.Count - accepted.Count,
            warnings);
    }

    /// <summary>Longest single simulation step. Callers may pass any duration.</summary>
    public const double MaxTickHours = 0.25;

    /// <summary>
    /// Advances time and simulates every region installed in memory. Regions whose
    /// shards were never opened are not ticked; that keeps memory bounded and is a
    /// documented M1 limitation (a future fleet pass can walk all shards).
    /// </summary>
    public TickReport Tick(double hours)
    {
        if (hours <= 0.0 || double.IsNaN(hours) || double.IsInfinity(hours))
            return TickReport.None;

        var remaining = hours;
        var completed = 0;
        var idled = 0;
        var arrived = 0;
        var steps = 0;

        while (remaining > 1e-9)
        {
            var step = Math.Min(remaining, MaxTickHours);
            var report = Step(step);
            completed += report.Completed;
            idled += report.Idled;
            arrived += report.Arrived;
            steps += report.Steps;
            remaining -= step;
        }

        if (steps > 0)
            Meta.UpdatedUtc = DateTimeOffset.UtcNow;

        return new TickReport(hours, steps, completed, idled, arrived);
    }

    /// <summary>Pauses or resumes one installation's construction or operation.</summary>
    public bool TrySetPaused(RegionTile tile, int hexOrdinal, LocalCell localCell, bool paused, out string error)
    {
        ArgumentNullException.ThrowIfNull(tile);

        if (!TryGetAt(tile, hexOrdinal, localCell, out var building) || building is null)
        {
            error = "no installation on this cell";
            return false;
        }

        if (building.Status is BuildingStatus.Destroyed or BuildingStatus.Damaged)
        {
            error = $"cannot pause a {building.Status.ToString().ToLowerInvariant()} building";
            return false;
        }

        building.Paused = paused;
        building.StatusReason = paused
            ? building.Status is BuildingStatus.Planned or BuildingStatus.UnderConstruction
                ? "construction paused"
                : "paused"
            : string.Empty;

        Touch(tile.Entry.Index);
        error = string.Empty;
        return true;
    }

    /// <summary>
    /// Current power balance and status counts for one region. Reflects the last
    /// tick (or placement) without mutating anything.
    /// </summary>
    public RegionEconomy GetEconomy(ulong regionIndex)
    {
        if (!_regions.TryGetValue(regionIndex, out var region))
            return RegionEconomy.Empty;

        var supply = 0.0;
        var demand = 0.0;
        var idle = 0;
        var operational = 0;
        var construction = 0;

        foreach (var building in region.Buildings)
        {
            if (!_registry.TryGet(building.DefId, out var def) || def is null)
                continue;

            switch (building.Status)
            {
                case BuildingStatus.Operational:
                    operational++;
                    if (building.Paused)
                        break;
                    if (def.PowerKw < 0.0)
                        supply += -def.PowerKw;
                    else
                        demand += def.PowerKw;
                    break;

                case BuildingStatus.Idle:
                    idle++;
                    break;

                case BuildingStatus.Planned or BuildingStatus.UnderConstruction:
                    construction++;
                    break;
            }
        }

        return new RegionEconomy(supply, demand, idle, operational, construction);
    }

    private TickReport Step(double hours)
    {
        Meta.GameTimeHours += hours;

        var completed = 0;
        var idled = 0;
        var arrived = 0;
        var steps = 1;

        // Sorted region order makes shared-stockpile updates reproducible.
        foreach (var regionIndex in _loaded.OrderBy(index => index))
        {
            if (!_regions.TryGetValue(regionIndex, out var region))
                continue;

            AdvanceMaintenance(region, hours);
            SimulateRegion(region, hours, ref completed, ref idled);
        }

        arrived += CompleteRoverArrivals();
        SettleExpiredContracts();
        TryRaiseRandomEvent();

        return new TickReport(hours, steps, completed, idled, arrived);
    }

    private int CompleteRoverArrivals()
    {
        var arrived = 0;
        var now = Meta.GameTimeHours;

        foreach (var rover in Meta.Rovers
                     .Where(item => item.Status == RoverStatus.Enroute
                                    && item.ArrivalGameHour is not null
                                    && item.ArrivalGameHour <= now)
                     .OrderBy(item => item.Id, StringComparer.Ordinal)
                     .ToList())
        {
            rover.LocationRegion = rover.DestinationRegion;
            rover.LocationHex = rover.DestinationHex;
            rover.DestinationRegion = null;
            rover.DestinationHex = null;
            rover.DepartureGameHour = null;
            rover.ArrivalGameHour = null;
            rover.Status = RoverStatus.Docked;
            _roverRoutes.Remove(rover.Id);
            arrived++;
        }

        return arrived;
    }

    private void SimulateRegion(RegionState region, double hours, ref int completed, ref int idled)
    {
        var candidates = new List<Candidate>(region.Buildings.Count);
        var now = Meta.GameTimeHours;

        foreach (var building in region.Buildings)
        {
            if (!_registry.TryGet(building.DefId, out var def) || def is null)
                continue;

            if (building.Status is BuildingStatus.Destroyed or BuildingStatus.Damaged)
                continue;

            if (building.Status is BuildingStatus.Planned or BuildingStatus.UnderConstruction)
            {
                if (building.Paused)
                {
                    building.StatusReason = "construction paused";
                    continue;
                }

                if (def.BuildTimeHours <= 0.0)
                {
                    CompleteConstruction(building, now);
                    completed++;
                    continue;
                }

                building.Status = BuildingStatus.UnderConstruction;
                building.StatusReason = string.Empty;
                building.Progress = Math.Min(1.0, building.Progress + hours / def.BuildTimeHours);

                if (building.Progress >= 1.0)
                {
                    CompleteConstruction(building, now);
                    completed++;
                }

                continue;
            }

            // Operational/Idle is recomputed every step so a power or input
            // recovery reactivates the building without special cases.
            building.Status = BuildingStatus.Operational;
            building.StatusReason = building.Paused ? "paused" : string.Empty;

            if (!building.Paused)
                candidates.Add(new Candidate(building, def));
        }

        if (candidates.Count == 0)
            return;

        var active = ApplyPowerBalance(candidates, ref idled);
        ApplyInputBalance(active, hours, ref idled);
        ApplyThroughput(active, hours);
        ApplyMaintenance(region, ref idled);
    }

    private List<Candidate> ApplyPowerBalance(List<Candidate> candidates, ref int idled)
    {
        var active = new List<Candidate>(candidates.Count);
        var supply = 0.0;

        foreach (var candidate in candidates)
        {
            if (candidate.Def.PowerKw < 0.0)
                supply += -candidate.Def.PowerKw;
            else if (candidate.Def.PowerKw <= 0.0)
                active.Add(candidate);
        }

        var consumers = candidates
            .Where(candidate => candidate.Def.PowerKw > 0.0)
            .OrderBy(candidate => ConsumptionPriority(candidate.Def.Category))
            .ThenBy(candidate => candidate.Building.Id.ToString("D"), StringComparer.Ordinal);

        var remaining = supply;
        foreach (var consumer in consumers)
        {
            if (consumer.Def.PowerKw <= remaining + 1e-9)
            {
                remaining -= consumer.Def.PowerKw;
                active.Add(consumer);
                continue;
            }

            consumer.Building.Status = BuildingStatus.Idle;
            consumer.Building.StatusReason =
                $"power deficit: needs {consumer.Def.PowerKw:F0} kW, {remaining:F0} kW available";
            idled++;
        }

        return active;
    }

    private void ApplyInputBalance(List<Candidate> active, double hours, ref int idled)
    {
        var resources = active
            .SelectMany(candidate => candidate.Def.ThroughputPerHour.Keys)
            .Where(resource => !string.Equals(resource, "power", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(resource => resource, StringComparer.Ordinal)
            .ToList();

        foreach (var resource in resources)
        {
            while (true)
            {
                var net = 0.0;
                foreach (var candidate in active)
                    net += candidate.Def.ThroughputPerHour.GetValueOrDefault(resource);

                var projected = Meta.Stockpile.GetValueOrDefault(resource) + net * hours;
                if (projected >= -1e-9)
                    break;

                var consumer = active
                    .Where(candidate => candidate.Def.ThroughputPerHour.GetValueOrDefault(resource) < 0.0)
                    .OrderBy(candidate => ConsumptionPriority(candidate.Def.Category))
                    .ThenBy(candidate => candidate.Def.CrewCapacity)
                    .ThenBy(candidate => candidate.Building.Id.ToString("D"), StringComparer.Ordinal)
                    .FirstOrDefault();

                if (consumer is null)
                    break;

                active.Remove(consumer);
                consumer.Building.Status = BuildingStatus.Idle;
                consumer.Building.StatusReason = LifeSupportResources.Contains(resource)
                    ? $"life support shortfall: {resource}"
                    : $"input shortage: {resource}";
                idled++;
            }
        }
    }

    private void ApplyThroughput(List<Candidate> active, double hours)
    {
        foreach (var candidate in active)
        {
            foreach (var (resource, perHour) in candidate.Def.ThroughputPerHour)
            {
                if (string.Equals(resource, "power", StringComparison.Ordinal) || perHour == 0.0)
                    continue;

                var current = Meta.Stockpile.GetValueOrDefault(resource);
                Meta.Stockpile[resource] = Math.Max(0.0, current + perHour * hours);
            }

            if (candidate.Def.SciencePerHour > 0.0)
                Meta.SciencePoints += candidate.Def.SciencePerHour * hours;
        }
    }

    private void ApplyMaintenance(RegionState region, ref int idled)
    {
        foreach (var building in region.Buildings)
        {
            if (building.Status is not (BuildingStatus.Operational or BuildingStatus.Idle))
                continue;

            if (!_registry.TryGet(building.DefId, out var def) || def is null)
                continue;

            if (def.MaintenancePerHour.Count == 0)
                continue;

            if (TryPay(def.MaintenancePerHour, out _))
                continue;

            building.Status = BuildingStatus.Idle;
            building.StatusReason = "maintenance unpaid";
            idled++;
        }
    }

    private static void CompleteConstruction(BuildingInstance building, double now)
    {
        building.Progress = 1.0;
        building.Status = BuildingStatus.Operational;
        building.ConstructionCompleteTime = now;
        building.StatusReason = string.Empty;
    }

    private static readonly HashSet<string> LifeSupportResources =
        new(StringComparer.Ordinal) { "oxygen", "water", "food" };

    /// <summary>Lower keeps power/inputs first when a region is short.</summary>
    private static int ConsumptionPriority(BuildingCategory category) => category switch
    {
        BuildingCategory.LifeSupport => 0,
        BuildingCategory.Habitat => 1,
        BuildingCategory.Science => 2,
        BuildingCategory.Production => 3,
        BuildingCategory.Storage => 4,
        BuildingCategory.Logistics => 5,
        BuildingCategory.Special => 6,
        BuildingCategory.Defense => 7,
        _ => 8,
    };

    private sealed record Candidate(BuildingInstance Building, BuildingDef Def);

    private void Touch(ulong regionIndex)
    {
        _loaded.Add(regionIndex);
        Meta.VisitedRegions.Add(regionIndex);
        Meta.UpdatedUtc = DateTimeOffset.UtcNow;
        RegionChanged?.Invoke(regionIndex);
    }

    private static Dictionary<ulong, int> BuildOrdinalMap(RegionTile tile)
    {
        var ordinals = new Dictionary<ulong, int>(tile.HexCount);
        for (var i = 0; i < tile.HexCount; i++)
            ordinals[(ulong)tile.Cells[i]] = i;

        return ordinals;
    }
}

/// <summary>Outcome of advancing the clock: buildings finished/stalled and rovers arrived.</summary>
public readonly record struct TickReport(double Hours, int Steps, int Completed, int Idled, int Arrived = 0)
{
    public static TickReport None => new(0.0, 0, 0, 0, 0);

    /// <summary>True when time actually advanced.</summary>
    public bool Changed => Steps > 0;
}

/// <summary>Power and status summary for one region, shown in the HUD.</summary>
public readonly record struct RegionEconomy(
    double SupplyKw,
    double DemandKw,
    int IdleCount,
    int OperationalCount,
    int UnderConstructionCount)
{
    public static RegionEconomy Empty => new(0.0, 0.0, 0, 0, 0);

    public double NetKw => SupplyKw - DemandKw;

    public bool HasDeficit => DemandKw > SupplyKw + 1e-9 || IdleCount > 0;
}
