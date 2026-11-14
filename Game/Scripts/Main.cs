using System;
using System.IO;
using Godot;
using H3;
using Sim.Biomes;
using Sim.Buildings;
using Sim.Movement;
using Sim.World;

/// <summary>
/// Phase 1 entry point: loads the Moon bake, shows the region overview and lets
/// the player zoom into a single region's fine hexes.
/// </summary>
public partial class Main : Node2D
{
    private const int LegendRowHeight = 20;
    private const float PanelLeft = 18f;
    private const float PanelTop = 18f;
    private const float PanelWidth = 300f;

    private const string GlobeStatus = "Drag to rotate · wheel to zoom · right-drag to pan · click a region";
    private const string RegionStatus =
        "Wheel zoom · right-drag pan · Tab overlay · 1-4 mode · click hex · Esc back";

    private static readonly Color PanelColor = new(0.04f, 0.05f, 0.07f, 0.86f);
    private static readonly Color MutedText = new(0.68f, 0.72f, 0.78f);
    private static readonly Color AccentText = new(0.96f, 0.97f, 0.99f);

    private enum Mode
    {
        Globe,
        Region,
    }

    private BodyWorld _world = null!;
    private BuildingRegistry _registry = null!;
    private MoonGlobe _globe = null!;
    private RegionView _regionView = null!;
    private Font _font = null!;

    private Mode _mode = Mode.Globe;
    private string _status = GlobeStatus;

    private ulong _hoverRegion;
    private string _hoverBiome = string.Empty;
    private int _hoverHexes;

    private int _selectedHex = -1;
    private string _selectedBiome = string.Empty;
    private int _selectedElevation;
    private CraterKind _selectedCrater;

    private int _routeLegs;
    private double _routeKm;
    private double _routeHours;

    private bool _reportFps;
    private bool _benchRedraw;
    private int _frameCounter;
    private double _frameSeconds;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        _world = OpenMoon();

        // World view sits below the HUD so the panel is never covered.
        var worldLayer = new CanvasLayer { Layer = -1 };
        AddChild(worldLayer);

        _globe = new MoonGlobe();
        worldLayer.AddChild(_globe);
        _globe.Initialize(_world);
        _globe.RegionHovered += OnRegionHovered;
        _globe.RegionSelected += OnRegionSelected;

        _regionView = new RegionView { Visible = false };
        worldLayer.AddChild(_regionView);
        _regionView.HexHovered += OnHexHovered;
        _regionView.HexSelected += OnHexSelected;
        _regionView.RouteComputed += OnRouteComputed;
        _regionView.RouteCleared += OnRouteCleared;
        _regionView.ViewModeChanged += OnViewModeChanged;
        _regionView.ActiveBuildingChanged += OnActiveBuildingChanged;
        _regionView.BuildingsChanged += OnBuildingsChanged;
        _regionView.PlacementFeedback += OnPlacementFeedback;
        _regionView.BaseSpeedKmh = FloatArgument("--speed", 10f);

        _registry = new BuildingRegistry(MoonBuildings.All);
        _regionView.SetBuildingRegistry(_registry);

        GD.Print($"Moon loaded: {_world.Regions.Count} regions, {_world.Biomes.Count} biomes.");

        _globe.Spin = HasArgument("--spin");
        _reportFps = HasArgument("--fps");
        _benchRedraw = HasArgument("--redraw");

        var yaw = FloatArgument("--yaw", -90f);
        var pitch = FloatArgument("--pitch", -14.3f);
        var zoom = FloatArgument("--zoom", 1f);
        _globe.SetView(yaw, pitch, zoom);

        var region = UlongArgument("--region");
        if (region != 0)
            ShowRegion(region);

        var route = ArgumentValue("--route");
        if (route is not null)
        {
            var parts = route.Split(',');
            if (parts.Length == 2
                && int.TryParse(parts[0], out var from)
                && int.TryParse(parts[1], out var to))
            {
                _regionView.ShowRoute(from, to);
            }
        }

        if (_mode == Mode.Region)
        {
            var overlay = ArgumentValue("--overlay");
            if (overlay is not null)
            {
                _regionView.SetViewMode(overlay.ToLowerInvariant() switch
                {
                    "terrain" => RegionViewMode.Terrain,
                    "score" or "sitescore" => RegionViewMode.SiteScore,
                    "buildings" or "building" => RegionViewMode.Buildings,
                    _ => RegionViewMode.Biome,
                });
            }

            var active = ArgumentValue("--active");
            if (active is not null && !_regionView.SetActiveBuilding(active))
                GD.PushWarning($"Unknown building id: {active}");

            var place = ArgumentValue("--place");
            if (place is not null)
            {
                foreach (var part in place.Split(','))
                {
                    if (int.TryParse(part, out var ordinal))
                        _regionView.PlaceActiveBuildingAt(ordinal);
                }
            }
        }

        var shotPath = FindShotPath();
        if (shotPath is not null)
            CaptureAndQuit(shotPath);
    }

    public override void _Process(double delta)
    {
        if (_benchRedraw && _mode == Mode.Region)
            _regionView.QueueRedraw();

        if (!_reportFps)
            return;

        _frameSeconds += delta;
        if (++_frameCounter < 60)
            return;

        var draw = _mode == Mode.Region
            ? $" · draw avg {_regionView.AverageDrawMs:F2} ms · last {_regionView.LastDrawMs:F2} ms"
            : string.Empty;

        GD.Print($"fps: {Engine.GetFramesPerSecond():F1} · frame {_frameSeconds / _frameCounter * 1000.0:F2} ms{draw}");
        _frameCounter = 0;
        _frameSeconds = 0;
        _regionView.ResetDrawStats();
    }

    public override void _Draw()
    {
        DrawPanel();
        DrawStatus();
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

            case Key.Key1 when _mode == Mode.Region:
                _regionView.SetViewMode(RegionViewMode.Biome);
                break;

            case Key.Key2 when _mode == Mode.Region:
                _regionView.SetViewMode(RegionViewMode.Terrain);
                break;

            case Key.Key3 when _mode == Mode.Region:
                _regionView.SetViewMode(RegionViewMode.SiteScore);
                break;

            case Key.Key4 when _mode == Mode.Region:
                _regionView.SetViewMode(RegionViewMode.Buildings);
                break;

            case Key.Tab when _mode == Mode.Region:
                _regionView.CycleViewMode();
                break;

            case Key.Comma when _mode == Mode.Region:
                _regionView.CycleActiveBuilding(-1);
                break;

            case Key.Period when _mode == Mode.Region:
                _regionView.CycleActiveBuilding(1);
                break;

            case Key.C when _mode == Mode.Region && !key.CtrlPressed:
                _regionView.ClearBuildings();
                break;

            case Key.Escape when _mode == Mode.Region:
                ShowGlobe();
                break;

            case Key.Escape:
                GetTree().Quit();
                break;
        }
    }

    private void ShowRegion(ulong regionIndex)
    {
        if (!_world.TryGetRegionEntry(regionIndex, out _))
        {
            GD.PushWarning($"Region {regionIndex} is not in the Moon bake.");
            return;
        }

        _regionView.Load(_world, regionIndex);
        _regionView.Visible = true;
        _globe.Visible = false;
        _mode = Mode.Region;
        _status = RegionStatus;
        _selectedHex = -1;
        _selectedBiome = string.Empty;
        QueueRedraw();
    }

    private void ShowGlobe()
    {
        _regionView.Visible = false;
        _globe.Visible = true;
        _mode = Mode.Globe;
        _status = GlobeStatus;
        QueueRedraw();
    }

    private void OnRegionHovered(ulong regionIndex, string biomeId, int hexCount)
    {
        _hoverRegion = regionIndex;
        _hoverBiome = biomeId;
        _hoverHexes = hexCount;

        _status = regionIndex == 0 || string.IsNullOrEmpty(biomeId)
            ? GlobeStatus
            : $"{DisplayName(biomeId)} · {hexCount} hexes · click to zoom in";

        QueueRedraw();
    }

    private void OnRegionSelected(ulong regionIndex, string biomeId, int hexCount)
    {
        ShowRegion(regionIndex);
    }

    private void OnHexHovered(int ordinal, string biomeId, int elevationM, int craterKind)
    {
        if (ordinal < 0)
        {
            _status = RegionStatus;
            QueueRedraw();
            return;
        }

        _status = $"hex {ordinal} · {DisplayName(biomeId)} · {elevationM:N0} m · {CraterName((CraterKind)craterKind)}";
        QueueRedraw();
    }

    private void OnHexSelected(int ordinal, string biomeId, int elevationM, int craterKind)
    {
        _selectedHex = ordinal;
        _selectedBiome = biomeId;
        _selectedElevation = elevationM;
        _selectedCrater = (CraterKind)craterKind;
        QueueRedraw();
    }

    private void OnRouteComputed(int legs, double distanceKm, double hours)
    {
        _routeLegs = legs;
        _routeKm = distanceKm;
        _routeHours = hours;
        QueueRedraw();
    }

    private void OnRouteCleared()
    {
        _routeLegs = 0;
        _routeKm = 0;
        _routeHours = 0;
        QueueRedraw();
    }

    private void OnViewModeChanged(int mode)
    {
        _status = ((RegionViewMode)mode) switch
        {
            RegionViewMode.Terrain => "terrain overlay · Tab to cycle · 1-4 mode",
            RegionViewMode.SiteScore => "site-score heatmap (7-hex footprint) · Tab to cycle",
            RegionViewMode.Buildings => "buildings overlay · click place · Del remove · C clear",
            _ => RegionStatus,
        };

        QueueRedraw();
    }

    private void OnActiveBuildingChanged(string defId)
    {
        _status = _registry.TryGet(defId, out var def) && def is not null
            ? $"active: {def.DisplayName} · click to place · , / . cycle"
            : $"active: {defId}";

        QueueRedraw();
    }

    private void OnBuildingsChanged(int count) => QueueRedraw();

    private void OnPlacementFeedback(bool success, string message)
    {
        _status = message;
        GD.Print($"placement: {(success ? "ok" : "failed")} · {message}");
        QueueRedraw();
    }

    private void DrawPanel()
    {
        if (_mode == Mode.Globe)
            DrawGlobePanel();
        else
            DrawRegionPanel();
    }

    private void DrawGlobePanel()
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
    }

    private void DrawRegionPanel()
    {
        var mode = _regionView.ViewMode;
        var biomes = _regionView.BiomesPresent;
        var legendRows = mode switch
        {
            RegionViewMode.Biome => Math.Max(1, biomes.Count),
            RegionViewMode.Buildings => 3,
            _ => 1,
        };

        var height = 202f + legendRows * LegendRowHeight + 18f;
        var panel = new Rect2(PanelLeft, PanelTop, PanelWidth, height);

        DrawRect(panel, PanelColor);
        DrawRect(new Rect2(panel.Position, new Vector2(3, panel.Size.Y)), new Color(0.55f, 0.68f, 0.85f, 0.9f));

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 30), "MOON · REGION",
            HorizontalAlignment.Left, -1, 17, AccentText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 48),
            $"{DisplayName(_regionView.RegionBiomeId)} · {_regionView.HexCount} hexes · res {_world.FineResolution}",
            HorizontalAlignment.Left, -1, 12, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 70),
            $"H3 {new H3Index(_regionView.RegionIndex)}", HorizontalAlignment.Left, -1, 11, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 92),
            $"overlay: {ModeName(mode)} · Tab / 1-4", HorizontalAlignment.Left, -1, 12,
            new Color(0.55f, 0.85f, 1f));

        if (_selectedHex >= 0)
        {
            var buildable = _world.Biomes.TryGetById(_selectedBiome, out var selectedBiome) && selectedBiome.Buildable;
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 116),
                $"hex {_selectedHex} · {DisplayName(_selectedBiome)} · {(buildable ? "buildable" : "no build")}",
                HorizontalAlignment.Left, -1, 13, AccentText);

            var score = _regionView.SiteScoreAt(_selectedHex);
            var scoreText = score is null ? "score —" : $"score {score.Value:P0}";
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 134),
                $"{_selectedElevation:N0} m · {ShortCrater(_selectedCrater)} · {scoreText}",
                HorizontalAlignment.Left, -1, 12, MutedText);
        }
        else
        {
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 116),
                "click a hex to inspect it", HorizontalAlignment.Left, -1, 12, MutedText);
        }

        if (_routeLegs > 0)
        {
            var kmh = _routeKm / _routeHours;
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 158),
                $"route {_routeLegs} legs · {_routeKm:N1} km · {_routeHours:N2} h · {kmh:N1} km/h",
                HorizontalAlignment.Left, -1, 12, new Color(0.45f, 0.85f, 1f));
        }
        else
        {
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 158),
                mode == RegionViewMode.Buildings ? "click to place · Del remove · C clear" : "click two hexes for a route preview",
                HorizontalAlignment.Left, -1, 12, MutedText);
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 182), LegendTitle(mode),
            HorizontalAlignment.Left, -1, 11, MutedText);

        var y = PanelTop + 202f;
        switch (mode)
        {
            case RegionViewMode.Biome:
                if (biomes.Count == 0)
                {
                    DrawString(_font, new Vector2(PanelLeft + 18, y + 11), "none",
                        HorizontalAlignment.Left, -1, 12, MutedText);
                    break;
                }

                foreach (var biomeId in biomes)
                {
                    var color = BiomePalette.For(biomeId);
                    DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), color);
                    DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), new Color(0, 0, 0, 0.35f), filled: false, width: 1f);
                    DrawString(_font, new Vector2(PanelLeft + 38, y + 11), DisplayName(biomeId),
                        HorizontalAlignment.Left, -1, 13, AccentText);
                    y += LegendRowHeight;
                }

                break;

            case RegionViewMode.Terrain:
                DrawRamp(TerrainPalette.For, y);
                break;

            case RegionViewMode.SiteScore:
                DrawRamp(t => SiteScorePalette.For(t, true), y);
                break;

            case RegionViewMode.Buildings:
                DrawBuildingLegend(y);
                break;
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + height - 10),
            "Esc back to globe · F12 screenshot", HorizontalAlignment.Left, -1, 11, MutedText);
    }

    private string LegendTitle(RegionViewMode mode) => mode switch
    {
        RegionViewMode.Biome => "biomes in this region",
        RegionViewMode.Terrain =>
            $"elevation · {_regionView.TerrainMinM:N0} – {_regionView.TerrainMaxM:N0} m",
        RegionViewMode.SiteScore =>
            $"site score · {_regionView.SiteScoreMin:P0} – {_regionView.SiteScoreMax:P0} · {_regionView.SiteScoreMs:F1} ms",
        _ => $"buildings · {_regionView.Buildings.Count} placed",
    };

    private void DrawBuildingLegend(float y)
    {
        var active = _regionView.ActiveBuilding;
        if (active is null)
        {
            DrawString(_font, new Vector2(PanelLeft + 18, y + 11), "no building definitions",
                HorizontalAlignment.Left, -1, 12, MutedText);
            return;
        }

        var color = Color.FromHtml(active.FootprintColor);
        DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), color);
        DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), new Color(0, 0, 0, 0.35f), filled: false, width: 1f);
        DrawString(_font, new Vector2(PanelLeft + 38, y + 11),
            $"{active.DisplayName} · {active.Category}", HorizontalAlignment.Left, -1, 13, AccentText);

        y += LegendRowHeight;
        var tech = string.IsNullOrEmpty(active.RequiredTech) ? string.Empty : $" · tech {active.RequiredTech}";
        DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
            $"{FootprintHexes(active)} hex · min {active.MinSiteScore:P0}{tech}",
            HorizontalAlignment.Left, -1, 11, MutedText);

        y += LegendRowHeight;
        DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
            $", / . cycle ({_regionView.BuildingChoiceCount}) · C clear",
            HorizontalAlignment.Left, -1, 11, MutedText);
    }

    private void DrawRamp(Func<double, Color> palette, float y)
    {
        const int steps = 14;
        const float width = 240f;
        var x = PanelLeft + 18f;
        var stepWidth = width / steps;

        for (var i = 0; i < steps; i++)
        {
            var t = i / (double)(steps - 1);
            DrawRect(new Rect2(x + i * stepWidth, y, stepWidth + 0.5f, 12f), palette(t));
        }

        DrawRect(new Rect2(x, y, width, 12f), new Color(0, 0, 0, 0.35f), filled: false, width: 1f);
    }


    private void DrawStatus()
    {
        var size = GetViewportRect().Size;
        var background = new Rect2(0, size.Y - 52, size.X, 52);
        DrawRect(background, new Color(0.03f, 0.04f, 0.05f, 0.8f));

        DrawString(_font, new Vector2(24, size.Y - 22), _status, HorizontalAlignment.Left, -1, 15, AccentText);

        var right = _mode switch
        {
            Mode.Globe when _hoverRegion != 0 => $"{_hoverBiome} · {_hoverHexes} hexes",
            Mode.Globe => "SPACE PROGRAM MANAGER · PHASE 1",
            _ => $"{ModeName(_regionView.ViewMode)} · {_regionView.Buildings.Count} buildings",
        };

        DrawString(_font, new Vector2(size.X - 340, size.Y - 22), right,
            HorizontalAlignment.Left, -1, 12, MutedText);
    }

    private string DisplayName(string biomeId)
        => _world.Biomes.TryGetById(biomeId, out Biome? biome) ? biome.DisplayName : biomeId;

    private static string CraterName(CraterKind crater) => crater switch
    {
        CraterKind.SingleCell => "crater rim",
        CraterKind.MultiCell => "crater rim (multi-cell)",
        _ => "no crater",
    };

    private static string ShortCrater(CraterKind crater) => crater switch
    {
        CraterKind.SingleCell => "crater rim",
        CraterKind.MultiCell => "multi-crater",
        _ => "no crater",
    };

    private static string ModeName(RegionViewMode mode) => mode switch
    {
        RegionViewMode.Terrain => "TERRAIN",
        RegionViewMode.SiteScore => "SITE SCORE",
        RegionViewMode.Buildings => "BUILDINGS",
        _ => "BIOME",
    };

    private static int FootprintHexes(BuildingDef def)
        => 3 * def.FootprintRadius * (def.FootprintRadius + 1) + 1;

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
        var value = ArgumentValue(name);
        return value is not null
               && float.TryParse(value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static ulong UlongArgument(string name)
        => ulong.TryParse(ArgumentValue(name), out var value) ? value : 0UL;

    private static string? ArgumentValue(string name)
    {
        var prefix = name + "=";
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
                return argument[prefix.Length..];
        }

        return null;
    }

    private static string? FindShotPath() => ArgumentValue("--shot");

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
