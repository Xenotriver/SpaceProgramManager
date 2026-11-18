using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Godot;
using H3;
using Sim.Biomes;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Hex;
using Sim.Movement;
using Sim.Time;
using Sim.World;

/// <summary>
/// Phase 1 entry point: loads the Moon bake, shows the region overview and lets
/// the player zoom into a single region's fine hexes.
/// </summary>
public partial class Main : Node2D
{
    private const int LegendRowHeight = 20;
    private const int MaxColonyRows = 12;
    private const float PanelLeft = 18f;
    private const float PanelTop = 18f;
    private const float PanelWidth = 300f;
    private const double AutosaveSeconds = 5.0;

    private const string GlobeStatus =
        "Drag to rotate · wheel zoom · right-drag pan · click a region · / search · M map · Esc pause";
    private const string RegionStatus =
        "Wheel zoom · right-drag pan · Tab overlay · 1-4 mode · click hex · Enter site · G colony · M map · Esc back";
    private const string ManagementStatus =
        "Wheel zoom · right-drag pan · Q/E rotate 60° · 1-4 overlay · Tab cycle · [ ] relief · Enter site · M map · Esc back";
    private const string SiteStatus =
        "Wheel zoom · right-drag pan · click cell to place · , / . building · Del remove · C clear · M map · Esc back";

    private static readonly Color PanelColor = new(0.04f, 0.05f, 0.07f, 0.86f);
    private static readonly Color MutedText = new(0.68f, 0.72f, 0.78f);
    private static readonly Color AccentText = new(0.96f, 0.97f, 0.99f);

    private enum Mode
    {
        Globe,
        Region,
        Management,
        Site,
    }

    private BodyWorld _world = null!;
    private BuildingRegistry _registry = null!;
    private CampaignState _campaign = null!;
    private CampaignService _campaignService = null!;
    private MoonGlobe _globe = null!;
    private RegionView _regionView = null!;
    private ManagementView _managementView = null!;
    private SiteView _siteView = null!;
    private ShellOverlay _shell = null!;
    private CanvasLayer _worldLayer = null!;
    private GameSettings _settings = null!;
    private CampaignSlotStore _catalogue = null!;
    private Font _font = null!;

    private static string? _pendingSlot;
    private static string? _pendingBody;
    private static bool _forceMenu;
    private static bool _skipCliView;
    private static bool _bodySwitch;

    private bool _started;

    private Mode _mode = Mode.Globe;
    private string _status = GlobeStatus;

    private ulong _hoverRegion;
    private string _hoverBiome = string.Empty;
    private int _hoverHexes;

    private int _selectedHex = -1;
    private int _siteParentOrdinal = -1;
    private Mode _returnMode = Mode.Region;
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

    private readonly SimulationClock _clock = new();
    private double _autosaveElapsed;

    private int GameDay => (int)Math.Floor(_campaign.Meta.GameTimeHours / 24.0) + 1;

    private string GameClockText
    {
        get
        {
            var hours = _campaign.Meta.GameTimeHours;
            var hour = (int)Math.Floor(hours % 24.0);
            var minute = (int)Math.Floor((hours - Math.Floor(hours)) * 60.0);
            return $"{hour:00}:{minute:00}";
        }
    }

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        _settings = GameSettings.Load();
        _catalogue = new CampaignSlotStore(_settings.SaveRoot);

        var requestedBody = _pendingBody ?? ArgumentValue("--body") ?? KnownBodies.Moon;
        var bodyKey = KnownBodies.TryNormalize(requestedBody);
        if (bodyKey is null)
        {
            GD.PushWarning($"unknown body '{requestedBody}'; loading {KnownBodies.Moon}");
            bodyKey = KnownBodies.Moon;
        }
        _world = OpenBody(bodyKey);

        // World view sits below the HUD so the panel is never covered.
        _worldLayer = new CanvasLayer { Layer = -1 };
        AddChild(_worldLayer);

        _globe = new MoonGlobe();
        _worldLayer.AddChild(_globe);
        _globe.Initialize(_world);
        _globe.RegionHovered += OnRegionHovered;
        _globe.RegionSelected += OnRegionSelected;

        _regionView = new RegionView { Visible = false };
        _worldLayer.AddChild(_regionView);
        _regionView.HexHovered += OnHexHovered;
        _regionView.HexSelected += OnHexSelected;
        _regionView.RouteComputed += OnRouteComputed;
        _regionView.RouteCleared += OnRouteCleared;
        _regionView.ViewModeChanged += OnViewModeChanged;
        _regionView.BaseSpeedKmh = FloatArgument("--speed", 10f);

        _registry = BuildingCatalogue.RegistryFor(_world.BodyKey);

        _managementView = new ManagementView { Visible = false };
        _worldLayer.AddChild(_managementView);
        _managementView.HexHovered += OnHexHovered;
        _managementView.HexSelected += OnHexSelected;
        _managementView.ViewModeChanged += OnViewModeChanged;
        _managementView.ReliefScale = FloatArgument("--relief", _settings.ReliefScale);

        _siteView = new SiteView { Visible = false };
        _worldLayer.AddChild(_siteView);
        _siteView.HexHovered += OnSiteHexHovered;
        _siteView.HexSelected += OnSiteHexSelected;
        _siteView.ActiveBuildingChanged += OnActiveBuildingChanged;
        _siteView.BuildingsChanged += OnBuildingsChanged;
        _siteView.PlacementFeedback += OnPlacementFeedback;

        _shell = new ShellOverlay
        {
            SaveRootLabel = _settings.SaveRoot,
            BodyLabel = KnownBodies.DisplayName(_world.BodyKey),
            BodyKey = _world.BodyKey,
        };
        AddChild(_shell);
        _shell.Command += OnShellCommand;

        GD.Print($"{KnownBodies.DisplayName(_world.BodyKey)} loaded: {_world.Regions.Count} regions, {_world.Biomes.Count} biomes.");

        _globe.Spin = HasArgument("--spin");
        _reportFps = HasArgument("--fps") || _settings.ShowFps;
        _benchRedraw = HasArgument("--redraw");

        var reloadSlot = _pendingSlot;
        var forceMenu = _forceMenu;
        var skipCli = _skipCliView;
        var bodySwitch = _bodySwitch;
        _pendingSlot = null;
        _pendingBody = null;
        _forceMenu = false;
        _skipCliView = false;
        _bodySwitch = false;

        var autoStart = !forceMenu
                        && (reloadSlot is not null
                            || HasArgument("--continue")
                            || OS.GetCmdlineUserArgs().Length > 0)
                        && !HasArgument("--menu");

        if (!autoStart)
        {
            ShowMainMenu();

            var menuShot = FindShotPath();
            if (menuShot is not null)
                CaptureAndQuit(menuShot);

            return;
        }

        StartCampaign(
            reloadSlot ?? ResolveStartupSlot(),
            fresh: HasArgument("--fresh"),
            ignoreCliSave: bodySwitch,
            skipCliView: skipCli);

        if (!skipCli)
            ApplyCommandLine();

        if (HasArgument("--map"))
            _shell.ToggleSystemMap();

        var shotPath = FindShotPath();
        if (shotPath is not null)
            CaptureAndQuit(shotPath);
    }

    private string ResolveStartupSlot()
    {
        var explicitSlot = ArgumentValue("--slot");
        if (!string.IsNullOrWhiteSpace(explicitSlot))
            return explicitSlot;

        return _settings.LastSlotFor(_world.BodyKey) ?? CampaignSlotStore.DefaultSlotId;
    }

    private void StartCampaign(
        string slotId,
        bool fresh,
        bool ignoreCliSave = false,
        bool skipCliView = false)
    {
        var fingerprint = BakeFingerprint.From(_world.Manifest);
        string slotDirectory;
        string slotName;

        // A UI body switch must never inherit --save from the original launch:
        // that directory belongs to the previous body's fingerprint.
        var saveOverride = ignoreCliSave ? null : ArgumentValue("--save");
        if (!string.IsNullOrWhiteSpace(saveOverride))
        {
            slotDirectory = Path.GetFullPath(saveOverride);
            slotId = new DirectoryInfo(slotDirectory).Name;
            slotName = _catalogue.TryGet(slotId, fingerprint)?.Name
                       ?? CampaignSlotStore.DefaultName(slotId);
        }
        else
        {
            slotDirectory = Path.Combine(_settings.SaveRoot, slotId);
            slotName = _catalogue.TryGet(slotId, fingerprint)?.Name
                       ?? CampaignSlotStore.DefaultName(slotId);
        }

        _campaign = new CampaignState(_world, _registry);
        _campaignService = new CampaignService(
            _world,
            _campaign,
            slotDirectory,
            slotId,
            slotName,
            enabled: !HasArgument("--nosave"),
            fresh: fresh);
        _campaignService.Notified += (message, error) => _shell.PushToast(message, error);
        _campaignService.StatusChanged += QueueRedraw;

        _regionView.SetCampaign(_campaign);
        _managementView.SetCampaign(_campaign);

        _campaignService.Load();
        _started = true;
        _worldLayer.Visible = true;
        _shell.Close();

        if (string.IsNullOrWhiteSpace(saveOverride))
        {
            _settings.SetLastSlot(_world.BodyKey, _campaignService.SlotId);
        }

        var view = _campaignService.ViewState;
        _globe.SetView(view.GlobeYawDegrees, view.GlobePitchDegrees, view.GlobeZoom);

        var region = skipCliView ? 0UL : UlongArgument("--region");
        if (region == 0 && _campaign.Meta.ActiveRegionIndex is { } restored)
            region = restored;

        if (region != 0)
            ShowRegion(region);
        else
            ShowGlobe();

        if (string.Equals(view.Mode, "management", StringComparison.OrdinalIgnoreCase)
            && _mode == Mode.Region)
        {
            ShowManagement();
        }
        else if (string.Equals(view.Mode, "region", StringComparison.OrdinalIgnoreCase)
                 && _mode == Mode.Globe
                 && _campaign.Meta.ActiveRegionIndex is { } activeRegion)
        {
            ShowRegion(activeRegion);
        }
        else if (string.Equals(view.Mode, "site", StringComparison.OrdinalIgnoreCase)
                 && view.SelectedHexOrdinal is { } restoredHex)
        {
            ShowSite(restoredHex);
        }

        _managementView.SetRotation(view.ColonyRotationSteps);
        SetOverlay(ParseOverlay(view.Overlay));

        _clock.Reset(_campaign.Meta.GameTimeHours);
        _autosaveElapsed = 0.0;
        // A fresh session runs at 1×; automation can pin the clock with --time=0.
        ApplyTimeSpeed(DoubleArgument("--time", 1.0) switch
        {
            <= 0.0 => 0,
            < 2.0 => 1,
            < 4.0 => 2,
            _ => 4,
        });

        RefreshMarkers();
        QueueRedraw();
    }

    private void ShowMainMenu()
    {
        _started = false;
        _mode = Mode.Globe;
        _status = GlobeStatus;
        _worldLayer.Visible = false;
        _globe.Visible = true;
        _regionView.Visible = false;
        _managementView.Visible = false;
        _siteView.Visible = false;

        var fingerprint = BakeFingerprint.From(_world.Manifest);
        _shell.OpenMenu(_catalogue.List(fingerprint), _settings.LastSlotFor(_world.BodyKey));
        QueueRedraw();
    }

    private void OnShellCommand(ShellCommand command, string? payload)
    {
        switch (command)
        {
            case ShellCommand.Continue:
            case ShellCommand.PlaySlot:
                if (string.IsNullOrWhiteSpace(payload))
                {
                    _shell.PushToast("no campaign to open");
                    return;
                }

                _settings.SetLastSlot(_world.BodyKey, payload);
                ReloadInto(payload);
                return;

            case ShellCommand.NewCampaign:
                var fingerprint = BakeFingerprint.From(_world.Manifest);
                var id = _catalogue.Create(NextCampaignName(), new CampaignMeta(), fingerprint);
                _settings.SetLastSlot(_world.BodyKey, id);
                ReloadInto(id);
                return;

            case ShellCommand.DeleteSlot:
                if (string.IsNullOrWhiteSpace(payload))
                    return;

                try
                {
                    _catalogue.Delete(payload);
                    _shell.PushToast("campaign deleted");
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
                {
                    _shell.PushToast($"delete failed: {ex.Message}", error: true);
                }

                if (string.Equals(_settings.LastSlotFor(_world.BodyKey), payload, StringComparison.Ordinal))
                    _settings.SetLastSlot(_world.BodyKey, null);

                _shell.SetSlots(
                    _catalogue.List(BakeFingerprint.From(_world.Manifest)),
                    _settings.LastSlotFor(_world.BodyKey));
                _shell.ShowSlots();
                return;

            case ShellCommand.SaveNow:
                SaveNowAndToast();
                return;

            case ShellCommand.PauseTime:
                ApplyTimeSpeed(0);
                return;

            case ShellCommand.Speed1:
            case ShellCommand.Speed2:
            case ShellCommand.Speed4:
                ApplyTimeSpeed(command switch
                {
                    ShellCommand.Speed2 => 2,
                    ShellCommand.Speed4 => 4,
                    _ => 1,
                });
                _shell.Close();
                return;

            case ShellCommand.Search:
                GoToRegion(payload);
                return;

            case ShellCommand.SwitchBody:
                SwitchBody(payload ?? KnownBodies.Moon);
                return;

            case ShellCommand.OpenSaveFolder:
                OS.ShellOpen(_settings.SaveRoot);
                return;

            case ShellCommand.MainMenu:
                if (_campaignService is not null)
                {
                    _campaignService.SetViewState(CaptureView());
                    _campaignService.SaveSimulation();
                }

                _forceMenu = true;
                _skipCliView = true;
                GetTree().ReloadCurrentScene();
                return;

            case ShellCommand.Quit:
                GetTree().Quit();
                return;
        }
    }

    private void ReloadInto(string slotId)
    {
        _pendingSlot = slotId;
        _skipCliView = true;
        GetTree().ReloadCurrentScene();
    }

    /// <summary>
    /// Saves the current campaign and reloads into a compatible slot for another
    /// body. A missing or incompatible slot is replaced by a fresh one; existing
    /// saves are never opened against the wrong fingerprint.
    /// </summary>
    private void SwitchBody(string bodyKey)
    {
        var target = KnownBodies.TryNormalize(bodyKey);
        if (target is null)
        {
            _shell.PushToast($"unknown body '{bodyKey}'", error: true);
            return;
        }

        if (string.Equals(target, _world.BodyKey, StringComparison.OrdinalIgnoreCase))
        {
            _shell.Close();
            return;
        }

        if (_campaignService is not null)
        {
            _campaignService.SetViewState(CaptureView());
            _campaignService.SaveSimulation();
        }

        try
        {
            using var targetWorld = OpenBody(target);
            var fingerprint = BakeFingerprint.From(targetWorld.Manifest);
            var slotId = _settings.LastSlotFor(target);
            var slot = slotId is null ? null : _catalogue.TryGet(slotId, fingerprint);

            if (slot is null || slot.State != SlotState.Ok)
            {
                slotId = _catalogue.Create(
                    $"{KnownBodies.DisplayName(target)} Campaign {NextCampaignNumber(fingerprint)}",
                    new CampaignMeta(),
                    fingerprint);
            }

            _settings.SetLastSlot(target, slotId);
            _pendingBody = target;
            _pendingSlot = slotId;
            _bodySwitch = true;
            _skipCliView = true;
            _forceMenu = false;
            GetTree().ReloadCurrentScene();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                                      or InvalidDataException or ArgumentException)
        {
            _shell.PushToast($"could not switch to {KnownBodies.DisplayName(target)}: {ex.Message}", error: true);
        }
    }

    private string NextCampaignName()
        => $"Campaign {NextCampaignNumber(BakeFingerprint.From(_world.Manifest))}";

    private int NextCampaignNumber(BakeFingerprint fingerprint)
        => _catalogue.List(fingerprint).Count + 1;

    private void ApplyCommandLine()
    {
        if (HasArgument("--yaw") || HasArgument("--pitch") || HasArgument("--zoom"))
        {
            _globe.SetView(
                FloatArgument("--yaw", _globe.YawDegrees),
                FloatArgument("--pitch", _globe.PitchDegrees),
                FloatArgument("--zoom", _globe.Zoom));
        }

        var view = ArgumentValue("--view");
        if (string.Equals(view, "site", StringComparison.OrdinalIgnoreCase))
        {
            var hex = IntArgument("--hex", 0);
            ShowSite(hex);
        }
        else if (string.Equals(view, "region", StringComparison.OrdinalIgnoreCase))
        {
            if (_regionView.IsLoaded)
                ShowRegion(_regionView.RegionIndex);
        }
        else if (HasArgument("--iso")
                 || string.Equals(view, "management", StringComparison.OrdinalIgnoreCase))
        {
            // The CLI view overrides the slot's restored mode, including site.
            if (_regionView.IsLoaded)
                ShowManagement();
        }

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

        var overlay = ArgumentValue("--overlay");
        if (overlay is not null)
            SetOverlay(ParseOverlay(overlay));

        var rot = ArgumentValue("--rot");
        if (rot is not null
            && _mode == Mode.Management
            && int.TryParse(rot, out var rotDegrees))
        {
            _managementView.SetRotation((int)Math.Round(rotDegrees / 60.0));
        }

        var active = ArgumentValue("--active");
        if (active is not null)
        {
            if (_mode != Mode.Site)
                GD.PushWarning("--active requires --view=site (region and colony views are inspectors)");
            else if (!_siteView.SetActiveBuilding(active))
                GD.PushWarning($"Unknown building id: {active}");
        }

        var places = ArgumentValues("--place").ToArray();
        if (_mode == Mode.Site)
        {
            // Site view uses axial coordinates: --place=q,r. Repeating the flag
            // places several installations in the same site in order.
            foreach (var place in places)
            {
                var parts = place.Split(',');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var q)
                    && int.TryParse(parts[1], out var r))
                {
                    _siteView.PlaceAt(q, r);
                }
                else
                {
                    GD.PushWarning($"Invalid site placement '--place={place}' (expected q,r)");
                }
            }
        }
        else if (places.Length > 0)
        {
            GD.PushWarning("--place requires --view=site (use --place=q,r)");
        }

        // Automation hook: advance the loaded regions immediately, e.g.
        // --advance=24 to prove a 24 h construction finishes before a screenshot.
        var advance = DoubleArgument("--advance", 0.0);
        if (advance > 0.0)
        {
            var report = _campaign.Tick(advance);
            _clock.Reset(_campaign.Meta.GameTimeHours);
            _campaignService.SaveSimulation();
            _regionView.QueueRedraw();
            _managementView.QueueRedraw();
            _siteView.QueueRedraw();
            GD.Print($"time: advanced {report.Hours:F2} h · {report.Steps} step(s) · " +
                     $"{report.Completed} completed · {report.Idled} idled");
            QueueRedraw();
        }
    }

    private void ApplyTimeSpeed(int speed)
    {
        _clock.SetSpeed(speed);
        AfterTimeChanged();
    }

    private void AfterTimeChanged()
    {
        _shell.SetTimeSpeed(_clock.Speed);
        _autosaveElapsed = 0.0;

        // Pausing is a natural save point; it captures progress and treasury
        // without waiting for the periodic autosave.
        if (_started && _clock.IsPaused)
            _campaignService.SaveSimulation();

        GD.Print($"time: {_clock.Label} · day {GameDay:0} {GameClockText}" +
                 $" · {_campaign.Meta.Credits:N0} cr");
        QueueRedraw();
    }

    private void SaveNowAndToast()
    {
        if (_campaignService is null)
            return;

        _campaignService.SetViewState(CaptureView());
        var saved = _campaignService.SaveSimulation() && _campaignService.SaveNow();
        _shell.PushToast(saved ? "campaign saved" : "save failed", error: !saved);
    }

    private SlotViewState CaptureView() => new()
    {
        Mode = _mode switch
        {
            Mode.Management => "management",
            Mode.Region => "region",
            Mode.Site => "site",
            _ => "globe",
        },
        SelectedHexOrdinal = _mode == Mode.Site ? _siteParentOrdinal : null,
        GlobeYawDegrees = _globe.YawDegrees,
        GlobePitchDegrees = _globe.PitchDegrees,
        GlobeZoom = _globe.Zoom,
        ColonyRotationSteps = _managementView.RotationSteps,
        Overlay = _mode == Mode.Management
            ? OverlayName(_managementView.Overlay)
            : OverlayName(_regionView.ViewMode),
    };

    private void RefreshMarkers()
    {
        if (_campaign is null)
            return;

        _globe.SetMarkers(
            _campaign.Meta.VisitedRegions,
            _campaignService.BuiltRegions,
            _campaign.Meta.ActiveRegionIndex);
    }

    private static string OverlayName(RegionViewMode mode) => mode switch
    {
        RegionViewMode.Terrain => "terrain",
        RegionViewMode.SiteScore => "siteScore",
        RegionViewMode.Buildings => "buildings",
        _ => "biome",
    };

    private void GoToRegion(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!ulong.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var value))
        {
            _shell.PushToast("not a decimal H3 index", error: true);
            return;
        }

        var cell = new H3Index(value);
        if (!cell.IsValidCell)
        {
            _shell.PushToast("not a valid H3 cell", error: true);
            return;
        }

        if (cell.Resolution == _world.FineResolution)
            cell = HexGrid.Region(cell, _world.RegionResolution);

        if (cell.Resolution != _world.RegionResolution
            || !_world.TryGetRegionEntry((ulong)cell, out _))
        {
            _shell.PushToast("index is not a region in this bake", error: true);
            return;
        }

        ShowGlobe();
        _globe.FocusRegion((ulong)cell);
        _shell.PushToast($"region {(ulong)cell}");
    }

    public override void _Process(double delta)
    {
        if (!_started)
            return;

        if (!_shell.IsOpen && !_clock.IsPaused)
        {
            var steps = _clock.Advance(delta);
            if (steps > 0)
            {
                _campaign.Tick(steps * SimulationClock.FixedStepHours);

                if (_mode == Mode.Region)
                    _regionView.QueueRedraw();
                else if (_mode == Mode.Management)
                    _managementView.QueueRedraw();
                else if (_mode == Mode.Site)
                    _siteView.QueueRedraw();

                QueueRedraw();
            }

            _autosaveElapsed += delta;
            if (_autosaveElapsed >= AutosaveSeconds)
            {
                _autosaveElapsed = 0.0;
                _campaignService.SaveSimulation();
            }
        }

        if (_benchRedraw && _mode == Mode.Region)
            _regionView.QueueRedraw();
        else if (_benchRedraw && _mode == Mode.Management)
            _managementView.QueueRedraw();
        else if (_benchRedraw && _mode == Mode.Site)
            _siteView.QueueRedraw();

        if (!_reportFps)
            return;

        _frameSeconds += delta;
        if (++_frameCounter < 60)
            return;

        var draw = _mode switch
        {
            Mode.Region => $" · draw avg {_regionView.AverageDrawMs:F2} ms · last {_regionView.LastDrawMs:F2} ms",
            Mode.Management =>
                $" · draw avg {_managementView.AverageDrawMs:F2} ms · last {_managementView.LastDrawMs:F2} ms",
            Mode.Site =>
                $" · draw avg {_siteView.AverageDrawMs:F2} ms · last {_siteView.LastDrawMs:F2} ms",
            _ => string.Empty,
        };

        GD.Print($"fps: {Engine.GetFramesPerSecond():F1} · frame {_frameSeconds / _frameCounter * 1000.0:F2} ms{draw}");
        _frameCounter = 0;
        _frameSeconds = 0;
        _regionView.ResetDrawStats();
        _managementView.ResetDrawStats();
        _siteView.ResetDrawStats();
    }

    public override void _Draw()
    {
        if (!_started)
            return;

        DrawPanel();
        DrawStatus();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!_started)
            return;

        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;

        switch (key.Keycode)
        {
            case Key.F12:
                SaveScreenshot(DefaultScreenshotPath());
                break;

            case Key.F5:
                SaveNowAndToast();
                break;

            case Key.Space when !_shell.IsOpen:
                _clock.TogglePause();
                AfterTimeChanged();
                break;

            case Key.Slash when _mode == Mode.Globe:
                _shell.OpenSearch();
                break;

            case Key.Home when _mode == Mode.Globe && _campaign?.Meta.ActiveRegionIndex is { } home:
                _globe.FocusRegion(home);
                RefreshMarkers();
                break;

            case Key.Enter when _mode is Mode.Region or Mode.Management:
            case Key.KpEnter when _mode is Mode.Region or Mode.Management:
                ShowSite(_selectedHex >= 0 ? _selectedHex : 0);
                break;

            case Key.Key1 when _mode is Mode.Region or Mode.Management:
                SetOverlay(RegionViewMode.Biome);
                break;

            case Key.Key2 when _mode is Mode.Region or Mode.Management:
                SetOverlay(RegionViewMode.Terrain);
                break;

            case Key.Key3 when _mode is Mode.Region or Mode.Management:
                SetOverlay(RegionViewMode.SiteScore);
                break;

            case Key.Key4 when _mode is Mode.Region or Mode.Management:
                SetOverlay(RegionViewMode.Buildings);
                break;

            case Key.Tab when _mode == Mode.Region:
                _regionView.CycleViewMode();
                break;

            case Key.Tab when _mode == Mode.Management:
                _managementView.CycleOverlay();
                break;

            case Key.Q when _mode == Mode.Management:
                RotateCamera(-1);
                break;

            case Key.E when _mode == Mode.Management:
                RotateCamera(1);
                break;

            case Key.M:
                _shell.ToggleSystemMap();
                break;

            case Key.G when _mode == Mode.Region:
                ShowManagement();
                break;

            case Key.Comma when _mode == Mode.Site:
                _siteView.CycleActiveBuilding(-1);
                break;

            case Key.Period when _mode == Mode.Site:
                _siteView.CycleActiveBuilding(1);
                break;

            case Key.C when _mode == Mode.Site && !key.CtrlPressed:
                _siteView.ClearInstallations();
                break;

            case Key.Delete when _mode == Mode.Site:
            case Key.Backspace when _mode == Mode.Site:
                _siteView.RemoveSelected();
                break;

            case Key.Escape when _mode == Mode.Site:
                if (_returnMode == Mode.Management)
                    ShowManagement();
                else
                    ShowRegion(_regionView.RegionIndex);
                break;

            case Key.Escape when _mode == Mode.Management:
                ShowRegion(_regionView.RegionIndex);
                break;

            case Key.Escape when _mode == Mode.Region:
                ShowGlobe();
                break;

            case Key.Escape:
                _shell.OpenPause();
                break;
        }
    }

    private void ShowRegion(ulong regionIndex)
    {
        if (!_world.TryGetRegionEntry(regionIndex, out _))
        {
            GD.PushWarning($"Region {regionIndex} is not in the {KnownBodies.DisplayName(_world.BodyKey)} bake.");
            return;
        }

        // Stored buildings must be installed before the view reads the region.
        _campaignService.EnsureRegionLoaded(regionIndex);

        _regionView.Load(_world, regionIndex);
        _regionView.Visible = true;
        _managementView.Visible = false;
        _siteView.Visible = false;
        _globe.Visible = false;
        _mode = Mode.Region;
        _status = RegionStatus;
        _selectedHex = -1;
        _selectedBiome = string.Empty;
        _campaignService.SetActiveRegion(regionIndex);
        RefreshMarkers();
        QueueRedraw();
    }

    private void ShowManagement()
    {
        if (!_regionView.IsLoaded)
            return;

        _mode = Mode.Management;
        _managementView.Load(_world, _regionView.RegionIndex);
        _managementView.Visible = true;
        _regionView.Visible = false;
        _siteView.Visible = false;
        _globe.Visible = false;
        _status = ManagementStatus;
        _selectedHex = -1;
        _selectedBiome = string.Empty;
        RefreshMarkers();
        GD.Print($"colony view: region {_regionView.RegionIndex} · overlay {_managementView.Overlay}");
        QueueRedraw();
    }

    private void ShowSite(int hexOrdinal)
    {
        if (!_regionView.IsLoaded)
        {
            GD.PushWarning("Open a region before opening a site.");
            return;
        }

        var tile = _regionView.Tile;
        if ((uint)hexOrdinal >= (uint)tile.HexCount)
        {
            GD.PushWarning($"Hex ordinal {hexOrdinal} is outside this region (0..{tile.HexCount - 1}).");
            return;
        }

        _campaignService.EnsureRegionLoaded(tile.Entry.Index);
        _returnMode = _mode == Mode.Management ? Mode.Management : Mode.Region;
        _siteParentOrdinal = hexOrdinal;

        _siteView.Load(_world, tile, hexOrdinal, _campaign, _registry);
        _siteView.Visible = true;
        _regionView.Visible = false;
        _managementView.Visible = false;
        _globe.Visible = false;
        _mode = Mode.Site;
        _status = SiteStatus;
        _selectedHex = -1;
        RefreshMarkers();
        QueueRedraw();
    }

    private void ShowGlobe()
    {
        _regionView.Visible = false;
        _managementView.Visible = false;
        _siteView.Visible = false;
        _globe.Visible = true;
        _mode = Mode.Globe;
        _status = GlobeStatus;
        _campaignService.SetActiveRegion(null);
        RefreshMarkers();
        QueueRedraw();
    }

    public override void _ExitTree()
    {
        if (_campaignService is not null)
        {
            _campaignService.SetViewState(CaptureView());
            _campaignService.SaveSimulation();
            _campaignService.Flush();
        }

        _settings?.Save();
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
            _status = _mode == Mode.Management ? ManagementStatus : RegionStatus;
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

    private void OnSiteHexHovered(int q, int r)
    {
        _status = q == -1 && r == -1
            ? SiteStatus
            : $"site cell ({q},{r}) · click to place · Del remove · , / . cycle";
        QueueRedraw();
    }

    private void OnSiteHexSelected(int q, int r)
    {
        _status = $"site cell ({q},{r})";
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
        var overlay = (RegionViewMode)mode;

        _status = _mode == Mode.Management
            ? overlay switch
            {
                RegionViewMode.Terrain => "colony · terrain relief · Q/E rotate · [ ] exaggeration · Tab cycle",
                RegionViewMode.SiteScore => "colony · site-score relief · Q/E rotate · Tab cycle",
                RegionViewMode.Buildings => "colony · sites overlay · Q/E rotate · Enter opens site",
                _ => ManagementStatus,
            }
            : overlay switch
            {
                RegionViewMode.Terrain => "terrain overlay · Tab to cycle · 1-4 mode",
                RegionViewMode.SiteScore => "site-score heatmap (7-hex footprint) · Tab to cycle",
                RegionViewMode.Buildings => "sites overlay · Enter opens selected site · 1-4 mode",
                _ => RegionStatus,
            };

        QueueRedraw();
    }

    private void OnActiveBuildingChanged(string defId)
    {
        if (_mode != Mode.Site)
            return;

        var def = _siteView.ActiveBuilding;
        _status = def is not null
            ? $"active: {def.DisplayName} · click cell · , / . cycle · Del remove"
            : $"active: {defId}";

        QueueRedraw();
    }

    private void OnBuildingsChanged(int count)
    {
        RefreshMarkers();
        QueueRedraw();
    }

    private void SetOverlay(RegionViewMode mode)
    {
        if (_mode == Mode.Management)
            _managementView.SetOverlay(mode);
        else
            _regionView.SetViewMode(mode);
    }

    private void RotateCamera(int steps)
    {
        _managementView.Rotate(steps);
        _status = $"camera {_managementView.CameraDegrees}° · Q/E rotate · [ ] relief · Esc back";
        QueueRedraw();
    }

    private void OnPlacementFeedback(bool success, string message)
    {
        _status = message;
        GD.Print($"placement: {(success ? "ok" : "failed")} · {message}");
        QueueRedraw();
    }

    private void DrawPanel()
    {
        switch (_mode)
        {
            case Mode.Globe:
                DrawGlobePanel();
                break;

            case Mode.Management:
                DrawManagementPanel();
                break;

            case Mode.Site:
                DrawSitePanel();
                break;

            default:
                DrawRegionPanel();
                break;
        }
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
            "F12 screenshot · F5 save · / search · Esc pause", HorizontalAlignment.Left, -1, 11, MutedText);
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
                mode == RegionViewMode.Buildings
                    ? "Enter opens the selected site"
                    : "click two hexes for a route preview",
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
                DrawSiteLegend(y);
                break;
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + height - 10),
            "Esc back to globe · F12 screenshot", HorizontalAlignment.Left, -1, 11, MutedText);
    }

    private void DrawSitePanel()
    {
        var tile = _siteView.IsLoaded ? _regionView.Tile : null;
        var parentHex = tile is not null && (uint)_siteParentOrdinal < (uint)tile.HexCount
            ? tile.Cells[_siteParentOrdinal]
            : new H3Index(0);
        var biomeId = tile is not null && (uint)_siteParentOrdinal < (uint)tile.HexCount
            ? _world.Biomes[tile[_siteParentOrdinal].BiomeCode].Id
            : string.Empty;
        var installations = _siteView.Installations;
        var active = _siteView.ActiveBuilding;

        var rows = installations.Count == 0 ? 1 : Math.Min(installations.Count, MaxColonyRows);
        var height = 226f + rows * LegendRowHeight + 18f;
        var panel = new Rect2(PanelLeft, PanelTop, PanelWidth, height);

        DrawRect(panel, PanelColor);
        DrawRect(new Rect2(panel.Position, new Vector2(3, panel.Size.Y)), new Color(0.55f, 0.9f, 0.75f, 0.9f));

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 30), "MOON · SITE",
            HorizontalAlignment.Left, -1, 17, AccentText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 48),
            $"{DisplayName(biomeId)} · hex {_siteParentOrdinal}", HorizontalAlignment.Left, -1, 12, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 70),
            $"H3 {parentHex}", HorizontalAlignment.Left, -1, 11, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 94),
            $"{_siteView.InstallationCount} installation(s) · 37 local cells", HorizontalAlignment.Left, -1, 12,
            new Color(0.55f, 0.95f, 0.75f));

        if (active is not null)
        {
            var color = string.IsNullOrWhiteSpace(active.FootprintColor)
                ? new Color(0.85f, 0.85f, 0.92f)
                : Color.FromHtml(active.FootprintColor);
            DrawRect(new Rect2(PanelLeft + 18, PanelTop + 112, 12, 12), color);
            DrawString(_font, new Vector2(PanelLeft + 38, PanelTop + 123), active.DisplayName,
                HorizontalAlignment.Left, -1, 13, AccentText);
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 146),
                $"{FootprintHexes(active)} local cell(s) · , / . cycle ({_siteView.BuildingChoiceCount})",
                HorizontalAlignment.Left, -1, 11, MutedText);
        }
        else
        {
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 123), "no building definitions",
                HorizontalAlignment.Left, -1, 12, MutedText);
        }

        if (!_siteView.GhostValid && _siteView.GhostError.Length > 0)
        {
            var error = _siteView.GhostError.Length > 42 ? _siteView.GhostError[..39] + "..." : _siteView.GhostError;
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 166), error,
                HorizontalAlignment.Left, -1, 11, new Color(1f, 0.62f, 0.55f));
        }
        else
        {
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 166),
                "click cell · Del remove · C clear this site", HorizontalAlignment.Left, -1, 11, MutedText);
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 190), "installations",
            HorizontalAlignment.Left, -1, 11, MutedText);

        var y = PanelTop + 210f;
        if (installations.Count == 0)
        {
            DrawString(_font, new Vector2(PanelLeft + 18, y + 11), "empty site",
                HorizontalAlignment.Left, -1, 12, MutedText);
        }
        else
        {
            var hidden = Math.Max(0, installations.Count - MaxColonyRows);
            if (hidden > 0)
                DrawString(_font, new Vector2(PanelLeft + 18, y + 11), $"+{hidden} more",
                    HorizontalAlignment.Left, -1, 10, MutedText);

            foreach (var building in installations.Take(MaxColonyRows))
            {
                var def = _registry.TryGet(building.DefId, out var found) ? found : null;
                var name = def?.DisplayName ?? building.DefId;
                var color = BuildingStatusColor(building.Status);

                DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), color);
                DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), new Color(0, 0, 0, 0.35f), false, 1f);
                DrawString(_font, new Vector2(PanelLeft + 38, y + 11),
                    $"{name} · {building.LocalCenter}", HorizontalAlignment.Left, -1, 11, AccentText);
                y += LegendRowHeight;
            }
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + height - 10),
            "Enter/Esc navigate · F12 screenshot", HorizontalAlignment.Left, -1, 11, MutedText);
    }

    private void DrawManagementPanel()
    {
        var overlay = _managementView.Overlay;
        var biomes = _managementView.BiomesPresent;
        var colony = overlay == RegionViewMode.Buildings
                     && _campaign is not null
                     && _managementView.Tile is not null
            ? _campaign.Buildings(_managementView.Tile.Entry.Index)
            : null;

        var legendRows = overlay switch
        {
            RegionViewMode.Biome => Math.Max(1, biomes.Count),
            RegionViewMode.Buildings => colony is null || colony.Count == 0
                ? 1
                : Math.Min(colony.Count, MaxColonyRows) + 1,
            _ => 1,
        };

        var height = 202f + legendRows * LegendRowHeight + 18f;
        var panel = new Rect2(PanelLeft, PanelTop, PanelWidth, height);

        DrawRect(panel, PanelColor);
        DrawRect(new Rect2(panel.Position, new Vector2(3, panel.Size.Y)), new Color(0.9f, 0.72f, 0.45f, 0.9f));

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 30), "MOON · COLONY",
            HorizontalAlignment.Left, -1, 17, AccentText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 48),
            $"{DisplayName(_managementView.RegionBiomeId)} · {_managementView.HexCount} hexes · res {_world.FineResolution}",
            HorizontalAlignment.Left, -1, 12, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 70),
            $"H3 {new H3Index(_managementView.RegionIndex)}", HorizontalAlignment.Left, -1, 11, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 92),
            $"overlay: {ModeName(overlay)} · Tab / 1-4", HorizontalAlignment.Left, -1, 12,
            new Color(0.95f, 0.8f, 0.55f));

        if (_selectedHex >= 0)
        {
            var buildable = _world.Biomes.TryGetById(_selectedBiome, out var selectedBiome) && selectedBiome.Buildable;
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 116),
                $"hex {_selectedHex} · {DisplayName(_selectedBiome)} · {(buildable ? "buildable" : "no build")}",
                HorizontalAlignment.Left, -1, 13, AccentText);
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 134),
                $"{_selectedElevation:N0} m · {ShortCrater(_selectedCrater)}",
                HorizontalAlignment.Left, -1, 12, MutedText);
        }
        else
        {
            DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 116),
                "click a hex to inspect it", HorizontalAlignment.Left, -1, 12, MutedText);
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 158),
            $"relief ×{_managementView.ReliefScale:F2} · camera {_managementView.CameraDegrees}° · Q/E / [ ]",
            HorizontalAlignment.Left, -1, 12, MutedText);
        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + 182), ManagementLegendTitle(),
            HorizontalAlignment.Left, -1, 11, MutedText);

        var y = PanelTop + 202f;
        switch (overlay)
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

            default:
                if (_campaign is not null && _managementView.Tile is not null)
                    DrawColonyPanel(y, _managementView.Tile.Entry.Index, PanelTop + height - 26f);
                else
                    DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
                        $"{_managementView.BuildingCount} building(s) in this region",
                        HorizontalAlignment.Left, -1, 12, MutedText);
                break;
        }

        DrawString(_font, new Vector2(PanelLeft + 18, PanelTop + height - 10),
            "Esc back · Q/E rotate · F12 screenshot", HorizontalAlignment.Left, -1, 11, MutedText);
    }

    private string ManagementLegendTitle()
    {
        var overlay = _managementView.Overlay;
        return overlay switch
        {
            RegionViewMode.Biome => "biomes in this region",
            RegionViewMode.Terrain =>
                $"elevation · {_managementView.TerrainMinM:N0} – {_managementView.TerrainMaxM:N0} m",
            RegionViewMode.SiteScore =>
                $"site score · {_managementView.SiteScoreMin:P0} – {_managementView.SiteScoreMax:P0} · {_managementView.SiteScoreMs:F1} ms",
            _ => $"sites · {_managementView.SiteCount} occupied hexes",
        };
    }

    private string LegendTitle(RegionViewMode mode) => mode switch
    {
        RegionViewMode.Biome => "biomes in this region",
        RegionViewMode.Terrain =>
            $"elevation · {_regionView.TerrainMinM:N0} – {_regionView.TerrainMaxM:N0} m",
        RegionViewMode.SiteScore =>
            $"site score · {_regionView.SiteScoreMin:P0} – {_regionView.SiteScoreMax:P0} · {_regionView.SiteScoreMs:F1} ms",
        _ => $"sites · {_regionView.SiteCount} occupied hexes",
    };

    private void DrawSiteLegend(float y)
    {
        var buildings = _regionView.Buildings;
        var sites = _regionView.SiteCount;
        var status = sites == 0
            ? BuildingStatus.Planned
            : buildings.Select(building => building.Status).DefaultIfEmpty(BuildingStatus.Planned).Max();

        DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), BuildingStatusColor(status));
        DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), new Color(0, 0, 0, 0.35f), filled: false, width: 1f);
        DrawString(_font, new Vector2(PanelLeft + 38, y + 11),
            $"{sites} site(s) · {buildings.Count} installation(s)",
            HorizontalAlignment.Left, -1, 13, AccentText);

        y += LegendRowHeight;
        DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
            "one marker per occupied hex · count shown when > 1",
            HorizontalAlignment.Left, -1, 11, MutedText);

        y += LegendRowHeight;
        DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
            "Enter opens a site · M switches to colony",
            HorizontalAlignment.Left, -1, 11, MutedText);
    }

    private void DrawColonyPanel(float y, ulong regionIndex, float bottom)
    {
        var buildings = _campaign.Buildings(regionIndex);
        if (buildings.Count == 0)
        {
            DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
                "no installations · open a site from region/colony view",
                HorizontalAlignment.Left, -1, 12, MutedText);
            return;
        }

        var sites = _campaign.Sites(regionIndex).Count;
        var hidden = Math.Max(0, buildings.Count - MaxColonyRows);
        var header = hidden == 0
            ? $"colony · {sites} site(s) · {buildings.Count} installation(s)"
            : $"colony · {sites} site(s) · {buildings.Count} installation(s) (+{hidden} more)";
        DrawString(_font, new Vector2(PanelLeft + 18, y + 11),
            header, HorizontalAlignment.Left, -1, 12, new Color(0.95f, 0.8f, 0.55f));
        y += LegendRowHeight;

        var shown = 0;
        foreach (var building in buildings)
        {
            var def = _registry.TryGet(building.DefId, out var d) ? d : null;
            var name = def?.DisplayName ?? building.DefId;
            var statusColor = BuildingStatusColor(building.Status);
            var statusText = building.Status.ToString();

            if (def is not null && def.FootprintRadius > 0)
            {
                DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), statusColor);
                DrawRect(new Rect2(PanelLeft + 18, y, 12, 12), new Color(0, 0, 0, 0.35f), filled: false, width: 1f);
            }

            DrawString(_font, new Vector2(PanelLeft + 38, y + 11),
                $"{name} · {statusText}", HorizontalAlignment.Left, -1, 12, AccentText);

            var detail = building.StatusReason.Length > 0
                ? building.StatusReason
                : building.Progress < 1.0 && building.Status == BuildingStatus.UnderConstruction
                    ? $"{building.Progress:P0} built"
                    : building.Health < 100.0
                        ? $"hp {building.Health:F0}%"
                        : string.Empty;

            if (detail.Length > 0)
            {
                if (detail.Length > 28)
                    detail = detail[..25] + "...";

                DrawString(_font, new Vector2(PanelLeft + 180, y + 11),
                    detail, HorizontalAlignment.Left, -1, 10, statusColor);
            }

            y += LegendRowHeight;
            if (++shown >= MaxColonyRows || y + LegendRowHeight > bottom)
                break;
        }
    }

    private static Color BuildingStatusColor(BuildingStatus status) => status switch
    {
        BuildingStatus.Planned => new Color(0.6f, 0.6f, 0.7f),
        BuildingStatus.UnderConstruction => new Color(0.9f, 0.75f, 0.3f),
        BuildingStatus.Operational => new Color(0.35f, 0.9f, 0.55f),
        BuildingStatus.Idle => new Color(0.5f, 0.7f, 0.9f),
        BuildingStatus.Damaged => new Color(0.9f, 0.5f, 0.3f),
        BuildingStatus.Destroyed => new Color(0.3f, 0.3f, 0.35f),
        _ => new Color(0.85f, 0.85f, 0.92f),
    };

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

        DrawString(_font, new Vector2(24, size.Y - 32), _status, HorizontalAlignment.Left, -1, 15, AccentText);
        DrawString(_font, new Vector2(24, size.Y - 14), SimulationLine(),
            HorizontalAlignment.Left, -1, 12, MutedText);

        var right = _mode switch
        {
            Mode.Globe when _hoverRegion != 0 => $"{_hoverBiome} · {_hoverHexes} hexes",
            Mode.Globe => "SPACE PROGRAM MANAGER · M1",
            Mode.Management =>
                $"{ModeName(_managementView.Overlay)} · {_managementView.SiteCount} sites",
            Mode.Site => $"SITE · {_siteView.InstallationCount} installations",
            _ => $"{ModeName(_regionView.ViewMode)} · {_regionView.SiteCount} sites",
        };

        DrawString(_font, new Vector2(size.X - 340, size.Y - 32), right,
            HorizontalAlignment.Left, -1, 12, MutedText);
        DrawString(_font, new Vector2(size.X - 340, size.Y - 14), _campaignService.Status,
            HorizontalAlignment.Left, -1, 11, MutedText);
    }

    /// <summary>Clock, money and (when in a region) the power situation.</summary>
    private string SimulationLine()
    {
        var line = $"day {GameDay:0} · {GameClockText} · {_clock.Label} · {_campaign.Meta.Credits:N0} cr";
        var regionIndex = _mode switch
        {
            Mode.Region when _regionView.IsLoaded => (ulong?)_regionView.RegionIndex,
            Mode.Management when _managementView.IsLoaded => (ulong?)_managementView.RegionIndex,
            Mode.Site when _siteView.IsLoaded => (ulong?)_regionView.RegionIndex,
            _ => null,
        };

        if (regionIndex is not { } index)
            return line;

        var economy = _campaign.GetEconomy(index);
        line += $" · power {economy.SupplyKw:F0}/{economy.DemandKw:F0} kW · {economy.IdleCount} idle";

        var reason = _campaign.Buildings(index)
            .Select(building => building.StatusReason)
            .FirstOrDefault(text => !string.IsNullOrEmpty(text));

        if (!string.IsNullOrEmpty(reason))
        {
            if (reason.Length > 48)
                reason = reason[..45] + "...";
            line += $" · {reason}";
        }

        return line;
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
        RegionViewMode.Buildings => "SITES",
        _ => "BIOME",
    };

    private static RegionViewMode ParseOverlay(string overlay) => overlay.ToLowerInvariant() switch
    {
        "terrain" => RegionViewMode.Terrain,
        "score" or "sitescore" => RegionViewMode.SiteScore,
        "buildings" or "building" or "sites" or "site" => RegionViewMode.Buildings,
        _ => RegionViewMode.Biome,
    };

    private static int FootprintHexes(BuildingDef def)
        => 3 * def.FootprintRadius * (def.FootprintRadius + 1) + 1;

    private static BodyWorld OpenBody(string bodyKey)
    {
        var key = KnownBodies.TryNormalize(bodyKey) ?? KnownBodies.Moon;

        try
        {
            return BodyWorld.Open(key);
        }
        catch (DirectoryNotFoundException)
        {
            // Running from the Godot project folder: data lives one level up.
            var fallback = ProjectSettings.GlobalizePath("res://../data");
            return BodyWorld.Open(key, fallback);
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

    private static double DoubleArgument(string name, double fallback)
    {
        var value = ArgumentValue(name);
        return value is not null
               && double.TryParse(value, System.Globalization.NumberStyles.Float,
                   System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;
    }

    private static ulong UlongArgument(string name)
        => ulong.TryParse(ArgumentValue(name), out var value) ? value : 0UL;

    private static IEnumerable<string> ArgumentValues(string name)
    {
        var prefix = name + "=";
        foreach (var argument in OS.GetCmdlineUserArgs())
        {
            if (argument.StartsWith(prefix, StringComparison.Ordinal))
                yield return argument[prefix.Length..];
        }
    }

    private static string? ArgumentValue(string name) => ArgumentValues(name).FirstOrDefault();

    private static int IntArgument(string name, int fallback)
        => int.TryParse(ArgumentValue(name), System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : fallback;

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
