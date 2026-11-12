using System.Diagnostics.CodeAnalysis;
using Sim.Biomes;

namespace Sim.World;

/// <summary>
/// Per-body biome table: tile code → <see cref="Biome"/>.  Built from the body
/// manifest, so ranks and buildability come from the bake rather than being
/// hardcoded per body.
/// </summary>
public sealed class BiomeRegistry
{
    private readonly Biome[] _byCode;
    private readonly Dictionary<string, Biome> _byId;

    public BiomeRegistry(IReadOnlyList<Biome> biomesByCode)
    {
        ArgumentNullException.ThrowIfNull(biomesByCode);

        if (biomesByCode.Count == 0)
            throw new ArgumentException("At least one biome is required.", nameof(biomesByCode));

        _byCode = biomesByCode.ToArray();
        _byId = new Dictionary<string, Biome>(_byCode.Length, StringComparer.Ordinal);

        for (var code = 0; code < _byCode.Length; code++)
        {
            var biome = _byCode[code]
                ?? throw new ArgumentException($"Biome code {code} is null.", nameof(biomesByCode));

            if (!_byId.TryAdd(biome.Id, biome))
                throw new ArgumentException($"Duplicate biome id '{biome.Id}'.", nameof(biomesByCode));
        }
    }

    /// <summary>Number of biome codes for this body.</summary>
    public int Count => _byCode.Length;

    /// <summary>Biomes ordered by tile code.</summary>
    public IReadOnlyList<Biome> All => _byCode;

    /// <summary>Returns the biome for a tile code.</summary>
    public Biome this[int code]
        => TryGet(code, out var biome)
            ? biome
            : throw new ArgumentOutOfRangeException(nameof(code), code, $"Body has {_byCode.Length} biome codes.");

    /// <summary>Looks up a biome by tile code.</summary>
    public bool TryGet(int code, [NotNullWhen(true)] out Biome? biome)
    {
        if ((uint)code < (uint)_byCode.Length)
        {
            biome = _byCode[code];
            return true;
        }

        biome = null;
        return false;
    }

    /// <summary>Looks up a biome by its stable id, e.g. <c>luna.mare</c>.</summary>
    public bool TryGetById(string id, [NotNullWhen(true)] out Biome? biome)
        => _byId.TryGetValue(id, out biome);
}
