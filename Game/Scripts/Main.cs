using System;
using System.Collections.Generic;
using System.IO;
using Godot;
using H3;
using Sim.Biomes;
using Sim.World;

/// <summary>
/// Phase 1 entry point: loads the Moon bake and shows the region overview.
/// </summary>
public partial class Main : Node2D
{
    private const int LegendRowHeight = 20;
    private const float PanelLeft = 18f;
    private const float PanelTop = 18f;
    private const float PanelWidth = 272f;

    private static readonly Color PanelColor = new(0.04f, 0.05f, 0.07f, 0.86f);
    private static readonly Color MutedText = new(0.68f, 0.72f, 0.78f);
    private static readonly Color AccentText = new(0.96f, 0.97f, 0.99f);

    private BodyWorld _world = null!;
    private MoonGlobe _globe = null!;
    private Font _font = null!;

    private string _status = "Drag to rotate · wheel to zoom · right-drag to pan · click a region";
    private string _hoverBiome = string.Empty;
    private ulong _hoverRegion;
    private int _hoverHexes;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        _world = OpenMoon();
        _globe = new MoonGlobe();
        AddChild(_globe);
        _globe.Initialize(_world);
        _globe.RegionHovered += OnRegionHovered;

        GD.Print($"Moon loaded: {_world.Regions.Count} regions, {_world.Biomes.Count} biomes.");

        _globe.Spin = HasArgument("--spin");
        _reportFps = HasArgument("--fps");

        var yaw = FloatArgument("--yaw", -90f);
        var pitch = FloatArgument("--pitch", -14.3f);
        var zoom = FloatArgument("--zoom", 1f);
        _globe.SetView(yaw, pitch, zoom);

        var shotPath = FindShotPath();
        if (shotPath is not null)
            CaptureAndQuit(shotPath);
    }

    private bool _reportFps;
    private int _frameCounter;
    private double _frameSeconds;

    public override void _Process(double delta)
    {
        if (!_reportFps)
            return;

        _frameSeconds += delta;
        if (++_frameCounter < 60)
            return;

        GD.Print($"fps: {Engine.GetFramesPerSecond():F1} · frame {_frameSeconds / _frameCounter * 1000.0:F2} ms");
        _frameCounter = 0;
        _frameSeconds = 0;
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;

        DrawPanel(size);
        DrawStatus(size);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.Keycode)
        {
            case Key.F12:
                SaveScreenshot(DefaultScreenshotPath());
                break;

            case Key.Escape:
                GetTree().Quit();
                break;
        }
    }

    private void OnRegionHovered(ulong regionIndex, string biomeId, int hexCount)
    {
        _hoverRegion = regionIndex;
        _hoverBiome = biomeId;
        _hoverHexes = hexCount;

        if (regionIndex == 0 || string.IsNullOrEmpty(biomeId))
        {
            _status = "Drag to rotate · wheel to zoom · right-drag to pan · click a region";
        }
        else
        {
            _status = $"{DisplayName(biomeId)} · {hexCount} hexes · {new H3Index(regionIndex)}";
        }

        QueueRedraw();
    }

    private void DrawPanel(Vector2 size)
    {
        var biomes = _world.Biomes.All;
        var height = 56f + biomes.Count * LegendRowHeight;
        var panel = new Rect2(PanelLeft, PanelTop, PanelWidth, height);

        DrawRect(panel, PanelColor);
        DrawRect(new Rect2(panel.Position, new Vector2(3, panel.Size.Y)), new Color(0.55f, 0.68f, 0.85f, 0.9f));

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 30), "MOON · REGIONS",
            HorizontalAlignment.Left, -1, 17, AccentText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 48),
            $"{_world.Regions.Count:N0} regions · res {_world.RegionResolution} · {_world.Biomes.Count} biomes",
            HorizontalAlignment.Left, -1, 12, MutedText);

        for (var i = 0; i < biomes.Count; i++)
        {
            var biome = biomes[i];
            var y = PanelTop + 66 + i * LegendRowHeight;
            var color = BiomePalette.For(biome.Id);

            DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), color);
            DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), new Color(0, 0, 0, 0.35f), filled: false, width: 1f);
            DrawString(_font, new Vector2(PanelLeft + 38, y + 11), biome.DisplayName,
                HorizontalAlignment.Left, -1, 13, AccentText);
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + height + 18),
            "F12 screenshot · Esc quit", HorizontalAlignment.Left, -1, 11, MutedText);

        DrawString(_font, new Vector2(size.X - 260, 28),
            "SPACE PROGRAM MANAGER · PHASE 1", HorizontalAlignment.Left, -1, 12, new Color(0.5f, 0.56f, 0.64f));
    }

    private void DrawStatus(Vector2 size)
    {
        var background = new Rect2(0, size.Y - 52, size.X, 52);
        DrawRect(background, new Color(0.03f, 0.04f, 0.05f, 0.8f));

        DrawString(_font, new Vector2(24, size.Y - 22), _status, HorizontalAlignment.Left, -1, 15, AccentText);

        if (_hoverRegion != 0)
        {
            DrawString(_font, new Vector2(size.X - 340, size.Y - 22),
                $"{_hoverBiome} · {_hoverHexes} hexes", HorizontalAlignment.Left, -1, 12, MutedText);
        }
    }

    private string DisplayName(string biomeId)
        => _world.Biomes.TryGetById(biomeId, out Biome? biome) ? biome.DisplayName : biomeId;

    private static BodyWorld OpenMoon()
    {
        try
        {
            return BodyWorld.Open("moon");
        }
        catch (DirectoryNotFoundException)
        {
            // Running from the Godot project folder: data lives one level up.
            var fallback = ProjectSettings.GlobalizePath("res://../data");
            return BodyWorld.Open("moon", fallback);
        }
    }

    private static bool HasArgument(string name)
        => Array.Exists(OS.GetCmdlineUserArgs(), argument => argument == name);

    private static float FloatArgument(string name, float fallback)
    {
        var prefix = name + "=";
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal)
                && float.TryParse(argument[prefix.Length..], System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var value))
            {
                return value;
            }
        }

        return fallback;
    }

    private static string? FindShotPath()
    {
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith("--shot=", StringComparison.Ordinal))
                return argument["--shot=".Length..];
        }

        return null;
    }

    private async void CaptureAndQuit(string path)
    {
        // Let the first frames render before grabbing the viewport.
        for (var i = 0; i < 4; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);

        SaveScreenshot(path);
        GetTree().Quit();
    }

    private void SaveScreenshot(string path)
    {
        var image = GetViewport().GetTexture().GetImage();
        var error = image.SavePng(path);

        if (error != Error.Ok)
        {
            GD.PushError($"Screenshot failed ({error}): {path}");
            return;
        }

        GD.Print($"Screenshot: {path}");
    }

    private static string DefaultScreenshotPath()
        => ProjectSettings.GlobalizePath("user://moon_overview.png");
}
