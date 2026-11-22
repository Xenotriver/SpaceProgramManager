using Sim.World;

/// <summary>
/// Presentation-only elevation scaling shared by the terrain views.
///
/// The dioramas normalise each view's observed elevation span to a fixed share
/// of the screen. That is correct for bodies with large local relief, but it can
/// turn a nearly flat synthetic region into a field of spikes: a 100 m jitter
/// would still become 18% of the view width. A body-aware span floor keeps flat
/// terrain flat without changing the real Moon/Mars behaviour.
/// </summary>
public static class ReliefMath
{
    /// <summary>Minimum span used for Earth's broad, gently rolling surface.</summary>
    private const double EarthMinSpanM = 2500.0;

    /// <summary>Effective elevation span for palette and geometry scaling.</summary>
    public static double EffectiveSpan(double observedSpan, string? bodyKey)
    {
        var floor = string.Equals(bodyKey, KnownBodies.Earth, StringComparison.OrdinalIgnoreCase)
            ? EarthMinSpanM
            : 1.0;
        return Math.Max(floor, Math.Max(1.0, observedSpan));
    }
}
