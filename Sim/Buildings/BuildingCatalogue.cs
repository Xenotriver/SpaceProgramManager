using System.Collections.Generic;
using System.Linq;

namespace Sim.Buildings;

/// <summary>
/// Body-specific building definitions.
///
/// Building ids are deliberately body-independent because they are persisted in
/// saves.  Only biome constraint lists vary: Mars reuses every Moon definition
/// with <c>luna.*</c> biome ids translated to their <c>mars.*</c> equivalents.
/// </summary>
public static class BuildingCatalogue
{
    private static readonly IReadOnlyList<BuildingDef> MarsDefinitions = BuildMarsDefinitions();

    /// <summary>Definitions for <paramref name="bodyKey"/> (case-insensitive).</summary>
    public static IReadOnlyList<BuildingDef> For(string bodyKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bodyKey);

        return bodyKey.ToLowerInvariant() switch
        {
            "moon" => MoonBuildings.All,
            "mars" => MarsDefinitions,
            _ => throw new ArgumentException(
                $"No building catalogue for body '{bodyKey}'.", nameof(bodyKey)),
        };
    }

    /// <summary>Convenience factory for the registry used by a body.</summary>
    public static BuildingRegistry RegistryFor(string bodyKey) => new(For(bodyKey));

    private static IReadOnlyList<BuildingDef> BuildMarsDefinitions()
        => MoonBuildings.All
            .Select(def => def with
            {
                AllowedBiomeIds = Translate(def.AllowedBiomeIds),
                ForbiddenBiomeIds = Translate(def.ForbiddenBiomeIds),
            })
            .ToArray();

    private static string[] Translate(IReadOnlyList<string> biomeIds)
    {
        var translated = new List<string>(biomeIds.Count);

        foreach (var biomeId in biomeIds)
        {
            var mapped = Translate(biomeId);
            if (!translated.Contains(mapped, StringComparer.Ordinal))
                translated.Add(mapped);
        }

        return translated.ToArray();
    }

    private static string Translate(string biomeId) => biomeId.ToLowerInvariant() switch
    {
        // Mare is the Moon's smooth basaltic plain; Mars' closest building
        // analogue is the northern lowlands, not the steep volcanic provinces.
        "luna.mare" => "mars.lowlands",
        "luna.lowlands" => "mars.lowlands",
        "luna.midlands" => "mars.midlands",
        "luna.highlands" => "mars.highlands",
        // Mars has one polar cap per hemisphere instead of three elevation
        // tiers, so all Moon polar tiers collapse onto the same biome.
        "luna.pole_north_lowlands" => "mars.pole_north",
        "luna.pole_north_midlands" => "mars.pole_north",
        "luna.pole_north_highlands" => "mars.pole_north",
        "luna.pole_south_lowlands" => "mars.pole_south",
        "luna.pole_south_midlands" => "mars.pole_south",
        "luna.pole_south_highlands" => "mars.pole_south",
        _ => throw new InvalidOperationException(
            $"No Mars biome equivalent for Moon biome '{biomeId}'."),
    };
}
