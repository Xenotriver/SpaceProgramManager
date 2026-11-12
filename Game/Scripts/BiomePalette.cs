using Godot;

/// <summary>
/// Presentation-only colours for biomes. The simulation never sees these.
/// Unknown biome ids get a stable colour derived from the id, so new bodies work
/// without touching this file.
/// </summary>
public static class BiomePalette
{
    public static Color For(string biomeId) => biomeId switch
    {
        "luna.mare" => Color.FromHtml("#2f3e4e"),
        "luna.lowlands" => Color.FromHtml("#5c6b5e"),
        "luna.midlands" => Color.FromHtml("#8c7f63"),
        "luna.highlands" => Color.FromHtml("#cfc9b8"),

        "luna.pole_north_lowlands" => Color.FromHtml("#4e6c7c"),
        "luna.pole_north_midlands" => Color.FromHtml("#7e929b"),
        "luna.pole_north_highlands" => Color.FromHtml("#e4e9ec"),

        "luna.pole_south_lowlands" => Color.FromHtml("#566b70"),
        "luna.pole_south_midlands" => Color.FromHtml("#8a979b"),
        "luna.pole_south_highlands" => Color.FromHtml("#ede7e2"),

        _ => StableColor(biomeId),
    };

    private static Color StableColor(string id)
    {
        var hash = 2166136261u;
        foreach (var c in id)
        {
            hash ^= c;
            hash *= 16777619u;
        }

        return Color.FromHsv((hash % 360) / 360f, 0.45f, 0.75f);
    }
}
