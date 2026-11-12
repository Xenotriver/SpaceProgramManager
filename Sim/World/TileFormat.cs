namespace Sim.World;

/// <summary>
/// Constants for the baked region-tile format (v1).  Mirrors
/// <c>pipeline/spm/config.py</c>; bump <see cref="Version"/> on any layout change
/// and keep both sides in sync.
/// </summary>
public static class TileFormat
{
    /// <summary>Magic bytes at the start of every region tile (<c>"SPMF"</c>).</summary>
    public const string Magic = "SPMF";

    /// <summary>Current tile format version.</summary>
    public const int Version = 1;

    /// <summary>Per-region header: magic(4) + version(2) + fineRes(2) + regionRes(2) + reserved(2) + hexCount(4) + reserved(4).</summary>
    public const int HeaderBytes = 20;

    /// <summary>Per-hex record: biome(u8) + elevation_m(i16) + crater(u8).</summary>
    public const int RecordBytes = 4;
}
