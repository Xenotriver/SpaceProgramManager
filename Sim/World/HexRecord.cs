using Sim.Movement;

namespace Sim.World;

/// <summary>
/// One fine-hex record inside a region tile: biome code, elevation and crater overlay.
/// </summary>
public readonly record struct HexRecord(byte BiomeCode, short ElevationM, CraterKind Crater)
{
    /// <summary>Size of one record in bytes (<see cref="TileFormat.RecordBytes"/>).</summary>
    public const int Size = TileFormat.RecordBytes;

    /// <summary>Reads a record from the start of <paramref name="source"/>.</summary>
    public static HexRecord Read(ReadOnlySpan<byte> source)
    {
        if (source.Length < Size)
            throw new ArgumentException($"A hex record needs {Size} bytes, got {source.Length}.", nameof(source));

        var biome = source[0];
        var elevation = (short)(source[1] | (source[2] << 8));
        var crater = (CraterKind)source[3];

        if (!Enum.IsDefined(crater))
            throw new InvalidDataException($"Unknown crater code {source[3]}.");

        return new HexRecord(biome, elevation, crater);
    }
}
