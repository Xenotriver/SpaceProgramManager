using System.Collections.Generic;
using System.Linq;

namespace Sim.World;

/// <summary>
/// Bodies the runtime knows how to load. Only baked bodies are "ready"; the
/// wider list used by the system map lives in the UI layer.
/// </summary>
public static class KnownBodies
{
    public const string Moon = "moon";
    public const string Mars = "mars";

    public static IReadOnlyList<string> ReadyKeys { get; } = new[] { Moon, Mars };

    public static bool IsReady(string? bodyKey)
        => bodyKey is not null && ReadyKeys.Contains(bodyKey, StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the canonical key for a case-insensitive body name.</summary>
    public static string? TryNormalize(string? bodyKey)
        => ReadyKeys.FirstOrDefault(key =>
            string.Equals(key, bodyKey?.Trim(), StringComparison.OrdinalIgnoreCase));

    public static string DisplayName(string bodyKey)
        => TryNormalize(bodyKey) switch
        {
            Moon => "Moon",
            Mars => "Mars",
            _ => bodyKey,
        };
}
