using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim.Campaign;
using Sim.World;

/// <summary>Actions the shell asks <see cref="Main"/> to perform.</summary>
public enum ShellCommand
{
    None,
    Continue,
    NewCampaign,
    PlaySlot,
    DeleteSlot,
    SaveNow,
    Search,
    OpenSaveFolder,
    MainMenu,
    Quit,
    PauseTime,
    Speed1,
    Speed2,
    Speed4,
    SwitchBody,
    UnlockTech,
    PurchaseRover,
    SelectRover,
    OpenArea,
    AcceptContract,
    DeliverContract,
    ClaimReward,
    ResolveEvent,
    StartRepair,
}

/// <summary>
/// Immediate-mode front end: main menu, slot list, pause, settings and toasts.
/// Draws over the world and takes exclusive input while open; it owns no game
/// state and asks <see cref="Main"/> to perform every action.
/// </summary>
public partial class ShellOverlay : Node2D
{
    private enum Screen
    {
        Closed,
        Main,
        Slots,
        Settings,
        Pause,
        ConfirmDelete,
        Search,
        SystemMap,
        TechTree,
        Fleet,
        Missions,
        Maintenance,
    }

    private const float RowHeight = 46f;
    private const float FooterHeight = 58f;
    private const float ToastLifetime = 4.5f;

    private static readonly int[] TimeSpeeds = { 0, 1, 2, 4 };
    private static readonly ShellCommand[] TimeCommands =
    {
        ShellCommand.PauseTime, ShellCommand.Speed1, ShellCommand.Speed2, ShellCommand.Speed4,
    };

    private static readonly Color PanelColor = new(0.045f, 0.055f, 0.075f, 0.97f);
    private static readonly Color RowColor = new(0.10f, 0.12f, 0.16f, 0.9f);
    private static readonly Color HoverColor = new(0.20f, 0.26f, 0.34f, 0.95f);
    private static readonly Color AccentText = new(0.96f, 0.97f, 0.99f);
    private static readonly Color MutedText = new(0.66f, 0.71f, 0.78f);
    private static readonly Color DisabledText = new(0.42f, 0.46f, 0.52f);
    private static readonly Color ErrorColor = new(0.96f, 0.45f, 0.42f);
    private static readonly Color InfoColor = new(0.55f, 0.85f, 1f);
    private static readonly Color WarmColor = new(0.98f, 0.72f, 0.35f);
    private static readonly Color ReadyColor = new(0.35f, 1f, 0.62f);

    /// <summary>
    /// Schematic map: one entry per visible body. Orbits and angles are fixed for
    /// readability rather than astronomical accuracy; only ready bodies are openable.
    /// </summary>
    private static readonly MapBody[] MapBodies =
    {
        new("mercury", "Mercury", new Color(0.62f, 0.60f, 0.57f), 0.08f, 210f, 4f, Vector2.Zero),
        new("venus", "Venus", new Color(0.92f, 0.76f, 0.42f), 0.14f, 135f, 6f, Vector2.Zero),
        new("earth", "Earth", new Color(0.32f, 0.62f, 1f), 0.20f, 80f, 7f, Vector2.Zero),
        new("moon", "Moon", new Color(0.82f, 0.84f, 0.86f), 0.20f, 80f, 3f, new Vector2(16f, -12f)),
        new("mars", "Mars", new Color(0.94f, 0.38f, 0.24f), 0.26f, 25f, 6f, Vector2.Zero),
        new("phobos", "Phobos", new Color(0.58f, 0.50f, 0.44f), 0.26f, 25f, 2f, new Vector2(14f, -10f)),
        new("deimos", "Deimos", new Color(0.60f, 0.54f, 0.48f), 0.26f, 25f, 2f, new Vector2(20f, 8f)),
        new("jupiter", "Jupiter", new Color(0.86f, 0.70f, 0.52f), 0.33f, 330f, 14f, Vector2.Zero),
        new("io", "Io", new Color(0.96f, 0.86f, 0.38f), 0.33f, 330f, 3f, new Vector2(-18f, -16f)),
        new("europa", "Europa", new Color(0.86f, 0.88f, 0.92f), 0.33f, 330f, 3f, new Vector2(-4f, -20f)),
        new("ganymede", "Ganymede", new Color(0.70f, 0.62f, 0.52f), 0.33f, 330f, 4f, new Vector2(14f, -19f)),
        new("callisto", "Callisto", new Color(0.52f, 0.46f, 0.42f), 0.33f, 330f, 3.5f, new Vector2(28f, -14f)),
        new("saturn", "Saturn", new Color(0.90f, 0.80f, 0.55f), 0.39f, 285f, 12f, Vector2.Zero),
        new("titan", "Titan", new Color(0.90f, 0.66f, 0.30f), 0.39f, 285f, 3f, new Vector2(16f, -15f)),
        new("enceladus", "Enceladus", new Color(0.92f, 0.94f, 0.96f), 0.39f, 285f, 2.5f, new Vector2(-16f, -14f)),
        new("uranus", "Uranus", new Color(0.48f, 0.88f, 0.92f), 0.44f, 235f, 9f, Vector2.Zero),
        new("titania", "Titania", new Color(0.68f, 0.78f, 0.80f), 0.44f, 235f, 3f, new Vector2(16f, -11f)),
        new("neptune", "Neptune", new Color(0.30f, 0.44f, 0.96f), 0.47f, 190f, 9f, Vector2.Zero),
        new("triton", "Triton", new Color(0.70f, 0.80f, 0.94f), 0.47f, 190f, 3f, new Vector2(16f, -10f)),
        new("pluto", "Pluto", new Color(0.76f, 0.70f, 0.64f), 0.49f, 160f, 3.5f, Vector2.Zero),
        new("charon", "Charon", new Color(0.66f, 0.64f, 0.62f), 0.49f, 160f, 2f, new Vector2(12f, 8f)),
    };

    private static readonly float[] MapOrbits = { 0.08f, 0.14f, 0.20f, 0.26f, 0.33f, 0.39f, 0.44f, 0.47f, 0.49f };

    private readonly List<Toast> _toasts = new();

    private Screen _screen = Screen.Closed;
    private Screen _returnScreen = Screen.Main;
    private IReadOnlyList<SlotSummary> _slots = Array.Empty<SlotSummary>();
    private string? _lastSlotId;
    private SlotSummary? _pendingDelete;
    private string _search = string.Empty;
    private int _selected;
    private Vector2 _mouse;
    private Font _font = null!;
    private bool _fontReady;

    /// <summary>Raised when the player chooses an action.</summary>
    public event Action<ShellCommand, string?>? Command;

    /// <summary>Save root shown in settings.</summary>
    public string SaveRootLabel { get; set; } = "user://saves";

    /// <summary>Active body shown in the menu subtitle.</summary>
    public string BodyLabel { get; set; } = "Moon";

    /// <summary>Canonical key of the active body, highlighted on the system map.</summary>
    public string BodyKey { get; set; } = KnownBodies.Moon;

    /// <summary>True while a menu/pause screen is open.</summary>
    public bool IsOpen => _screen != Screen.Closed;

    /// <summary>True while the slot list is showing (used to refresh it).</summary>
    public bool IsShowingSlots => _screen == Screen.Slots;

    /// <summary>Live campaign values rendered by the tech screen.</summary>
    public sealed record TechTreeView(
        double SciencePoints,
        IReadOnlySet<string> UnlockedTechs,
        IReadOnlyDictionary<string, string> BuildingNames);

    /// <summary>Supplies tech state on every draw; the shell owns no game state.</summary>
    public Func<TechTreeView>? TechViewProvider { get; set; }

    /// <summary>One rover row in the fleet screen.</summary>
    public sealed record RoverFleetRow(
        string Id,
        string Name,
        string Status,
        string Detail,
        string Right,
        bool Available);

    /// <summary>Live garage and fleet values rendered by the fleet screen.</summary>
    public sealed record RoverFleetView(
        ulong RegionIndex,
        bool GarageReady,
        int Capacity,
        int Stationed,
        IReadOnlyList<RoverFleetRow> Rovers);

    /// <summary>Supplies rover state on every draw; the shell owns no game state.</summary>
    public Func<RoverFleetView>? FleetViewProvider { get; set; }

    private RoverFleetView CurrentFleetView() => FleetViewProvider?.Invoke()
        ?? new RoverFleetView(0UL, false, 0, 0, Array.Empty<RoverFleetRow>());

    private TechTreeView CurrentTechView() => TechViewProvider?.Invoke()
        ?? new TechTreeView(
            0.0,
            new HashSet<string>(StringComparer.Ordinal),
            new Dictionary<string, string>(StringComparer.Ordinal));

    /// <summary>One unaccepted contract offer in the missions screen.</summary>
    public sealed record MissionOfferRow(
        string DefId,
        string DisplayName,
        string Detail,
        string Right,
        bool Available,
        string? DisabledReason);

    /// <summary>One accepted contract in the missions screen.</summary>
    public sealed record MissionActiveRow(
        string Id,
        string DisplayName,
        string Detail,
        string Right,
        bool Deliverable,
        bool Claimable);

    /// <summary>One event option; the payload encodes "eventId|choiceId".</summary>
    public sealed record MissionChoiceRow(string Label, string Detail, string Payload);

    /// <summary>One pending event with its presented choices.</summary>
    public sealed record MissionEventRow(
        string DisplayName,
        string Description,
        IReadOnlyList<MissionChoiceRow> Choices);

    /// <summary>Live contract and event values rendered by the missions screen.</summary>
    public sealed record MissionView(
        double Credits,
        double Science,
        IReadOnlyList<MissionOfferRow> Offers,
        IReadOnlyList<MissionActiveRow> Active,
        IReadOnlyList<MissionEventRow> Events);

    /// <summary>Supplies missions state on every draw; the shell owns no game state.</summary>
    public Func<MissionView>? MissionViewProvider { get; set; }

    private MissionView CurrentMissionsView() => MissionViewProvider?.Invoke()
        ?? new MissionView(
            0.0,
            0.0,
            Array.Empty<MissionOfferRow>(),
            Array.Empty<MissionActiveRow>(),
            Array.Empty<MissionEventRow>());

    /// <summary>One open failure; the payload encodes "regionIndex|buildingId".</summary>
    public sealed record MaintenanceRow(
        string Payload,
        string DisplayName,
        string Detail,
        string Right,
        bool Repairable);

    /// <summary>Live maintenance values rendered by the repair screen.</summary>
    public sealed record MaintenanceView(double Credits, IReadOnlyList<MaintenanceRow> Rows);

    /// <summary>Supplies maintenance state on every draw; the shell owns no game state.</summary>
    public Func<MaintenanceView>? MaintenanceViewProvider { get; set; }

    private MaintenanceView CurrentMaintenanceView() => MaintenanceViewProvider?.Invoke()
        ?? new MaintenanceView(0.0, Array.Empty<MaintenanceRow>());

    /// <summary>Current simulation multiplier shown by the pause-screen buttons.</summary>
    public int TimeSpeed { get; private set; }

    /// <summary>Updates the highlighted time button.</summary>
    public void SetTimeSpeed(int speed)
    {
        TimeSpeed = speed is 1 or 2 or 4 ? speed : 0;
        QueueRedraw();
    }

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;
        _fontReady = true;
        SetProcess(true);
    }

    /// <summary>Opens the main menu.</summary>
    public void OpenMenu(IReadOnlyList<SlotSummary> slots, string? lastSlotId)
    {
        _slots = slots;
        _lastSlotId = lastSlotId;
        _screen = Screen.Main;
        _returnScreen = Screen.Main;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Opens the in-game pause menu.</summary>
    public void OpenPause()
    {
        _returnScreen = Screen.Pause;
        _screen = Screen.Pause;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Opens the region search box (bound to <c>/</c>).</summary>
    public void OpenSearch()
    {
        _returnScreen = Screen.Closed;
        _screen = Screen.Search;
        _search = string.Empty;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Opens the schematic Solar System Map, or closes it if already open.</summary>
    public void ToggleSystemMap()
    {
        if (_screen == Screen.SystemMap)
        {
            Close();
            return;
        }

        _returnScreen = Screen.Closed;
        _screen = Screen.SystemMap;
        var current = Array.FindIndex(MapBodies, body => body.Key == BodyKey);
        _selected = current >= 0 ? current : 0;
        QueueRedraw();
    }

    /// <summary>Opens the tech tree from gameplay or the <c>--tech</c> flag.</summary>
    public void OpenTechTree()
    {
        if (_screen == Screen.TechTree)
        {
            Close();
            return;
        }

        _returnScreen = Screen.Closed;
        _screen = Screen.TechTree;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Opens the rover fleet and garage screen from gameplay.</summary>
    public void OpenFleet()
    {
        if (_screen == Screen.Fleet)
        {
            Close();
            return;
        }

        _returnScreen = Screen.Closed;
        _screen = Screen.Fleet;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Opens the contracts and events screen from gameplay or <c>--missions</c>.</summary>
    public void OpenMissions()
    {
        if (_screen == Screen.Missions)
        {
            Close();
            return;
        }

        _returnScreen = Screen.Closed;
        _screen = Screen.Missions;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Opens the failure/repair screen from gameplay or <c>--maintenance</c>.</summary>
    public void OpenMaintenance()
    {
        if (_screen == Screen.Maintenance)
        {
            Close();
            return;
        }

        _returnScreen = Screen.Closed;
        _screen = Screen.Maintenance;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Replaces the slot list, e.g. after a delete.</summary>
    public void SetSlots(IReadOnlyList<SlotSummary> slots, string? lastSlotId)
    {
        _slots = slots;
        _lastSlotId = lastSlotId;
        _selected = Math.Clamp(_selected, 0, Math.Max(0, slots.Count - 1));
        QueueRedraw();
    }

    /// <summary>Returns to the slot list (after a delete or refresh).</summary>
    public void ShowSlots()
    {
        _pendingDelete = null;
        _screen = Screen.Slots;
        _returnScreen = Screen.Main;
        _selected = 0;
        QueueRedraw();
    }

    /// <summary>Closes the open screen; toasts keep drawing.</summary>
    public void Close()
    {
        _screen = Screen.Closed;
        _pendingDelete = null;
        _search = string.Empty;
        QueueRedraw();
    }

    /// <summary>Shows a transient message above the status bar.</summary>
    public void PushToast(string message, bool error = false)
    {
        if (string.IsNullOrWhiteSpace(message))
            return;

        _toasts.Add(new Toast(message, error, Now() + ToastLifetime));
        if (_toasts.Count > 12)
            _toasts.RemoveRange(0, _toasts.Count - 12);

        QueueRedraw();
    }

    public override void _Process(double delta)
    {
        if (_toasts.Count == 0)
            return;

        var now = Now();
        var removed = _toasts.RemoveAll(toast => toast.Expires <= now);
        if (removed > 0)
            QueueRedraw();
    }

    public override void _Draw()
    {
        if (!_fontReady)
        {
            _font = ThemeDB.FallbackFont;
            _fontReady = true;
        }

        DrawToasts();

        if (!IsOpen)
            return;

        var layout = BuildLayout();
        var size = GetViewportRect().Size;

        DrawRect(new Rect2(Vector2.Zero, size), new Color(0f, 0f, 0f, 0.58f));
        DrawRect(layout.Panel, PanelColor);
        DrawRect(new Rect2(layout.Panel.Position, new Vector2(3f, layout.Panel.Size.Y)), InfoColor);

        DrawString(_font, new Vector2(layout.Panel.Position.X + 28f, layout.Panel.Position.Y + 44f), layout.Title,
            HorizontalAlignment.Left, -1, 25, AccentText);

        if (!string.IsNullOrEmpty(layout.Subtitle))
            DrawString(_font, new Vector2(layout.Panel.Position.X + 28f, layout.Panel.Position.Y + 68f), layout.Subtitle!,
                HorizontalAlignment.Left, -1, 12, MutedText);

        if (_screen == Screen.SystemMap)
        {
            DrawSystemMap(layout.Panel, layout.Rows);
        }
        else
        {
            for (var i = 0; i < layout.Rows.Count; i++)
            {
                var row = layout.Rows[i];
                var hovered = row.Enabled && row.Rect.HasPoint(_mouse);
                var active = i == _selected && row.Enabled;

                if (hovered || active)
                    DrawRect(row.Rect, HoverColor);
                else if (row.Enabled)
                    DrawRect(row.Rect, RowColor);

                if (row.Accent is { } accent)
                    DrawRect(new Rect2(row.Rect.Position, new Vector2(3f, row.Rect.Size.Y)), accent);

                var textX = row.Rect.Position.X + 16f;
                var labelY = row.Detail is null ? row.Rect.Position.Y + 27f : row.Rect.Position.Y + 21f;
                var labelColor = row.Enabled ? AccentText : DisabledText;

                DrawString(_font, new Vector2(textX, labelY), row.Label,
                    HorizontalAlignment.Left, -1, 16, labelColor);

                if (row.Detail is not null)
                    DrawString(_font, new Vector2(textX, row.Rect.Position.Y + 37f), row.Detail,
                        HorizontalAlignment.Left, -1, 12, row.Enabled ? MutedText : DisabledText);

                if (row.Right is not null)
                    DrawRight(row.Right, row.Rect.End.X - 18f, row.Rect.Position.Y + 27f, 12,
                        row.Enabled ? MutedText : DisabledText);

                if (row.DeleteRect is { } delete)
                {
                    DrawRect(delete, new Color(0.55f, 0.16f, 0.16f, 0.75f));
                    DrawString(_font, new Vector2(delete.Position.X + 7f, delete.Position.Y + 19f), "×",
                        HorizontalAlignment.Left, -1, 16, AccentText);
                }
            }

            if (_screen == Screen.Pause)
                DrawTimeControls(layout.Panel);

            if (_screen == Screen.Search)
                DrawSearchBox(layout.Panel);
        }

        DrawString(_font, new Vector2(layout.Panel.Position.X + 28f, layout.Panel.End.Y - 22f), layout.Hint,
            HorizontalAlignment.Left, -1, 12, MutedText);

        DrawRight("M1 · simulation", layout.Panel.End.X - 28f, layout.Panel.End.Y - 22f, 11, DisabledText);
    }

    private void DrawRight(string text, float rightX, float y, int size, Color color)
    {
        var width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(_font, new Vector2(rightX - width, y), text, HorizontalAlignment.Left, -1, size, color);
    }

    private void DrawCentered(string text, float centerX, float y, int size, Color color)
    {
        var width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(_font, new Vector2(centerX - width * 0.5f, y), text, HorizontalAlignment.Left, -1, size, color);
    }

    private static Rect2 SystemMapArea(Rect2 panel)
        => new(
            panel.Position.X + 24f,
            panel.Position.Y + 92f,
            Mathf.Max(80f, panel.Size.X - 48f),
            Mathf.Max(80f, panel.Size.Y - 160f));

    private static Vector2 SystemMapCenter(Rect2 area)
        => new(area.Position.X + area.Size.X * 0.5f, area.Position.Y + area.Size.Y * 0.56f);

    private static float MapScale(Rect2 area)
        => Mathf.Clamp(area.Size.X / 720f, 0.75f, 1.1f);

    private static Vector2 MapBodyPosition(MapBody body, Rect2 area)
    {
        var angle = Mathf.DegToRad(body.AngleDegrees);
        var orbit = new Vector2(
            Mathf.Cos(angle) * area.Size.X * body.Orbit,
            Mathf.Sin(angle) * area.Size.Y * body.Orbit * 0.55f);
        return SystemMapCenter(area) + orbit + body.Offset * MapScale(area);
    }

    private static Rect2 MapBodyRect(MapBody body, Rect2 area)
    {
        var position = MapBodyPosition(body, area);
        var size = Mathf.Max(body.Radius * 2f + 12f, 20f) * MapScale(area);
        return new Rect2(position.X - size * 0.5f, position.Y - size * 0.5f, size, size);
    }

    private void DrawSystemMap(Rect2 panel, IReadOnlyList<Row> rows)
    {
        var area = SystemMapArea(panel);
        DrawRect(area, new Color(0.025f, 0.035f, 0.055f, 0.95f));
        DrawRect(area, new Color(0.38f, 0.52f, 0.68f, 0.5f), filled: false, width: 1f);

        var center = SystemMapCenter(area);
        var scale = MapScale(area);
        var ringColor = new Color(0.52f, 0.66f, 0.82f, 0.14f);

        foreach (var orbit in MapOrbits)
        {
            var points = new Vector2[97];
            for (var i = 0; i < points.Length; i++)
            {
                var angle = i / (float)(points.Length - 1) * Mathf.Tau;
                points[i] = center + new Vector2(
                    Mathf.Cos(angle) * area.Size.X * orbit,
                    Mathf.Sin(angle) * area.Size.Y * orbit * 0.55f);
            }
            DrawPolyline(points, ringColor, 1f, true);
        }

        DrawCircle(center, 18f * scale, new Color(1f, 0.72f, 0.25f, 0.16f));
        DrawCircle(center, 12f * scale, WarmColor);
        DrawCentered("Sun", center.X, center.Y - 28f * scale, 12, WarmColor);

        for (var i = 0; i < Math.Min(rows.Count, MapBodies.Length); i++)
        {
            var body = MapBodies[i];
            var row = rows[i];
            var position = MapBodyPosition(body, area);
            var radius = body.Radius * scale;
            var fill = body.Ready
                ? body.Tint
                : new Color(body.Tint.R, body.Tint.G, body.Tint.B, 0.28f);

            if (row.Enabled && i == _selected)
                DrawCircle(position, radius + 6f * scale, InfoColor);
            else if (row.Enabled && row.Rect.HasPoint(_mouse))
                DrawCircle(position, radius + 4f * scale, WarmColor);

            DrawCircle(position, radius + 1.5f * scale, new Color(0f, 0f, 0f, 0.8f));
            DrawCircle(position, radius, fill);

            var isCurrent = string.Equals(body.Key, BodyKey, StringComparison.OrdinalIgnoreCase);
            var label = isCurrent && body.Ready ? $"{body.Name} · current" : body.Name;
            var labelColor = !body.Ready ? DisabledText : isCurrent ? WarmColor : AccentText;
            DrawCentered(label, position.X, position.Y - radius - 14f * scale,
                body.Ready ? 11 : 10, labelColor);

            if (body.Ready)
                DrawCentered("READY", position.X, position.Y + radius + 13f * scale, 9, ReadyColor);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (!IsOpen)
            return;

        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                HandleKey(key);
                GetViewport().SetInputAsHandled();
                break;

            case InputEventKey { Pressed: false }:
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseMotion motion:
                _mouse = motion.Position;
                QueueRedraw();
                GetViewport().SetInputAsHandled();
                break;

            case InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } button:
                _mouse = button.Position;
                HandleClick(button.Position);
                GetViewport().SetInputAsHandled();
                break;
        }
    }

    private void HandleKey(InputEventKey key)
    {
        if (_screen == Screen.Search)
        {
            switch (key.Keycode)
            {
                case Key.Escape:
                    Back();
                    return;

                case Key.Enter or Key.KpEnter:
                    var text = _search.Trim();
                    if (text.Length == 0)
                        return;

                    var payload = text;
                    Close();
                    Command?.Invoke(ShellCommand.Search, payload);
                    return;

                case Key.Backspace:
                    if (_search.Length > 0)
                        _search = _search[..^1];
                    QueueRedraw();
                    return;
            }

            var unicode = key.Unicode;
            if (unicode >= 32 && unicode < 127 && !key.CtrlPressed)
            {
                _search += (char)unicode;
                QueueRedraw();
            }

            return;
        }

        switch (key.Keycode)
        {
            case Key.Escape:
                Back();
                return;

            case Key.Up or Key.W or Key.Left:
                MoveSelection(-1);
                return;

            case Key.Down or Key.S or Key.Right:
                MoveSelection(1);
                return;

            case Key.M when _screen == Screen.SystemMap:
                Close();
                return;

            case Key.T when _screen == Screen.TechTree:
                Back();
                return;

            case Key.R when _screen == Screen.Fleet:
                Back();
                return;

            case Key.C when _screen == Screen.Missions:
                Back();
                return;

            case Key.X when _screen == Screen.Maintenance:
                Back();
                return;

            case Key.Enter or Key.KpEnter or Key.Space:
                ActivateSelected();
                return;

            case Key.F5 when _screen == Screen.Pause:
                Command?.Invoke(ShellCommand.SaveNow, null);
                return;

            case Key.Key1 when _screen == Screen.Pause:
                Command?.Invoke(ShellCommand.Speed1, null);
                return;

            case Key.Key2 when _screen == Screen.Pause:
                Command?.Invoke(ShellCommand.Speed2, null);
                return;

            case Key.Key4 when _screen == Screen.Pause:
                Command?.Invoke(ShellCommand.Speed4, null);
                return;
        }
    }

    private void HandleClick(Vector2 position)
    {
        var layout = BuildLayout();

        for (var i = 0; i < layout.Rows.Count; i++)
        {
            var row = layout.Rows[i];

            if (row.DeleteRect is { } delete && delete.HasPoint(position))
            {
                if (row.Payload is not null)
                {
                    var slot = _slots.FirstOrDefault(s => s.Id == row.Payload);
                    if (slot is not null)
                    {
                        _pendingDelete = slot;
                        _returnScreen = _screen;
                        _screen = Screen.ConfirmDelete;
                        _selected = 0;
                        QueueRedraw();
                    }
                }

                return;
            }

            if (!row.Rect.HasPoint(position))
                continue;

            _selected = i;
            Activate(row);
            return;
        }

        var timeRects = TimeButtonRects(layout.Panel).ToList();
        var timeIndex = timeRects.FindIndex(rect => rect.HasPoint(position));

        if (_screen == Screen.Pause && timeIndex >= 0)
        {
            Command?.Invoke(TimeCommands[timeIndex], null);
            return;
        }
    }

    private void ActivateSelected()
    {
        var layout = BuildLayout();

        if (_selected < 0 || _selected >= layout.Rows.Count)
            return;

        Activate(layout.Rows[_selected]);
    }

    private void Activate(Row row)
    {
        if (row.DeleteRect is not null)
            return;

        if (!row.Enabled)
        {
            PushToast(row.DisabledReason ?? "not available yet");
            return;
        }

        switch (row.Command)
        {
            case ShellCommand.Continue:
            case ShellCommand.PlaySlot:
            case ShellCommand.DeleteSlot:
            case ShellCommand.SaveNow:
            case ShellCommand.OpenSaveFolder:
            case ShellCommand.MainMenu:
            case ShellCommand.Quit:
            case ShellCommand.SwitchBody:
            case ShellCommand.UnlockTech:
            case ShellCommand.PurchaseRover:
            case ShellCommand.SelectRover:
            case ShellCommand.OpenArea:
            case ShellCommand.AcceptContract:
            case ShellCommand.DeliverContract:
            case ShellCommand.ClaimReward:
            case ShellCommand.ResolveEvent:
            case ShellCommand.StartRepair:
                Command?.Invoke(row.Command, row.Payload);
                return;

            case ShellCommand.NewCampaign:
                Command?.Invoke(ShellCommand.NewCampaign, null);
                return;

            case ShellCommand.Search:
                _returnScreen = _screen;
                _screen = Screen.Search;
                _search = string.Empty;
                _selected = 0;
                QueueRedraw();
                return;
        }

        // Internal navigation is encoded with payload keywords.
        switch (row.Payload)
        {
            case "screen:slots":
                _returnScreen = _screen;
                _screen = Screen.Slots;
                _selected = 0;
                QueueRedraw();
                break;

            case "screen:settings":
                _returnScreen = _screen;
                _screen = Screen.Settings;
                _selected = 0;
                QueueRedraw();
                break;

            case "screen:tech":
                _returnScreen = _screen == Screen.Pause ? Screen.Pause : Screen.Closed;
                _screen = Screen.TechTree;
                _selected = 0;
                QueueRedraw();
                break;

            case "screen:fleet":
                _returnScreen = _screen == Screen.Pause ? Screen.Pause : Screen.Closed;
                _screen = Screen.Fleet;
                _selected = 0;
                QueueRedraw();
                break;

            case "screen:missions":
                _returnScreen = _screen == Screen.Pause ? Screen.Pause : Screen.Closed;
                _screen = Screen.Missions;
                _selected = 0;
                QueueRedraw();
                break;

            case "screen:maintenance":
                _returnScreen = _screen == Screen.Pause ? Screen.Pause : Screen.Closed;
                _screen = Screen.Maintenance;
                _selected = 0;
                QueueRedraw();
                break;

            case "screen:main":
                _screen = Screen.Main;
                _selected = 0;
                QueueRedraw();
                break;

            case "close":
                Close();
                break;

            case "back":
                Back();
                break;
        }
    }

    private void Back()
    {
        switch (_screen)
        {
            case Screen.ConfirmDelete:
                _pendingDelete = null;
                _screen = _returnScreen;
                break;

            case Screen.Slots:
            case Screen.Settings:
                _screen = _returnScreen;
                break;

            case Screen.Search:
                _search = string.Empty;
                _screen = _returnScreen;
                break;

            case Screen.TechTree:
            case Screen.Fleet:
            case Screen.Missions:
            case Screen.Maintenance:
                _screen = _returnScreen;
                break;

            case Screen.Pause:
                Close();
                return;

            case Screen.Main:
                return;

            default:
                Close();
                return;
        }

        _selected = 0;
        QueueRedraw();
    }

    private void MoveSelection(int direction)
    {
        var rows = BuildLayout().Rows;
        if (rows.Count == 0)
            return;

        var index = Math.Clamp(_selected, 0, rows.Count - 1);
        for (var step = 0; step < rows.Count; step++)
        {
            index = (index + direction + rows.Count) % rows.Count;
            if (rows[index].Enabled)
                break;
        }

        _selected = index;
        QueueRedraw();
    }

    private Layout BuildLayout()
    {
        var size = GetViewportRect().Size;
        var width = Mathf.Min(780f, size.X - 80f);

        var specs = new List<RowSpec>();
        string title;
        string? subtitle;
        string hint;
        var extraHeight = 0f;
        float? fixedHeight = null;

        switch (_screen)
        {
            case Screen.Main:
                title = "SPACE PROGRAM MANAGER";
                subtitle = $"{BodyLabel} campaign · construction shell";
                hint = "↑↓ select · Enter choose · Esc close";
                var last = FindLastSlot();
                var canContinue = last is { State: SlotState.Ok };
                specs.Add(new RowSpec(
                    last is null ? "Continue" : $"Continue · {last.Name}",
                    last is null ? "No campaign to continue yet" : Detail(last),
                    null,
                    canContinue,
                    ShellCommand.Continue,
                    last?.Id,
                    false,
                    last?.Problem ?? "Start a new campaign first"));
                specs.Add(new RowSpec("New campaign", "Creates a slot and starts immediately", null,
                    true, ShellCommand.NewCampaign, null));
                specs.Add(new RowSpec("Load campaign", _slots.Count == 0 ? "No slots yet" : $"{_slots.Count} slot(s)",
                    null, true, ShellCommand.None, "screen:slots"));
                specs.Add(new RowSpec("Settings", "Save root and keybinds", null,
                    true, ShellCommand.None, "screen:settings"));
                specs.Add(new RowSpec("Quit", null, null, true, ShellCommand.Quit, null));
                break;

            case Screen.Slots:
                title = "LOAD CAMPAIGN";
                subtitle = _slots.Count == 0 ? "No campaigns yet" : "Newest first";
                hint = "Click a slot to play · × deletes · Esc back";
                if (_slots.Count == 0)
                {
                    specs.Add(new RowSpec("No campaigns yet", "Use New campaign on the main menu", null,
                        false, ShellCommand.None, null));
                }
                else
                {
                    foreach (var slot in _slots.Take(8))
                    {
                        specs.Add(new RowSpec(slot.Name, Detail(slot), Badge(slot),
                            slot.State == SlotState.Ok, ShellCommand.PlaySlot, slot.Id,
                            true, slot.Problem));
                    }
                }

                specs.Add(new RowSpec("Back", null, null, true, ShellCommand.None, "screen:main"));
                break;

            case Screen.Settings:
                title = "SETTINGS";
                subtitle = "Preferences live outside any campaign";
                hint = "Esc back";
                specs.Add(new RowSpec("Save root", SaveRootLine(), null, false, ShellCommand.None, null));
                specs.Add(new RowSpec("Open save folder", "Reveal slots on disk", null,
                    true, ShellCommand.OpenSaveFolder, null));
                specs.Add(new RowSpec("Relief exaggeration", "Change in the colony view with [ and ]", null,
                    false, ShellCommand.None, null));
                specs.Add(new RowSpec("Keybinds",
                    "F5 save · Esc pause/back · / search · M system map · T tech · C missions · X maintenance · A area · R fleet · G colony · Home active region", null,
                    false, ShellCommand.None, null));
                specs.Add(new RowSpec("Back", null, null, true, ShellCommand.None, "screen:main"));
                break;

            case Screen.Pause:
                title = "PAUSED";
                subtitle = "The simulation clock arrives in M1";
                hint = "Esc resume · F5 save";
                specs.Add(new RowSpec("Resume", null, "Esc", true, ShellCommand.None, "close"));
                specs.Add(new RowSpec("Save now", "Campaign is also autosaved on every change", "F5",
                    true, ShellCommand.SaveNow, null));
                specs.Add(new RowSpec("Tech tree", "Spend science points on new technology", "T",
                    true, ShellCommand.None, "screen:tech"));
                specs.Add(new RowSpec("Rover fleet", "Buy rovers and plan cross-region trips", "R",
                    true, ShellCommand.None, "screen:fleet"));
                specs.Add(new RowSpec("Missions", "Accept contracts, deliver cargo and answer events", "C",
                    true, ShellCommand.None, "screen:missions"));
                specs.Add(new RowSpec("Maintenance", "Repair damaged installations", "X",
                    true, ShellCommand.None, "screen:maintenance"));
                specs.Add(new RowSpec("Load campaign", "Switch slot and reload the scene", null,
                    true, ShellCommand.None, "screen:slots"));
                specs.Add(new RowSpec("Settings", null, null, true, ShellCommand.None, "screen:settings"));
                specs.Add(new RowSpec("Main menu", "Return to the front end", null,
                    true, ShellCommand.MainMenu, null));
                specs.Add(new RowSpec("Quit", null, null, true, ShellCommand.Quit, null));
                extraHeight = 54f;
                break;

            case Screen.ConfirmDelete:
                title = "DELETE CAMPAIGN";
                subtitle = _pendingDelete is null
                    ? null
                    : $"{_pendingDelete.Name} · {Detail(_pendingDelete)}";
                hint = "This removes the slot directory; it cannot be undone";
                specs.Add(new RowSpec("Delete permanently", null, null, true,
                    ShellCommand.DeleteSlot, _pendingDelete?.Id));
                specs.Add(new RowSpec("Cancel", null, null, true, ShellCommand.None, "back"));
                break;

            case Screen.Search:
                title = "GO TO REGION";
                subtitle = "Region or fine-hex H3 index (decimal)";
                hint = "Enter jump · Esc cancel";
                break;

            case Screen.SystemMap:
                title = "SOLAR SYSTEM MAP";
                subtitle = $"Current: {BodyLabel} · ready bakes: Moon, Mars";
                hint = "←→ select · Enter travel · M/Esc close · click a body";
                fixedHeight = 680f;
                foreach (var body in MapBodies)
                {
                    specs.Add(new RowSpec(
                        body.Name,
                        null,
                        body.Ready ? "ready" : "not baked",
                        body.Ready,
                        ShellCommand.SwitchBody,
                        body.Key,
                        false,
                        $"{body.Name} has no baked terrain yet"));
                }
                break;

            case Screen.TechTree:
                var techView = CurrentTechView();
                title = "TECH TREE";
                subtitle = $"Science available: {techView.SciencePoints:0.#} · research is immediate";
                hint = "↑↓ select · Enter research · T/Esc close";
                foreach (var tech in TechCatalogue.All)
                {
                    var unlocked = techView.UnlockedTechs.Contains(tech.Id);
                    var missing = tech.Prerequisites
                        .Where(id => !techView.UnlockedTechs.Contains(id))
                        .ToList();
                    var ready = techView.SciencePoints + 1e-9 >= tech.ScienceCost;

                    string right;
                    string? reason;
                    if (unlocked)
                    {
                        right = "researched";
                        reason = $"{tech.DisplayName} is already researched";
                    }
                    else if (missing.Count > 0)
                    {
                        right = $"needs {string.Join(", ", MissingTechNames(missing))}";
                        reason = right;
                    }
                    else
                    {
                        right = $"{tech.ScienceCost:0.#} science";
                        reason = $"need {tech.ScienceCost:0.#} science";
                    }

                    specs.Add(new RowSpec(
                        tech.DisplayName,
                        TechDetail(tech, techView),
                        right,
                        !unlocked && missing.Count == 0 && ready,
                        ShellCommand.UnlockTech,
                        tech.Id,
                        false,
                        reason));
                }
                break;

            case Screen.Fleet:
                var fleet = CurrentFleetView();
                var garage = fleet.GarageReady
                    ? $"garage ready · {fleet.Stationed}/{fleet.Capacity} slots"
                    : "no operational rover garage here";
                title = "ROVER FLEET";
                subtitle = $"Region {fleet.RegionIndex} · {garage} · {fleet.Rovers.Count} rover(s)";
                hint = "Click a rover, then a region in the area view to dispatch · R/Esc close";
                specs.Add(new RowSpec("Open area operations",
                    "Show rover tracks and dispatch targets", "A",
                    true, ShellCommand.OpenArea, null));
                specs.Add(new RowSpec(
                    $"Purchase {RoverCatalogue.Mk1.DisplayName}",
                    $"{RoverCatalogue.Mk1.Cost.GetValueOrDefault("credits"):N0} cr · " +
                    $"{RoverCatalogue.Mk1.Cost.GetValueOrDefault("metals"):0.#} metals · " +
                    $"{RoverCatalogue.Mk1.Cost.GetValueOrDefault("electronics"):0.#} electronics",
                    fleet.GarageReady && fleet.Stationed < fleet.Capacity ? "garage" : "unavailable",
                    fleet.GarageReady && fleet.Stationed < fleet.Capacity,
                    ShellCommand.PurchaseRover,
                    RoverCatalogue.Mk1Id,
                    false,
                    fleet.GarageReady
                        ? "garage is full"
                        : "build and finish a Rover Garage in this region first"));

                foreach (var rover in fleet.Rovers.Take(8))
                {
                    specs.Add(new RowSpec(
                        rover.Name,
                        rover.Detail,
                        rover.Right,
                        rover.Available,
                        ShellCommand.SelectRover,
                        rover.Id,
                        false,
                        $"{rover.Name} is unavailable ({rover.Status})"));
                }

                if (fleet.Rovers.Count > 8)
                {
                    specs.Add(new RowSpec(
                        $"+ {fleet.Rovers.Count - 8} more rover(s)",
                        "Fleets above eight need a wider display",
                        null,
                        false,
                        ShellCommand.None,
                        null));
                }

                specs.Add(new RowSpec("Back", null, null, true, ShellCommand.None, "close"));
                break;

            case Screen.Missions:
                var missions = CurrentMissionsView();
                title = "MISSIONS";
                subtitle = $"{missions.Credits:N0} cr · {missions.Science:0.#} science · partial delivery pays proportionally";
                hint = "↑↓ select · Enter act · C/Esc close";

                if (missions.Events.Count == 0)
                {
                    specs.Add(new RowSpec("No pending events", "Telemetry is quiet", null,
                        false, ShellCommand.None, null));
                }

                foreach (var item in missions.Events.Take(1))
                {
                    specs.Add(new RowSpec(item.DisplayName, item.Description, "event",
                        false, ShellCommand.None, null));
                    foreach (var choice in item.Choices)
                    {
                        specs.Add(new RowSpec(choice.Label, choice.Detail, null,
                            true, ShellCommand.ResolveEvent, choice.Payload));
                    }
                }

                if (missions.Events.Count > 1)
                {
                    specs.Add(new RowSpec($"+ {missions.Events.Count - 1} more event(s)",
                        "Resolve the first one to see the next", null,
                        false, ShellCommand.None, null));
                }

                specs.Add(new RowSpec("CONTRACTS — OFFERS", null, null, false, ShellCommand.None, null));
                if (missions.Offers.Count == 0)
                {
                    specs.Add(new RowSpec("No new offers", "Every contract is already on the books", null,
                        false, ShellCommand.None, null));
                }

                foreach (var offer in missions.Offers.Take(2))
                {
                    specs.Add(new RowSpec(offer.DisplayName, offer.Detail, offer.Right,
                        offer.Available, ShellCommand.AcceptContract, offer.DefId,
                        false, offer.DisabledReason));
                }

                if (missions.Offers.Count > 2)
                {
                    specs.Add(new RowSpec($"+ {missions.Offers.Count - 2} more offer(s)",
                        "Accept current work to see the next", null,
                        false, ShellCommand.None, null));
                }

                specs.Add(new RowSpec("CONTRACTS — ACTIVE", null, null, false, ShellCommand.None, null));
                if (missions.Active.Count == 0)
                {
                    specs.Add(new RowSpec("No active contracts", "Accept an offer above", null,
                        false, ShellCommand.None, null));
                }

                foreach (var active in missions.Active.Take(2))
                {
                    specs.Add(new RowSpec(active.DisplayName, active.Detail, active.Right,
                        active.Deliverable, ShellCommand.DeliverContract, active.Id,
                        false, "nothing left to deliver or no stock available"));
                    if (active.Claimable)
                    {
                        specs.Add(new RowSpec("Settle and claim",
                            "Close now; partial delivery reduces the payout", null,
                            true, ShellCommand.ClaimReward, active.Id));
                    }
                }

                if (missions.Active.Count > 2)
                {
                    specs.Add(new RowSpec($"+ {missions.Active.Count - 2} more contract(s)",
                        "More space is needed for the full list", null,
                        false, ShellCommand.None, null));
                }

                specs.Add(new RowSpec("Back", null, null, true, ShellCommand.None, "close"));
                break;

            case Screen.Maintenance:
                var maintenance = CurrentMaintenanceView();
                title = "MAINTENANCE";
                subtitle = $"{maintenance.Credits:N0} cr · repairs finish in game time";
                hint = "↑↓ select · Enter start repair · X/Esc close";

                if (maintenance.Rows.Count == 0)
                {
                    specs.Add(new RowSpec("No open failures", "All installations are healthy", null,
                        false, ShellCommand.None, null));
                }

                foreach (var row in maintenance.Rows.Take(8))
                {
                    specs.Add(new RowSpec(row.DisplayName, row.Detail, row.Right,
                        row.Repairable, ShellCommand.StartRepair, row.Payload,
                        false, row.Repairable ? null : "repair already in progress or unknown cause"));
                }

                if (maintenance.Rows.Count > 8)
                {
                    specs.Add(new RowSpec($"+ {maintenance.Rows.Count - 8} more failure(s)",
                        "Repair the first ones to see the rest", null,
                        false, ShellCommand.None, null));
                }

                specs.Add(new RowSpec("Back", null, null, true, ShellCommand.None, "close"));
                break;

            default:
                title = string.Empty;
                subtitle = null;
                hint = string.Empty;
                break;
        }

        var height = fixedHeight ?? 92f + specs.Count * RowHeight + FooterHeight + extraHeight;
        height = Mathf.Min(height, size.Y - 60f);
        var panel = new Rect2(
            Mathf.Round((size.X - width) * 0.5f),
            Mathf.Round((size.Y - height) * 0.5f),
            width,
            height);

        var rows = new List<Row>(specs.Count);
        var y = panel.Position.Y + 86f;

        for (var i = 0; i < specs.Count; i++)
        {
            var spec = specs[i];
            var rect = _screen == Screen.SystemMap
                ? MapBodyRect(MapBodies[i], panel)
                : new Rect2(panel.Position.X + 24f, y, panel.Size.X - 48f, RowHeight - 6f);
            Rect2? delete = null;

            if (spec.ShowDelete)
                delete = new Rect2(rect.End.X - 34f, rect.Position.Y + 8f, 26f, 26f);

            rows.Add(new Row
            {
                Label = spec.Label,
                Detail = spec.Detail,
                Right = spec.Right,
                Rect = rect,
                Enabled = spec.Enabled,
                Command = spec.Command,
                Payload = spec.Payload,
                DeleteRect = delete,
                DisabledReason = spec.DisabledReason,
                Accent = spec.Enabled ? null : DisabledText,
            });

            y += RowHeight;
        }

        return new Layout(panel, title, subtitle, hint, rows);
    }

    private IEnumerable<Rect2> TimeButtonRects(Rect2 panel)
    {
        const float buttonWidth = 74f;
        const float gap = 10f;
        var startX = panel.Position.X + 28f;
        var y = panel.End.Y - FooterHeight - 42f;

        for (var i = 0; i < 4; i++)
            yield return new Rect2(startX + i * (buttonWidth + gap), y, buttonWidth, 30f);
    }

    private void DrawTimeControls(Rect2 panel)
    {
        var labels = new[] { "Pause", "1×", "2×", "4×" };
        var index = 0;

        foreach (var rect in TimeButtonRects(panel))
        {
            var active = TimeSpeeds[index] == TimeSpeed;
            var fill = active ? new Color(0.16f, 0.30f, 0.23f, 0.95f) : new Color(0.12f, 0.13f, 0.16f, 0.9f);
            DrawRect(rect, fill);
            DrawRect(rect, active ? new Color(0.35f, 1f, 0.6f, 0.9f) : InfoColor,
                filled: false, width: active ? 1.5f : 1f);
            DrawString(_font, new Vector2(rect.Position.X + 12f, rect.Position.Y + 20f), labels[index],
                HorizontalAlignment.Left, -1, 13, active ? new Color(0.7f, 1f, 0.8f) : AccentText);
            index++;
        }

        var detail = TimeSpeed == 0
            ? "time paused · space resumes at 1×"
            : $"1 real s = {TimeSpeed} game h · build times advance";
        DrawString(_font, new Vector2(panel.Position.X + 28f + 4f * 84f + 12f, panel.End.Y - FooterHeight - 20f),
            detail, HorizontalAlignment.Left, -1, 11, WarmColor);
    }

    private void DrawSearchBox(Rect2 panel)
    {
        var rect = new Rect2(panel.Position.X + 24f, panel.Position.Y + 96f, panel.Size.X - 48f, 46f);
        DrawRect(rect, RowColor);
        DrawRect(rect, InfoColor, filled: false, width: 1f);

        var text = _search.Length == 0 ? "type an H3 index…" : _search;
        DrawString(_font, new Vector2(rect.Position.X + 14f, rect.Position.Y + 29f), text,
            HorizontalAlignment.Left, -1, 16, _search.Length == 0 ? DisabledText : AccentText);

        if (_search.Length > 0)
        {
            var width = _font.GetStringSize(_search, HorizontalAlignment.Left, -1, 16).X;
            DrawRect(new Rect2(rect.Position.X + 16f + width, rect.Position.Y + 12f, 2f, 22f), InfoColor);
        }
    }

    private void DrawToasts()
    {
        if (_toasts.Count == 0)
            return;

        var size = GetViewportRect().Size;
        var now = Now();
        var visible = _toasts.Where(toast => toast.Expires > now).TakeLast(5).ToList();
        var y = size.Y - 176f;

        foreach (var toast in visible)
        {
            var width = Mathf.Min(640f, size.X - 120f);
            var rect = new Rect2(24f, y, width, 26f);
            DrawRect(rect, new Color(0.03f, 0.04f, 0.06f, 0.92f));
            DrawRect(new Rect2(rect.Position, new Vector2(3f, rect.Size.Y)),
                toast.Error ? ErrorColor : InfoColor);
            DrawString(_font, new Vector2(rect.Position.X + 12f, rect.Position.Y + 18f), toast.Message,
                HorizontalAlignment.Left, -1, 13, toast.Error ? ErrorColor : AccentText);
            y -= 30f;
        }
    }

    private static string TechDetail(TechDefinition tech, TechTreeView view)
    {
        var buildings = tech.UnlockedBuildingIds
            .Select(id => view.BuildingNames.TryGetValue(id, out var name) ? name : id)
            .ToList();
        var unlocks = buildings.Count == 0
            ? "no direct unlocks"
            : $"unlocks {string.Join(", ", buildings)}";
        return $"tier {tech.Tier} · {tech.Description} · {unlocks}";
    }

    private static IEnumerable<string> MissingTechNames(IEnumerable<string> ids)
        => ids.Select(id => TechCatalogue.TryGet(id, out var def) && def is not null
            ? def.DisplayName
            : id);

    private string SaveRootLine()
        => ProjectSettings.GlobalizePath(SaveRootLabel);

    private SlotSummary? FindLastSlot()
    {
        if (_lastSlotId is not null)
            return _slots.FirstOrDefault(slot => slot.Id == _lastSlotId);

        return _slots.FirstOrDefault();
    }

    private static string Detail(SlotSummary slot)
    {
        var parts = new List<string>
        {
            $"{slot.BuildingCount} building(s)",
            $"{slot.VisitedRegionCount} region(s)",
            RelativeTime(slot.LastPlayedUtc),
        };

        if (slot.State != SlotState.Ok)
            parts.Add(slot.Problem ?? slot.State.ToString());

        return string.Join(" · ", parts);
    }

    private static string Badge(SlotSummary slot) => slot.State switch
    {
        SlotState.Incompatible => "incompatible bake",
        SlotState.Unreadable => "unreadable",
        _ => slot.Credits > 0 ? $"{slot.Credits:N0} cr" : string.Empty,
    };

    private static string RelativeTime(DateTimeOffset when)
    {
        if (when == default)
            return "never played";

        var span = DateTimeOffset.UtcNow - when;
        if (span.TotalMinutes < 1)
            return "just now";
        if (span.TotalHours < 1)
            return $"{(int)span.TotalMinutes} min ago";
        if (span.TotalDays < 1)
            return $"{(int)span.TotalHours} h ago";
        if (span.TotalDays < 30)
            return $"{(int)span.TotalDays} d ago";

        return when.ToLocalTime().ToString("yyyy-MM-dd");
    }

    private static double Now() => Time.GetTicksMsec() / 1000.0;

    private sealed record Toast(string Message, bool Error, double Expires);

    private sealed record MapBody(
        string Key,
        string Name,
        Color Tint,
        float Orbit,
        float AngleDegrees,
        float Radius,
        Vector2 Offset)
    {
        public bool Ready => KnownBodies.IsReady(Key);
    }

    private sealed record Layout(Rect2 Panel, string Title, string? Subtitle, string Hint, List<Row> Rows);

    private sealed record RowSpec(
        string Label,
        string? Detail,
        string? Right,
        bool Enabled,
        ShellCommand Command,
        string? Payload,
        bool ShowDelete = false,
        string? DisabledReason = null);

    private sealed class Row
    {
        public required string Label { get; init; }
        public string? Detail { get; init; }
        public string? Right { get; init; }
        public required Rect2 Rect { get; init; }
        public bool Enabled { get; init; }
        public ShellCommand Command { get; init; }
        public string? Payload { get; init; }
        public Rect2? DeleteRect { get; init; }
        public string? DisabledReason { get; init; }
        public Color? Accent { get; init; }
    }
}
