using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;
using Sim.World;

/// <summary>
/// Player-facing preferences, stored outside any campaign slot. Losing this file
/// only costs a menu default; campaigns are unaffected.
/// </summary>
public sealed class GameSettings
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Directory holding every save slot.</summary>
    public string SaveRoot { get; set; } = DefaultSaveRoot;

    /// <summary>Slot opened most recently; drives Continue.</summary>
    [JsonIgnore]
    public string? LastSlotId
    {
        get => LastSlotFor(KnownBodies.Moon);
        set => SetLastSlot(KnownBodies.Moon, value, save: false);
    }

    /// <summary>Last slot per body. Pre-M4 <see cref="LegacyLastSlotId"/> migrates into the Moon entry.</summary>
    public Dictionary<string, string> LastSlotIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Pre-M4 single slot id, read once during <see cref="Load"/>.</summary>
    [JsonPropertyName("lastSlotId")]
    public string? LegacyLastSlotId { get; set; }

    /// <summary>Default relief exaggeration for the colony view.</summary>
    public float ReliefScale { get; set; } = 1f;

    /// <summary>Print frame timings to the log.</summary>
    public bool ShowFps { get; set; }

    /// <summary>
    /// Opt-in visual LOD preview. Stored outside campaign slots so it never
    /// affects saves, the bake fingerprint or simulation state.
    /// </summary>
    public bool EnableVisualLod { get; set; }

    /// <summary>Default save root under Godot's per-user data directory.</summary>
    public static string DefaultSaveRoot => ProjectSettings.GlobalizePath("user://saves");

    /// <summary>Default settings file.</summary>
    public static string DefaultPath => ProjectSettings.GlobalizePath("user://settings.json");

    /// <summary>Returns the last slot for <paramref name="bodyKey"/>, if one is known.</summary>
    public string? LastSlotFor(string bodyKey)
    {
        var key = NormalizeKey(bodyKey);
        return key is not null
               && LastSlotIds.TryGetValue(key, out var slotId)
               && !string.IsNullOrWhiteSpace(slotId)
            ? slotId
            : null;
    }

    /// <summary>Remembers a slot for <paramref name="bodyKey"/> and writes settings.</summary>
    public void SetLastSlot(string bodyKey, string? slotId, bool save = true)
    {
        var key = NormalizeKey(bodyKey);
        if (key is null)
            return;

        if (string.IsNullOrWhiteSpace(slotId))
            LastSlotIds.Remove(key);
        else
            LastSlotIds[key] = slotId;

        if (save)
            Save();
    }

    private static string? NormalizeKey(string bodyKey)
        => string.IsNullOrWhiteSpace(bodyKey) ? null : bodyKey.Trim().ToLowerInvariant();

    /// <summary>Loads settings, returning defaults when the file is missing or invalid.</summary>
    public static GameSettings Load(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            if (!File.Exists(path))
                return new GameSettings();

            var settings = JsonSerializer.Deserialize<GameSettings>(
                File.ReadAllText(path), JsonOptions);

            if (settings is null)
                return new GameSettings();

            if (string.IsNullOrWhiteSpace(settings.SaveRoot))
                settings.SaveRoot = DefaultSaveRoot;
            else
                settings.SaveRoot = ProjectSettings.GlobalizePath(settings.SaveRoot);

            // settings.json is outside any campaign slot, so this migration is
            // safe: the old single slot always referred to the Moon bake.
            var slots = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, slotId) in settings.LastSlotIds)
            {
                var normalizedKey = NormalizeKey(key);
                if (normalizedKey is not null && !string.IsNullOrWhiteSpace(slotId))
                    slots[normalizedKey] = slotId.Trim();
            }
            settings.LastSlotIds = slots;

            if (!slots.ContainsKey(KnownBodies.Moon)
                && !string.IsNullOrWhiteSpace(settings.LegacyLastSlotId))
            {
                slots[KnownBodies.Moon] = settings.LegacyLastSlotId.Trim();
            }
            settings.LegacyLastSlotId = null;

            return settings;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"settings: {path}: {ex.Message}");
            return new GameSettings();
        }
    }

    /// <summary>Writes settings, swallowing IO failures (they are not fatal).</summary>
    public void Save(string? path = null)
    {
        path ??= DefaultPath;

        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"settings: could not save {path}: {ex.Message}");
        }
    }
}
