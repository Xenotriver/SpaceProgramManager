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

        "earth.ocean" => Color.FromHtml("#1d4f73"),
        "earth.lake" => Color.FromHtml("#3a82a8"),
        "earth.river" => Color.FromHtml("#4b9fc7"),
        "earth.shore" => Color.FromHtml("#c8bb91"),
        "earth.plains" => Color.FromHtml("#6f8f56"),
        "earth.hills" => Color.FromHtml("#7d7b5a"),
        "earth.mountains" => Color.FromHtml("#9a958c"),
        "earth.forest" => Color.FromHtml("#446b3e"),
        "earth.taiga" => Color.FromHtml("#3e6257"),
        "earth.tundra" => Color.FromHtml("#8f9a87"),
        "earth.pole_north" => Color.FromHtml("#dce9ed"),
        "earth.pole_south" => Color.FromHtml("#e8eef0"),
        "earth.volcanic" => Color.FromHtml("#493b36"),

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
