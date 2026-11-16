using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Godot;

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
    public string? LastSlotId { get; set; }

    /// <summary>Default relief exaggeration for the colony view.</summary>
    public float ReliefScale { get; set; } = 1f;

    /// <summary>Print frame timings to the log.</summary>
    public bool ShowFps { get; set; }

    /// <summary>Default save root under Godot's per-user data directory.</summary>
    public static string DefaultSaveRoot => ProjectSettings.GlobalizePath("user://saves");

    /// <summary>Default settings file.</summary>
    public static string DefaultPath => ProjectSettings.GlobalizePath("user://settings.json");

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
