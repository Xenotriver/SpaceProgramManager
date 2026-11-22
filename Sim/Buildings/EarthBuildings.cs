using System.Collections.Generic;
using System.Linq;
using Sim.Biomes;

namespace Sim.Buildings;

/// <summary>
/// Earth's shared building catalogue with water-capable logistics added.
/// Normal installations are translated from Moon biome ids and remain water-forbidden
/// through <see cref="WaterPlacement.Forbidden"/> defaults.
/// </summary>
public static class EarthBuildings
{
    public static IReadOnlyList<BuildingDef> All { get; } = Build();

    private static IReadOnlyList<BuildingDef> Build()
    {
        var translated = MoonBuildings.All
            .Select(def => def with
            {
                AllowedBiomeIds = Translate(def.AllowedBiomeIds),
                ForbiddenBiomeIds = Translate(def.ForbiddenBiomeIds),
            })
            .ToList();

        translated.Add(new BuildingDef
        {
            Id = "offshore_pad",
            DisplayName = "Offshore Launch Pad",
            Category = BuildingCategory.Logistics,
            Description = "Sea-based launch and landing platform for orbital vehicles.",
            FootprintRadius = 1,
            MinSiteScore = 0.2,
            BuildOnWater = WaterPlacement.Required,
            MaxSlopeM = 100.0,
            Cost = new Dictionary<string, double>
            {
                ["credits"] = 5_000_000.0,
                ["metals"] = 180.0,
                ["composites"] = 60.0,
            },
            BuildTimeHours = 672.0,
            PowerKw = 2.0,
            ThroughputPerHour = new Dictionary<string, double>(),
            StorageCapacity = new Dictionary<string, double> { ["cargo"] = 1000.0 },
            VisualKey = "offshore_pad",
            FootprintColor = "#66CCFF88",
        });

        translated.Add(new BuildingDef
        {
            Id = "barge_port",
            DisplayName = "Barge Port",
            Category = BuildingCategory.Logistics,
            Description = "Floating berth and charging bay for up to 4 barges.",
            FootprintRadius = 0,
            MinSiteScore = 0.2,
            BuildOnWater = WaterPlacement.Required,
            MaxSlopeM = 200.0,
            Cost = new Dictionary<string, double>
            {
                ["credits"] = 1_000_000.0,
                ["metals"] = 30.0,
                ["electronics"] = 10.0,
            },
            BuildTimeHours = 168.0,
            PowerKw = 5.0,
            ThroughputPerHour = new Dictionary<string, double> { ["power"] = -10.0 },
            StorageCapacity = new Dictionary<string, double> { ["barge_slots"] = 4.0 },
            VisualKey = "barge_port",
            FootprintColor = "#88DDFF88",
        });

        return translated;
    }

    private static string[] Translate(IReadOnlyList<string> biomeIds)
    {
        var translated = new List<string>(biomeIds.Count);
        foreach (var biomeId in biomeIds)
        {
            foreach (var mapped in Translate(biomeId))
            {
                if (!translated.Contains(mapped, StringComparer.Ordinal))
                    translated.Add(mapped);
            }
        }

        return translated.ToArray();
    }

    // Moon's coarse terrain bands map to several Earth land-cover biomes so the
    // shared catalogue remains useful outside plains. Water classes are never
    // mapped here; only the two explicit logistics buildings permit water.
    private static IReadOnlyList<string> Translate(string biomeId) => biomeId.ToLowerInvariant() switch
    {
        "luna.mare" => new[] { "earth.plains", "earth.forest", "earth.taiga" },
        "luna.lowlands" => new[] { "earth.shore", "earth.plains" },
        "luna.midlands" => new[] { "earth.hills", "earth.forest", "earth.taiga" },
        "luna.highlands" => new[] { "earth.mountains", "earth.tundra" },
        "luna.pole_north_lowlands" => new[] { "earth.pole_north" },
        "luna.pole_north_midlands" => new[] { "earth.pole_north" },
        "luna.pole_north_highlands" => new[] { "earth.pole_north" },
        "luna.pole_south_lowlands" => new[] { "earth.pole_south" },
        "luna.pole_south_midlands" => new[] { "earth.pole_south" },
        "luna.pole_south_highlands" => new[] { "earth.pole_south" },
        _ => throw new InvalidOperationException(
            $"No Earth biome equivalent for Moon biome '{biomeId}'."),
    };
}
