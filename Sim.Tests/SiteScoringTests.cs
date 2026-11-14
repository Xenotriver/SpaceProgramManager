using System.Buffers.Binary;
using H3;
using H3.Model;
using Sim.Biomes;
using Sim.Hex;
using Sim.Movement;
using Sim.Site;
using Sim.World;
using Xunit;

namespace Sim.Tests;

public class SiteScoringTests
{
    private static readonly Biome BuildableBiome = new() { Id = "test.buildable", DisplayName = "Buildable", ElevationRank = 1, Buildable = true };
    private static readonly Biome NonBuildableBiome = new() { Id = "test.nonbuildable", DisplayName = "NonBuildable", ElevationRank = 2, Buildable = false };

    private static RegionTile CreateTestTile(BodyWorld world, params (byte biome, short elev, CraterKind crater)[] records)
    {
        var region = HexGrid.FromLatLon(0.0, 0.0, 3);
        var cells = HexGrid.FineHexesInRegion(region, 6);

        var entry = new RegionEntry(
            Index: (ulong)region,
            DominantBiomeCode: 0,
            HexCount: cells.Count,
            Offset: 0,
            Length: TileFormat.HeaderBytes + cells.Count * TileFormat.RecordBytes);

        var buffer = new byte[entry.Length];
        System.Text.Encoding.ASCII.GetBytes(TileFormat.Magic, buffer);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4), TileFormat.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(6), 6);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(8), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(12), (uint)cells.Count);

        for (int i = 0; i < cells.Count && i < records.Length; i++)
        {
            var off = TileFormat.HeaderBytes + i * TileFormat.RecordBytes;
            buffer[off] = records[i].biome;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(off + 1), records[i].elev);
            buffer[off + 3] = (byte)records[i].crater;
        }

        // Fill remaining with defaults
        for (int i = records.Length; i < cells.Count; i++)
        {
            var off = TileFormat.HeaderBytes + i * TileFormat.RecordBytes;
            buffer[off] = 0;
            BinaryPrimitives.WriteInt16LittleEndian(buffer.AsSpan(off + 1), (short)0);
            buffer[off + 3] = 0;
        }

        return new RegionTile(entry, buffer, 6, 3);
    }

    private static BodyWorld CreateTestWorld(params Biome[] biomes)
    {
        var biomeList = biomes.Length > 0 ? biomes : new[] { BuildableBiome };
        var registry = new BiomeRegistry(biomeList);

        var manifest = new BodyManifest
        {
            Format = "spm-region-tiles",
            Version = 1,
            GeneratedUtc = "2026-01-01T00:00:00Z",
            Body = new BodyInfo("test", "Test Body", 1737.4),
            FineResolution = 6,
            RegionResolution = 3,
            RecordBytes = 4,
            TileHeaderBytes = 20,
            TilesFile = "fine.bin",
            Biomes = biomeList,
            Regions = new List<RegionEntry>(),
            Sources = new Dictionary<string, string>()
        };

        // We can't easily construct a full BodyWorld without a real file,
        // so we'll test the scoring logic directly with mock data.
        return null!; // Tests below use direct factor computation
    }

    [Fact]
    public void SiteFactors_Composite_ZeroWhenNotBuildable()
    {
        var factors = new SiteFactors
        {
            Sunlight = 1.0,
            Comms = 1.0,
            Terrain = 1.0,
            Resources = 1.0,
            CraterClearance = 1.0,
            Buildable = false
        };

        Assert.Equal(0.0, factors.Composite(1, 1, 1, 1, 1));
    }

    [Fact]
    public void SiteFactors_Composite_WeightedAverage()
    {
        var factors = new SiteFactors
        {
            Sunlight = 1.0,
            Comms = 0.5,
            Terrain = 0.0,
            Resources = 0.0,
            CraterClearance = 0.0,
            Buildable = true
        };

        // Equal weights: (1.0 + 0.5 + 0 + 0 + 0) / 5 = 0.3
        Assert.Equal(0.3, factors.Composite(1, 1, 1, 1, 1), 10);

        // Custom weights: sunlight 2x, others 1x
        // (1.0*2 + 0.5*1 + 0*1 + 0*1 + 0*1) / (2+1+1+1+1) = 2.5/6 = 0.416...
        Assert.Equal(2.5 / 6.0, factors.Composite(2, 1, 1, 1, 1), 10);
    }

    [Fact]
    public void SiteScore_Viable_RequiresBuildableAndThreshold()
    {
        var buildable = new SiteFactors { Buildable = true, Sunlight = 0.5, Comms = 0.5, Terrain = 0.5, Resources = 0.5, CraterClearance = 0.5 };
        var nonBuildable = new SiteFactors { Buildable = false, Sunlight = 1.0, Comms = 1.0, Terrain = 1.0, Resources = 1.0, CraterClearance = 1.0 };

        var score1 = new SiteScore { Center = H3Index.FromLatLng(new LatLng(0, 0), 6), Footprint = Array.Empty<H3Index>(), Factors = buildable };
        var score2 = new SiteScore { Center = H3Index.FromLatLng(new LatLng(0, 0), 6), Footprint = Array.Empty<H3Index>(), Factors = nonBuildable };
        var score3 = new SiteScore { Center = H3Index.FromLatLng(new LatLng(0, 0), 6), Footprint = Array.Empty<H3Index>(), Factors = new SiteFactors { Buildable = true, Sunlight = 0.1, Comms = 0.1, Terrain = 0.1, Resources = 0.1, CraterClearance = 0.1 } };

        Assert.True(score1.Viable);  // 0.5 > 0.25
        Assert.False(score2.Viable); // not buildable
        Assert.False(score3.Viable); // 0.1 < 0.25
    }

    [Fact]
    public void SiteWeights_Presets_HaveCorrectValues()
    {
        Assert.Equal(2.0, SiteWeights.PowerHeavy.Sunlight);
        Assert.Equal(1.0, SiteWeights.PowerHeavy.Resources);

        Assert.Equal(2.0, SiteWeights.CommsHeavy.Comms);

        Assert.Equal(1.5, SiteWeights.SafetyHeavy.Terrain);
        Assert.Equal(1.5, SiteWeights.SafetyHeavy.CraterClearance);
    }

    [Fact]
    public void ResourceDeposit_HasExpectedProperties()
    {
        var deposit = new ResourceDeposit
        {
            LatitudeDeg = -89.0,
            LongitudeDeg = 0.0,
            Type = "water_ice",
            Richness = 0.8
        };

        Assert.Equal(-89.0, deposit.LatitudeDeg);
        Assert.Equal(0.0, deposit.LongitudeDeg);
        Assert.Equal("water_ice", deposit.Type);
        Assert.Equal(0.8, deposit.Richness);
    }

    [Fact]
    public void SiteScorerConfig_Defaults_AreReasonable()
    {
        var config = new SiteScorerConfig { BodyRadiusKm = 1737.4 };

        Assert.Equal(1, config.FootprintRadius);
        Assert.Equal(1.54, config.AxialTiltDeg);
        Assert.Equal(5.0, config.MinSunElevationDeg);
        Assert.Empty(config.ResourceDeposits);
        Assert.Equal(0.25, config.ViabilityThreshold);
        Assert.Equal(SiteWeights.Default.Sunlight, config.Weights.Sunlight);
    }
}