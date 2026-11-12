using System.Buffers.Binary;
using H3;
using Sim.Hex;
using Sim.Movement;

namespace Sim.World;

/// <summary>
/// One decoded region tile: a validated header plus one <see cref="HexRecord"/> per
/// fine hex, in H3 child order.
///
/// The tile is self-contained (it owns its byte buffer), so it can be cached and
/// passed around without keeping the tile file open.
/// </summary>
public sealed class RegionTile
{
    private readonly byte[] _records;

    public RegionTile(
        RegionEntry entry,
        ReadOnlySpan<byte> tile,
        int fineResolution,
        int regionResolution)
    {
        ArgumentNullException.ThrowIfNull(entry);
        Entry = entry;

        if (tile.Length != entry.Length)
            throw new InvalidDataException(
                $"Region {entry.Index}: expected {entry.Length} B, got {tile.Length} B.");

        if (tile.Length < TileFormat.HeaderBytes)
            throw new InvalidDataException(
                $"Region {entry.Index}: tile is shorter than the {TileFormat.HeaderBytes} B header.");

        var magic = System.Text.Encoding.ASCII.GetString(tile[..4]);
        if (magic != TileFormat.Magic)
            throw new InvalidDataException(
                $"Region {entry.Index}: bad magic '{magic}', expected '{TileFormat.Magic}'.");

        var version = BinaryPrimitives.ReadUInt16LittleEndian(tile[4..]);
        if (version != TileFormat.Version)
            throw new InvalidDataException(
                $"Region {entry.Index}: tile version {version}, runtime supports {TileFormat.Version}.");

        var fineRes = BinaryPrimitives.ReadUInt16LittleEndian(tile[6..]);
        if (fineRes != fineResolution)
            throw new InvalidDataException(
                $"Region {entry.Index}: tile fine resolution {fineRes}, manifest says {fineResolution}.");

        var regionRes = BinaryPrimitives.ReadUInt16LittleEndian(tile[8..]);
        if (regionRes != regionResolution)
            throw new InvalidDataException(
                $"Region {entry.Index}: tile region resolution {regionRes}, manifest says {regionResolution}.");

        var hexCount = BinaryPrimitives.ReadUInt32LittleEndian(tile[12..]);
        if (hexCount != (uint)entry.HexCount)
            throw new InvalidDataException(
                $"Region {entry.Index}: tile holds {hexCount} hexes, manifest says {entry.HexCount}.");

        var expectedLength = TileFormat.HeaderBytes + entry.HexCount * HexRecord.Size;
        if (tile.Length != expectedLength)
            throw new InvalidDataException(
                $"Region {entry.Index}: {entry.HexCount} hexes need {expectedLength} B, got {tile.Length} B.");

        _records = tile[TileFormat.HeaderBytes..].ToArray();

        Region = new H3Index(entry.Index);
        if (Region.Resolution != regionResolution)
            throw new InvalidDataException(
                $"Region {entry.Index}: index has resolution {Region.Resolution}, expected {regionResolution}.");

        var cells = HexGrid.FineHexesInRegion(Region, fineResolution);
        if (cells.Count != entry.HexCount)
            throw new InvalidDataException(
                $"Region {entry.Index}: H3 yields {cells.Count} fine hexes, manifest says {entry.HexCount}.");

        Cells = cells;
    }

    /// <summary>Manifest entry this tile was loaded from.</summary>
    public RegionEntry Entry { get; }

    /// <summary>The region (coarse parent) cell.</summary>
    public H3Index Region { get; }

    /// <summary>Fine hexes in this region, in tile-record order.</summary>
    public IReadOnlyList<H3Index> Cells { get; }

    public int HexCount => Entry.HexCount;

    /// <summary>Dominant biome code of the whole region.</summary>
    public byte DominantBiomeCode => Entry.DominantBiomeCode;

    /// <summary>Record for the fine hex at <paramref name="index"/>.</summary>
    public HexRecord this[int index]
    {
        get
        {
            if ((uint)index >= (uint)Entry.HexCount)
                throw new ArgumentOutOfRangeException(nameof(index), index, $"Region has {Entry.HexCount} hexes.");

            return HexRecord.Read(_records.AsSpan(index * HexRecord.Size, HexRecord.Size));
        }
    }

    /// <summary>Biome code of the fine hex at <paramref name="index"/>.</summary>
    public byte BiomeCode(int index) => this[index].BiomeCode;

    /// <summary>Elevation in metres of the fine hex at <paramref name="index"/>.</summary>
    public short ElevationM(int index) => this[index].ElevationM;

    /// <summary>Crater overlay of the fine hex at <paramref name="index"/>.</summary>
    public CraterKind Crater(int index) => this[index].Crater;

    /// <summary>Resolves the biome of the fine hex at <paramref name="index"/>.</summary>
    public Biomes.Biome Biome(int index, BiomeRegistry biomes)
    {
        ArgumentNullException.ThrowIfNull(biomes);
        return biomes[BiomeCode(index)];
    }
}
