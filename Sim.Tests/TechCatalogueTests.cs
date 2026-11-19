using Sim.Buildings;
using Sim.Campaign;
using Xunit;

namespace Sim.Tests;

/// <summary>
/// Static tech-tree invariants. Gameplay purchase paths live in SimulationTests.
/// </summary>
public class TechCatalogueTests
{
    [Fact]
    public void Catalogue_HasUniqueIdsKnownPrerequisitesAndConsistentUnlocks()
    {
        Assert.NotEmpty(TechCatalogue.All);
        Assert.Equal(
            TechCatalogue.All.Count,
            TechCatalogue.All.Select(tech => tech.Id).Distinct(StringComparer.Ordinal).Count());

        foreach (var tech in TechCatalogue.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(tech.DisplayName));
            Assert.True(tech.Tier >= 1);
            Assert.True(tech.ScienceCost > 0.0);
            Assert.DoesNotContain(tech.Id, tech.Prerequisites);

            foreach (var prerequisite in tech.Prerequisites)
                Assert.True(TechCatalogue.IsKnown(prerequisite), $"unknown prerequisite {prerequisite}");
        }

        var unlockedBuildings = TechCatalogue.All.SelectMany(tech => tech.UnlockedBuildingIds).ToList();
        Assert.Equal(unlockedBuildings.Count, unlockedBuildings.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Catalogue_CoversEveryBuildingTechRequirement()
    {
        var definitions = BuildingCatalogue.For("moon")
            .Concat(BuildingCatalogue.For("mars"));

        foreach (var def in definitions)
        {
            if (string.IsNullOrEmpty(def.RequiredTech))
                continue;

            Assert.True(TechCatalogue.IsKnown(def.RequiredTech), $"no tech for {def.Id}");
            Assert.Contains(def.Id, TechCatalogue.All
                .Single(tech => tech.Id == def.RequiredTech).UnlockedBuildingIds);
        }
    }

    [Fact]
    public void Catalogue_IsOrderedByTierThenId()
    {
        var expected = TechCatalogue.All
            .OrderBy(tech => tech.Tier)
            .ThenBy(tech => tech.Id, StringComparer.Ordinal)
            .Select(tech => tech.Id);

        Assert.Equal(expected, TechCatalogue.All.Select(tech => tech.Id));
    }
}
