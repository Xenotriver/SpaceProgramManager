using System;
using Godot;

/// <summary>
/// Presentation-only colour ramps for the region overlays. The simulation never
/// sees these; they only translate normalised values into readable colours.
/// </summary>
public static class TerrainPalette
{
    // Low ground reads as cold shadow; high ground reads as bright regolith.
    private static readonly Color[] Stops =
    {
        Color.FromHtml("#12202e"),
        Color.FromHtml("#1f3a4d"),
        Color.FromHtml("#3d5a5e"),
        Color.FromHtml("#7a7a63"),
        Color.FromHtml("#b0a88f"),
        Color.FromHtml("#e8e2d2"),
    };

    /// <summary>Colour for an elevation normalised to 0..1 within the region.</summary>
    public static Color For(double t) => PaletteRamp.Sample(Stops, t);
}

/// <summary>
/// Site-score heatmap: red is hostile, amber is marginal, green is good.
/// Non-buildable hexes are deliberately flat and dark so they read as "no".
/// </summary>
public static class SiteScorePalette
{
    private static readonly Color[] Stops =
    {
        Color.FromHtml("#3b0d12"),
        Color.FromHtml("#7a1f1a"),
        Color.FromHtml("#a85a1c"),
        Color.FromHtml("#c9a227"),
        Color.FromHtml("#7fb04a"),
        Color.FromHtml("#2f9e6b"),
    };

    private static readonly Color Blocked = Color.FromHtml("#1a1c20");

    /// <summary>Colour for a normalised composite score; blocked sites stay dark.</summary>
    public static Color For(double t, bool buildable)
        => buildable ? PaletteRamp.Sample(Stops, t) : Blocked;
}

internal static class PaletteRamp
{
    public static Color Sample(Color[] stops, double t)
    {
        if (stops.Length == 0)
            return Colors.Magenta;

        t = Math.Clamp(t, 0.0, 1.0);
        if (stops.Length == 1)
            return stops[0];

        var scaled = t * (stops.Length - 1);
        var index = (int)scaled;
        if (index >= stops.Length - 1)
            return stops[^1];

        return stops[index].Lerp(stops[index + 1], (float)(scaled - index));
    }
}
