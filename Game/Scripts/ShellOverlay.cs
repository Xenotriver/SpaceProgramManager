using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using Sim.Campaign;

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

    /// <summary>True while a menu/pause screen is open.</summary>
    public bool IsOpen => _screen != Screen.Closed;

    /// <summary>True while the slot list is showing (used to refresh it).</summary>
    public bool IsShowingSlots => _screen == Screen.Slots;

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

        DrawString(_font, new Vector2(layout.Panel.Position.X + 28f, layout.Panel.End.Y - 22f), layout.Hint,
            HorizontalAlignment.Left, -1, 12, MutedText);

        DrawRight("M1 · simulation", layout.Panel.End.X - 28f, layout.Panel.End.Y - 22f, 11, DisabledText);
    }

    private void DrawRight(string text, float rightX, float y, int size, Color color)
    {
        var width = _font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        DrawString(_font, new Vector2(rightX - width, y), text, HorizontalAlignment.Left, -1, size, color);
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

            case Key.Up or Key.W:
                MoveSelection(-1);
                return;

            case Key.Down or Key.S:
                MoveSelection(1);
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

        switch (_screen)
        {
            case Screen.Main:
                title = "SPACE PROGRAM MANAGER";
                subtitle = "Moon campaign · construction shell";
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
                    "F5 save · Esc pause/back · / search · Home active region · M colony · Tab overlay", null,
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

            default:
                title = string.Empty;
                subtitle = null;
                hint = string.Empty;
                break;
        }

        var height = 92f + specs.Count * RowHeight + FooterHeight + extraHeight;
        height = Mathf.Min(height, size.Y - 60f);
        var panel = new Rect2(
            Mathf.Round((size.X - width) * 0.5f),
            Mathf.Round((size.Y - height) * 0.5f),
            width,
            height);

        var rows = new List<Row>(specs.Count);
        var y = panel.Position.Y + 86f;

        foreach (var spec in specs)
        {
            var rect = new Rect2(panel.Position.X + 24f, y, panel.Size.X - 48f, RowHeight - 6f);
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
