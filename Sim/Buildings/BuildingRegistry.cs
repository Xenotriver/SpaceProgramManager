using System.Collections.Generic;
using System.Linq;
using H3;
using Sim.Site;
using Sim.World;

namespace Sim.Buildings;

/// <summary>
/// Registry of all building definitions. Loaded once at game start.
/// </summary>
public sealed class BuildingRegistry
{
    private readonly Dictionary<string, BuildingDef> _byId = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<BuildingCategory, List<BuildingDef>> _byCategory = new();

    public BuildingRegistry(IEnumerable<BuildingDef> definitions)
    {
        ArgumentNullException.ThrowIfNull(definitions);

        foreach (var def in definitions)
        {
            ArgumentNullException.ThrowIfNull(def);

            if (!_byId.TryAdd(def.Id, def))
                throw new ArgumentException($"Duplicate building id: {def.Id}", nameof(definitions));

            if (!_byCategory.TryGetValue(def.Category, out var list))
                _byCategory[def.Category] = list = new List<BuildingDef>();
            list.Add(def);
        }

        // Sort each category by id for deterministic ordering.
        foreach (var list in _byCategory.Values)
            list.Sort((a, b) => string.Compare(a.Id, b.Id, StringComparison.Ordinal));
    }

    /// <summary>All definitions keyed by id (case-insensitive).</summary>
    public IReadOnlyDictionary<string, BuildingDef> All => _byId;

    public BuildingDef this[string id] => _byId[id];

    public bool TryGet(string id, out BuildingDef? def) => _byId.TryGetValue(id, out def);

    /// <summary>Definitions in a category, sorted by id. Empty when the category has none.</summary>
    public IReadOnlyList<BuildingDef> GetByCategory(BuildingCategory category)
        => _byCategory.TryGetValue(category, out var list) ? list : Array.Empty<BuildingDef>();

    /// <summary>
    /// Returns every definition the player may place at the given site, applying
    /// tech unlocks and full placement validation.
    /// </summary>
    public IEnumerable<BuildingDef> GetAvailableAtSite(
        RegionTile tile,
        int centerOrdinal,
        BodyWorld world,
        SiteScorerConfig siteConfig,
        IReadOnlyDictionary<ulong, BuildingInstance>? existingBuildings = null,
        IReadOnlySet<string>? unlockedTechs = null)
    {
        foreach (var def in _byId.Values)
        {
            if (!string.IsNullOrEmpty(def.RequiredTech) &&
                (unlockedTechs is null || !unlockedTechs.Contains(def.RequiredTech)))
            {
                continue;
            }

            if (def.CanPlaceAt(tile, centerOrdinal, world, siteConfig, existingBuildings).Success)
                yield return def;
        }
    }

    /// <summary>
    /// Builds the hex → building occupancy map used by placement validation.
    /// </summary>
    /// <exception cref="ArgumentException">
    /// Two buildings claim the same hex, which means the region state is corrupt.
    /// </exception>
    public static IReadOnlyDictionary<ulong, BuildingInstance> BuildOccupancy(
        IEnumerable<BuildingInstance> buildings)
    {
        ArgumentNullException.ThrowIfNull(buildings);

        var occupancy = new Dictionary<ulong, BuildingInstance>();

        foreach (var building in buildings)
        {
            foreach (var hex in building.Footprint)
            {
                if (!occupancy.TryAdd((ulong)hex, building))
                {
                    throw new ArgumentException(
                        $"Hex {hex} is claimed by both '{occupancy[(ulong)hex].DefId}' and '{building.DefId}'.",
                        nameof(buildings));
                }
            }
        }

        return occupancy;
    }
}

/// <summary>
/// Predefined building definitions for the Moon.
/// </summary>
public static class MoonBuildings
{
    public static IReadOnlyList<BuildingDef> All { get; } = new[]
    {
        // ===== POWER =====
        new BuildingDef
        {
            Id = "solar_array_small",
            DisplayName = "Small Solar Array",
            Category = BuildingCategory.Power,
            Description = "Compact photovoltaic array. Low output, fits in tight spaces.",
            FootprintRadius = 0,
            MinSiteScore = 0.3,
            AllowedBiomeIds = new[] { "luna.mare", "luna.lowlands", "luna.midlands", "luna.highlands" },
            MaxSlopeM = 200.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 50_000 }, { "metals", 2.0 }, { "electronics", 0.5 } },
            BuildTimeHours = 24.0,
            PowerKw = -5.0, // generates 5 kW
            ThroughputPerHour = new Dictionary<string, double> { { "power", 5.0 } },
            VisualKey = "solar_array_small",
            FootprintColor = "#FFFF0088"
        },

        new BuildingDef
        {
            Id = "solar_array_large",
            DisplayName = "Large Solar Array",
            Category = BuildingCategory.Power,
            Description = "High-output solar farm. Requires flat, open terrain.",
            FootprintRadius = 1, // 7 hexes
            MinSiteScore = 0.5,
            AllowedBiomeIds = new[] { "luna.mare", "luna.lowlands" },
            MaxSlopeM = 100.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 500_000 }, { "metals", 20.0 }, { "electronics", 5.0 } },
            BuildTimeHours = 168.0,
            PowerKw = -50.0,
            ThroughputPerHour = new Dictionary<string, double> { { "power", 50.0 } },
            VisualKey = "solar_array_large",
            FootprintColor = "#FFAA0088"
        },

        new BuildingDef
        {
            Id = "rtg_generator",
            DisplayName = "RTG Generator",
            Category = BuildingCategory.Power,
            Description = "Radioisotope thermoelectric generator. Works anywhere, low output.",
            FootprintRadius = 0,
            MinSiteScore = 0.0,
            MaxSlopeM = 1000.0,
            AllowSingleCellCrater = true,
            AllowMultiCellCrater = true,
            Cost = new Dictionary<string, double> { { "credits", 2_000_000 }, { "radioactives", 10.0 } },
            BuildTimeHours = 72.0,
            PowerKw = -2.0,
            ThroughputPerHour = new Dictionary<string, double> { { "power", 2.0 } },
            RequiredTech = "nuclear_power",
            VisualKey = "rtg_generator",
            FootprintColor = "#FF440088"
        },

        // ===== LIFE SUPPORT =====
        new BuildingDef
        {
            Id = "air_reclaimer",
            DisplayName = "Air Reclaimer",
            Category = BuildingCategory.LifeSupport,
            Description = "Scrubs CO2 and replenishes O2 from habitat atmosphere.",
            FootprintRadius = 0,
            MinSiteScore = 0.2,
            Cost = new Dictionary<string, double> { { "credits", 100_000 }, { "metals", 3.0 }, { "chemicals", 1.0 } },
            BuildTimeHours = 48.0,
            PowerKw = 1.0,
            ThroughputPerHour = new Dictionary<string, double> { { "oxygen", 0.5 }, { "co2_scrubbed", 0.5 } },
            VisualKey = "air_reclaimer",
            FootprintColor = "#00FFAA88"
        },

        new BuildingDef
        {
            Id = "water_reclaimer",
            DisplayName = "Water Reclaimer",
            Category = BuildingCategory.LifeSupport,
            Description = "Recycles wastewater and humidity into potable water.",
            FootprintRadius = 0,
            MinSiteScore = 0.2,
            Cost = new Dictionary<string, double> { { "credits", 150_000 }, { "metals", 4.0 }, { "chemicals", 2.0 } },
            BuildTimeHours = 72.0,
            PowerKw = 2.0,
            ThroughputPerHour = new Dictionary<string, double> { { "water", 0.3 } },
            VisualKey = "water_reclaimer",
            FootprintColor = "#0088FF88"
        },

        // ===== HABITAT =====
        new BuildingDef
        {
            Id = "habitat_module_small",
            DisplayName = "Small Habitat Module",
            Category = BuildingCategory.Habitat,
            Description = "Pressurized living space for 4 crew. Includes basic life support.",
            FootprintRadius = 0,
            MinSiteScore = 0.3,
            MaxSlopeM = 300.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 5_000_000 }, { "metals", 50.0 }, { "electronics", 10.0 }, { "composites", 20.0 } },
            BuildTimeHours = 336.0,
            PowerKw = 5.0,
            ThroughputPerHour = new Dictionary<string, double> { { "oxygen", -0.2 }, { "water", -0.1 }, { "food", -0.15 } },
            StorageCapacity = new Dictionary<string, double> { { "oxygen", 500 }, { "water", 200 }, { "food", 100 } },
            CrewCapacity = 4,
            VisualKey = "habitat_small",
            FootprintColor = "#8844FF88"
        },

        new BuildingDef
        {
            Id = "habitat_module_large",
            DisplayName = "Large Habitat Module",
            Category = BuildingCategory.Habitat,
            Description = "Expanded habitat for 16 crew with recreation and medical facilities.",
            FootprintRadius = 1,
            MinSiteScore = 0.4,
            MaxSlopeM = 200.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 50_000_000 }, { "metals", 500.0 }, { "electronics", 100.0 }, { "composites", 200.0 } },
            BuildTimeHours = 1344.0,
            PowerKw = 20.0,
            ThroughputPerHour = new Dictionary<string, double> { { "oxygen", -0.8 }, { "water", -0.4 }, { "food", -0.6 } },
            StorageCapacity = new Dictionary<string, double> { { "oxygen", 5000 }, { "water", 2000 }, { "food", 1000 } },
            CrewCapacity = 16,
            RequiredTech = "advanced_habitats",
            VisualKey = "habitat_large",
            FootprintColor = "#AA00FF88"
        },

        // ===== STORAGE =====
        new BuildingDef
        {
            Id = "storage_warehouse",
            DisplayName = "Warehouse",
            Category = BuildingCategory.Storage,
            Description = "General-purpose bulk storage for resources and equipment.",
            FootprintRadius = 0,
            MinSiteScore = 0.1,
            MaxSlopeM = 500.0,
            Cost = new Dictionary<string, double> { { "credits", 200_000 }, { "metals", 10.0 } },
            BuildTimeHours = 72.0,
            PowerKw = 0.2,
            ThroughputPerHour = new Dictionary<string, double>(),
            StorageCapacity = new Dictionary<string, double> { { "metals", 1000 }, { "minerals", 1000 }, { "chemicals", 500 }, { "electronics", 100 }, { "composites", 200 } },
            VisualKey = "warehouse",
            FootprintColor = "#88888888"
        },

        new BuildingDef
        {
            Id = "cryogenic_tank_farm",
            DisplayName = "Cryogenic Tank Farm",
            Category = BuildingCategory.Storage,
            Description = "Insulated tanks for liquid oxygen, hydrogen, and methane.",
            FootprintRadius = 1,
            MinSiteScore = 0.2,
            MaxSlopeM = 100.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 10_000_000 }, { "metals", 200.0 }, { "composites", 50.0 } },
            BuildTimeHours = 672.0,
            PowerKw = 10.0,
            ThroughputPerHour = new Dictionary<string, double>(),
            StorageCapacity = new Dictionary<string, double> { { "oxygen", 50_000 }, { "hydrogen", 50_000 }, { "methane", 20_000 } },
            RequiredTech = "cryogenic_storage",
            VisualKey = "cryo_tanks",
            FootprintColor = "#00FFFF88"
        },

        // ===== PRODUCTION =====
        new BuildingDef
        {
            Id = "regolith_miner",
            DisplayName = "Regolith Miner",
            Category = BuildingCategory.Production,
            Description = "Excavates and processes raw regolith into metals and minerals.",
            FootprintRadius = 0,
            MinSiteScore = 0.2,
            MaxSlopeM = 400.0,
            Cost = new Dictionary<string, double> { { "credits", 2_000_000 }, { "metals", 50.0 }, { "electronics", 10.0 } },
            BuildTimeHours = 240.0,
            PowerKw = 15.0,
            ThroughputPerHour = new Dictionary<string, double> { { "regolith", -10.0 }, { "metals", 2.0 }, { "minerals", 3.0 }, { "oxygen", 1.0 } },
            VisualKey = "regolith_miner",
            FootprintColor = "#FF880088"
        },

        new BuildingDef
        {
            Id = "ice_extractor",
            DisplayName = "Ice Extractor",
            Category = BuildingCategory.Production,
            Description = "Drills and sublimates water ice from polar regolith.",
            FootprintRadius = 0,
            MinSiteScore = 0.4,
            AllowedBiomeIds = new[]
            {
                "luna.pole_north_lowlands", "luna.pole_north_midlands", "luna.pole_north_highlands",
                "luna.pole_south_lowlands", "luna.pole_south_midlands", "luna.pole_south_highlands"
            },
            MaxSlopeM = 200.0,
            Cost = new Dictionary<string, double> { { "credits", 5_000_000 }, { "metals", 100.0 }, { "electronics", 20.0 }, { "chemicals", 10.0 } },
            BuildTimeHours = 480.0,
            PowerKw = 25.0,
            ThroughputPerHour = new Dictionary<string, double> { { "regolith", -50.0 }, { "water", 10.0 }, { "oxygen", 2.0 }, { "hydrogen", 2.0 } },
            RequiredTech = "ice_extraction",
            VisualKey = "ice_extractor",
            FootprintColor = "#00AAFF88"
        },

        new BuildingDef
        {
            Id = "smelter",
            DisplayName = "Smelter",
            Category = BuildingCategory.Production,
            Description = "Refines raw metals into high-purity alloys and electronics-grade stock.",
            FootprintRadius = 1,
            MinSiteScore = 0.3,
            MaxSlopeM = 150.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 20_000_000 }, { "metals", 500.0 }, { "electronics", 50.0 }, { "composites", 100.0 } },
            BuildTimeHours = 1344.0,
            PowerKw = 100.0,
            ThroughputPerHour = new Dictionary<string, double> { { "metals", -10.0 }, { "refined_metals", 8.0 }, { "electronics", 1.0 } },
            RequiredTech = "smelting",
            VisualKey = "smelter",
            FootprintColor = "#FF444488"
        },

        // ===== SCIENCE =====
        new BuildingDef
        {
            Id = "science_lab",
            DisplayName = "Science Laboratory",
            Category = BuildingCategory.Science,
            Description = "General-purpose research facility. Generates science points.",
            FootprintRadius = 0,
            MinSiteScore = 0.3,
            MaxSlopeM = 300.0,
            Cost = new Dictionary<string, double> { { "credits", 10_000_000 }, { "metals", 50.0 }, { "electronics", 30.0 }, { "composites", 20.0 } },
            BuildTimeHours = 672.0,
            PowerKw = 10.0,
            ThroughputPerHour = new Dictionary<string, double>(),
            SciencePerHour = 5.0,
            CrewCapacity = 2,
            VisualKey = "science_lab",
            FootprintColor = "#FF00FF88"
        },

        new BuildingDef
        {
            Id = "telescope_array",
            DisplayName = "Radio Telescope Array",
            Category = BuildingCategory.Science,
            Description = "Far-side radio astronomy. Requires radio-quiet zone.",
            FootprintRadius = 1,
            MinSiteScore = 0.6,
            AllowedBiomeIds = new[] { "luna.highlands", "luna.pole_north_highlands", "luna.pole_south_highlands" },
            ForbiddenBiomeIds = new[] { "luna.mare" }, // Too much RF noise from Earth
            MaxSlopeM = 100.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 50_000_000 }, { "metals", 200.0 }, { "electronics", 100.0 }, { "composites", 50.0 } },
            BuildTimeHours = 2688.0,
            PowerKw = 5.0,
            ThroughputPerHour = new Dictionary<string, double>(),
            SciencePerHour = 50.0,
            RequiredTech = "radio_astronomy",
            VisualKey = "telescope_array",
            FootprintColor = "#8800FF88"
        },

        // ===== LOGISTICS =====
        new BuildingDef
        {
            Id = "landing_pad",
            DisplayName = "Landing Pad",
            Category = BuildingCategory.Logistics,
            Description = "Reinforced pad for cargo and crew landers. Includes guidance beacon.",
            FootprintRadius = 1,
            MinSiteScore = 0.4,
            MaxSlopeM = 50.0,
            RequireCraterFree = true,
            Cost = new Dictionary<string, double> { { "credits", 5_000_000 }, { "metals", 200.0 }, { "composites", 50.0 } },
            BuildTimeHours = 672.0,
            PowerKw = 2.0,
            ThroughputPerHour = new Dictionary<string, double>(),
            StorageCapacity = new Dictionary<string, double> { { "cargo", 1000 } },
            VisualKey = "landing_pad",
            FootprintColor = "#FFFFFF88"
        },

        new BuildingDef
        {
            Id = "rover_garage",
            DisplayName = "Rover Garage",
            Category = BuildingCategory.Logistics,
            Description = "Maintenance bay and charging station for up to 4 rovers.",
            FootprintRadius = 0,
            MinSiteScore = 0.2,
            MaxSlopeM = 200.0,
            Cost = new Dictionary<string, double> { { "credits", 1_000_000 }, { "metals", 30.0 }, { "electronics", 10.0 } },
            BuildTimeHours = 168.0,
            PowerKw = 5.0,
            ThroughputPerHour = new Dictionary<string, double> { { "power", -10.0 } }, // charges rovers
            StorageCapacity = new Dictionary<string, double> { { "rover_slots", 4 } },
            VisualKey = "rover_garage",
            FootprintColor = "#88FF0088"
        },

        // ===== SPECIAL =====
        new BuildingDef
        {
            Id = "comms_relay",
            DisplayName = "Comms Relay Tower",
            Category = BuildingCategory.Special,
            Description = "High-gain antenna for far-side and polar comms. Extends LOS.",
            FootprintRadius = 0,
            MinSiteScore = 0.2,
            MaxSlopeM = 500.0,
            AllowSingleCellCrater = true,
            Cost = new Dictionary<string, double> { { "credits", 2_000_000 }, { "metals", 20.0 }, { "electronics", 15.0 } },
            BuildTimeHours = 168.0,
            PowerKw = 3.0,
            ThroughputPerHour = new Dictionary<string, double>(),
            RequiredTech = "deep_space_comms",
            VisualKey = "comms_relay",
            FootprintColor = "#FF88FF88"
        }
    };
}
