namespace Sim.Time;

/// <summary>
/// Small, seeded, platform-independent PRNG (SplitMix64). State is an explicit
/// number so a campaign can persist and restore it: the same seed and the same
/// call count always produce the same sequence.
///
/// Engine-free and allocation-free; M1 only stores it, later milestones
/// (reliability rolls, events) draw from it.
/// </summary>
public sealed class DeterministicRandom
{
    private const ulong Increment = 0x9E3779B97F4A7C15UL;
    private const ulong NonZeroFallback = 0xD1B54A32D192ED03UL;

    private ulong _state;

    public DeterministicRandom(ulong seed)
    {
        Seed = seed;
        _state = seed;
    }

    /// <summary>Seed the sequence was created with.</summary>
    public ulong Seed { get; }

    /// <summary>
    /// Opaque current state. Persist this instead of the call count; assigning it
    /// resumes the sequence exactly.
    /// </summary>
    public ulong State
    {
        get => _state;
        set => _state = value;
    }

    /// <summary>Next 64 random bits.</summary>
    public ulong NextULong()
    {
        _state += Increment;
        var z = _state;
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }

    /// <summary>Uniform double in [0, 1).</summary>
    public double NextDouble() => (NextULong() >> 11) * (1.0 / (1UL << 53));

    /// <summary>Uniform integer in [0, maxExclusive).</summary>
    public int NextInt(int maxExclusive)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxExclusive);

        // Rejection sampling keeps the distribution flat without floating point.
        var bound = (ulong)maxExclusive;
        var limit = ulong.MaxValue - ulong.MaxValue % bound;
        ulong value;
        do
        {
            value = NextULong();
        }
        while (value >= limit);

        return (int)(value % bound);
    }

    /// <summary>True with the given probability.</summary>
    public bool Chance(double probability)
        => probability > 0.0 && NextDouble() < probability;

    /// <summary>A non-zero seed derived from content, for migrations and tests.</summary>
    public static ulong SeedFrom(ulong value)
    {
        var mixed = value == 0 ? NonZeroFallback : value;
        mixed ^= mixed >> 33;
        mixed *= 0xFF51AFD7ED558CCDUL;
        mixed ^= mixed >> 33;
        mixed *= 0xC4CEB9FE1A85EC53UL;
        mixed ^= mixed >> 33;
        return mixed == 0 ? NonZeroFallback : mixed;
    }
}
