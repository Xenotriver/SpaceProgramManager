using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using H3;
using H3.Extensions;
using Sim.Biomes;
using Sim.Campaign;
using Sim.Hex;
using Sim.World;

/// <summary>
/// Multi-region operations view. It shows the active region and its immediate H3
/// neighbours on one tangent plane, with rover tracks and location markers.
///
/// This is the M5 AreaView decision: cross-region logistics needs a scale between
/// the globe and one region, but it deliberately loads only seven region tiles.
/// Hovering outlines the whole region without hiding the terrain grid.
/// </summary>
public partial class AreaView : Node2D
{
    /// <summary>A region was clicked with no rover awaiting a destination.</summary>
    [Signal]
    public delegate void RegionOpenedEventHandler(ulong regionIndex);

    /// <summary>A rover was selected and a destination region was clicked.</summary>
    [Signal]
    public delegate void DispatchRequestedEventHandler(string roverId, ulong regionIndex, ulong hex);

    /// <summary>Hover text for the selected rover or region.</summary>
    [Signal]
    public delegate void AreaHoveredEventHandler(string detail);

    private const float MinZoom = 0.5f;
    private const float MaxZoom = 8f;
    private const float DragThresholdPixels = 4f;

    private static readonly Color BorderColor = new(0f, 0f, 0f, 0.28f);
    private static readonly Color ActiveColor = new(1f, 0.92f, 0.45f, 0.95f);
    private static readonly Color HoverColor = new(1f, 1f, 1f, 0.9f);
    private static readonly Color RouteColor = new(0.35f, 0.85f, 1f, 0.95f);
    private static readonly Color RoverColor = new(1f, 0.62f, 0.28f, 1f);
    private static readonly Color SelectedColor = new(0.35f, 1f, 0.62f, 1f);

    private sealed class RegionArea
    {
        public required ulong Index { get; init; }

        public required Vector2 Center { get; init; }

        public required IReadOnlyList<int> Cells { get; init; }

        public required int CenterHex { get; init; }
    }

    private BodyWorld _world = null!;
    private CampaignState _campaign = null!;

    private readonly List<RegionArea> _areas = new();
    private readonly Dictionary<ulong, RegionArea> _areaByIndex = new();
    private readonly Dictionary<(ulong Region, ulong Hex), int> _cellByKey = new();
    private readonly Dictionary<ulong, Vector2[]> _regionBorders = new();
    private readonly Dictionary<ulong, Vector2[]> _screenRegionBorders = new();

    private Vector2[] _centers = Array.Empty<Vector2>();
    private Vector2[][] _corners = Array.Empty<Vector2[]>();
    private Color[] _fills = Array.Empty<Color>();
    private Vector2[] _borders = Array.Empty<Vector2>();
    private Vector2[] _screenBorders = Array.Empty<Vector2>();

    private ArrayMesh? _fillMesh;
    private ImageTexture _whiteTexture = null!;
    private Font _font = null!;

    private Vector2 _origin;
    private Vector2 _span = Vector2.One;
    private float _unitsPerRadian = 1f;
    private float _zoom = 1f;
    private Vector2 _pan;
    private Vector2 _dragStart;
    private bool _screenDirty = true;

    private ulong _centerRegion;
    private int _hoverRegionIndex = -1;
    private string? _hoverRoverId;

    /// <summary>Currently centred region.</summary>
    public ulong CenterRegion => _centerRegion;

    /// <summary>Rover chosen as the source of the next destination click.</summary>
    public string? SelectedRoverId { get; private set; }

    /// <summary>True once a body/region has been loaded.</summary>
    public bool IsLoaded => _areas.Count > 0;

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;

        var white = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        white.Fill(Colors.White);
        _whiteTexture = ImageTexture.CreateFromImage(white);

        GetViewport().SizeChanged += OnViewportResized;
        SetProcessUnhandledInput(true);
    }

    /// <summary>Loads the active region plus its present H3 neighbours.</summary>
    public void Load(BodyWorld world, CampaignState campaign, ulong centerRegion)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(campaign);

        if (!world.TryGetRegionEntry(centerRegion, out _))
            return;

        _world = world;
        _campaign = campaign;
        _centerRegion = centerRegion;

        _areas.Clear();
        _areaByIndex.Clear();
        _cellByKey.Clear();
        _regionBorders.Clear();
        _screenRegionBorders.Clear();

        var regionIndices = new List<ulong> { centerRegion };
        regionIndices.AddRange(HexGrid.Neighbors(new H3Index(centerRegion))
            .Select(neighbour => (ulong)neighbour)
            .Where(index => world.TryGetRegionEntry(index, out _)));

        var center = new H3Index(centerRegion).ToLatLng();
        var regionCenter = ToUnitVector(center);
        var (east, north) = TangentBasis(regionCenter);

        var centers = new List<Vector2>();
        var corners = new List<Vector2[]>();
        var fills = new List<Color>();
        var borderPoints = new List<Vector2>();
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);

        foreach (var regionIndex in regionIndices)
        {
            var tile = world.GetRegion(regionIndex);
            var regionUnit = ToUnitVector(new H3Index(regionIndex).ToLatLng());
            var cells = new List<int>();
            var regionBorder = new List<Vector2>();
            var centerHex = 0;
            var bestDot = -2f;

            for (var ordinal = 0; ordinal < tile.HexCount; ordinal++)
            {
                var cell = tile.Cells[ordinal];
                var cellCenter = ToUnitVector(cell.ToLatLng());

                var dot = cellCenter.Dot(regionUnit);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    centerHex = ordinal;
                }

                var boundary = cell.GetCellBoundaryVertices().ToList();
                var polygon = new Vector2[boundary.Count];
                for (var v = 0; v < boundary.Count; v++)
                {
                    polygon[v] = Project(ToUnitVector(boundary[v]), east, north);
                    min = new Vector2(Mathf.Min(min.X, polygon[v].X), Mathf.Min(min.Y, polygon[v].Y));
                    max = new Vector2(Mathf.Max(max.X, polygon[v].X), Mathf.Max(max.Y, polygon[v].Y));
                }

                var globalIndex = centers.Count;
                centers.Add(Project(cellCenter, east, north));
                corners.Add(polygon);
                _cellByKey[(regionIndex, (ulong)cell)] = globalIndex;
                cells.Add(globalIndex);

                var biome = world.Biomes[tile.BiomeCode(ordinal)];
                var color = BiomePalette.For(biome.Id);
                if (regionIndex != centerRegion)
                    color = new Color(color.R * 0.72f, color.G * 0.72f, color.B * 0.72f, 1f);
                fills.Add(color);

                for (var v = 0; v < polygon.Length; v++)
                {
                    regionBorder.Add(polygon[v]);
                    regionBorder.Add(polygon[(v + 1) % polygon.Length]);
                    borderPoints.Add(polygon[v]);
                    borderPoints.Add(polygon[(v + 1) % polygon.Length]);
                }
            }

            var area = new RegionArea
            {
                Index = regionIndex,
                Center = Project(regionUnit, east, north),
                Cells = cells,
                CenterHex = centerHex,
            };
            _areaByIndex[regionIndex] = area;
            _areas.Add(area);
            _regionBorders[regionIndex] = regionBorder.ToArray();
        }

        _centers = centers.ToArray();
        _corners = corners.ToArray();
        _fills = fills.ToArray();
        _borders = borderPoints.ToArray();
        _origin = -((min + max) * 0.5f);

        var size = GetViewportRect().Size;
        _span = max - min;
        _unitsPerRadian = Mathf.Max(
            1f,
            Mathf.Min(size.X / Mathf.Max(0.0001f, _span.X), size.Y / Mathf.Max(0.0001f, _span.Y)) * 0.86f);
        _zoom = 1f;
        _pan = Vector2.Zero;
        _hoverRegionIndex = -1;
        _hoverRoverId = null;
        SelectedRoverId = null;

        BuildFillMesh();
        _screenDirty = true;
        Visible = true;
        QueueRedraw();
    }

    /// <summary>Refreshes rover markers after a tick or an action.</summary>
    public void Refresh() => QueueRedraw();

    /// <summary>Clears a selected rover so the next click opens a region again.</summary>
    public void ClearSelection()
    {
        SelectedRoverId = null;
        QueueRedraw();
    }

    /// <summary>Chooses the rover whose next region click becomes a dispatch order.</summary>
    public void SelectRover(string roverId)
    {
        if (_campaign is null || !_campaign.Rovers.Any(rover => rover.Id == roverId))
            return;

        SelectedRoverId = roverId;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (!IsLoaded || _fillMesh is null)
            return;

        if (_screenDirty)
            RebuildScreenCache();

        var scale = _unitsPerRadian * _zoom;
        var origin = GetViewportRect().Size * 0.5f + _pan
                     + new Vector2(_origin.X * scale, -_origin.Y * scale);
        var transform = new Transform2D(
            new Vector2(scale, 0f),
            new Vector2(0f, -scale),
            origin);

        DrawMesh(_fillMesh, _whiteTexture, transform);
        DrawMultiline(_screenBorders, BorderColor, 1f, false);

        if (_areaByIndex.TryGetValue(_centerRegion, out var active)
            && _screenRegionBorders.TryGetValue(active.Index, out var activeBorder))
            DrawMultiline(activeBorder, ActiveColor, 2.5f, false);

        if (_hoverRegionIndex >= 0 && _hoverRegionIndex < _areas.Count)
        {
            var hovered = _areas[_hoverRegionIndex];
            if (_screenRegionBorders.TryGetValue(hovered.Index, out var hoverBorder))
                DrawMultiline(hoverBorder, HoverColor, 2f, false);
        }

        DrawRoverTracks();
        DrawRoverMarkers();
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !IsLoaded)
            return;

        switch (@event)
        {
            case InputEventMouseButton button:
                GetViewport().SetInputAsHandled();
                HandleMouseButton(button);
                break;

            case InputEventMouseMotion motion:
                GetViewport().SetInputAsHandled();
                HandleMouseMotion(motion);
                break;

            case InputEventKey key when key.Pressed && key.Keycode == Key.Escape
                                               && SelectedRoverId is not null:
                GetViewport().SetInputAsHandled();
                SelectedRoverId = null;
                QueueRedraw();
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton button)
    {
        switch (button.ButtonIndex)
        {
            case MouseButton.WheelUp when button.Pressed:
                ZoomAt(1.12f, button.Position);
                break;

            case MouseButton.WheelDown when button.Pressed:
                ZoomAt(1f / 1.12f, button.Position);
                break;

            case MouseButton.Right:
                if (button.Pressed)
                    _dragStart = button.Position;
                break;

            case MouseButton.Left:
                if (button.Pressed)
                    _dragStart = button.Position;
                else if (button.Position.DistanceTo(_dragStart) <= DragThresholdPixels)
                    HandleClick(button.Position);
                break;
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (motion.ButtonMask.HasFlag(MouseButtonMask.Right))
        {
            _pan += motion.Relative;
            _screenDirty = true;
            QueueRedraw();
            return;
        }

        var rover = PickRover(motion.Position);
        _hoverRoverId = rover?.Id;
        var hovered = PickRegion(motion.Position);
        var next = hovered is null ? -1 : _areas.IndexOf(hovered);
        if (next == _hoverRegionIndex && rover is null)
            return;

        _hoverRegionIndex = next;
        EmitSignal(SignalName.AreaHovered, HoverDetail(hovered, rover));
        QueueRedraw();
    }

    private void HandleClick(Vector2 position)
    {
        var rover = PickRover(position);
        if (rover is not null)
        {
            SelectedRoverId = SelectedRoverId == rover.Id ? null : rover.Id;
            EmitSignal(SignalName.AreaHovered, RoverDetail(rover));
            QueueRedraw();
            return;
        }

        var area = PickRegion(position);
        if (area is null)
            return;

        if (SelectedRoverId is not null)
        {
            var roverId = SelectedRoverId;
            SelectedRoverId = null;
            EmitSignal(SignalName.DispatchRequested, roverId, area.Index, CenterHexValue(area));
        }
        else
        {
            EmitSignal(SignalName.RegionOpened, area.Index);
        }

        QueueRedraw();
    }

    private RegionArea? PickRegion(Vector2 position)
    {
        RegionArea? best = null;
        var bestDistance = float.MaxValue;
        var scale = _unitsPerRadian * _zoom;

        foreach (var area in _areas)
        {
            var screen = ToScreen(area.Center, scale);
            var distance = screen.DistanceSquaredTo(position);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = area;
            }
        }

        return best;
    }

    private RoverState? PickRover(Vector2 position)
    {
        var scale = _unitsPerRadian * _zoom;
        foreach (var rover in _campaign.Rovers)
        {
            if (CellFor(rover.LocationRegion, rover.LocationHex) is not { } cell)
                continue;

            if (ToScreen(_centers[cell], scale).DistanceTo(position) <= 14f)
                return rover;
        }

        return null;
    }

    private void ZoomAt(float factor, Vector2 position)
    {
        var before = _zoom;
        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(before, _zoom))
            return;

        // Keep the world point under the cursor stable.
        var viewport = GetViewportRect().Size * 0.5f;
        var scaleBefore = _unitsPerRadian * before;
        var scaleAfter = _unitsPerRadian * _zoom;
        var world = (position - viewport - _pan) / scaleBefore;
        _pan += world * (scaleBefore - scaleAfter);
        _screenDirty = true;
        QueueRedraw();
    }

    private void OnViewportResized()
    {
        if (!IsLoaded)
            return;

        var size = GetViewportRect().Size;
        _unitsPerRadian = Mathf.Max(
            1f,
            Mathf.Min(size.X / Mathf.Max(0.0001f, _span.X), size.Y / Mathf.Max(0.0001f, _span.Y)) * 0.86f);
        _screenDirty = true;
        QueueRedraw();
    }

    private void RebuildScreenCache()
    {
        var scale = _unitsPerRadian * _zoom;
        _screenBorders = ToScreen(_borders, scale);
        _screenRegionBorders.Clear();
        foreach (var (region, points) in _regionBorders)
            _screenRegionBorders[region] = ToScreen(points, scale);

        _screenDirty = false;
    }

    private Vector2[] ToScreen(Vector2[] points, float scale)
    {
        var screen = new Vector2[points.Length];
        for (var i = 0; i < points.Length; i++)
            screen[i] = ToScreen(points[i], scale);

        return screen;
    }

    private Vector2 ToScreen(Vector2 point, float scale)
    {
        var viewport = GetViewportRect().Size * 0.5f;
        return viewport + _pan + new Vector2(
            (point.X + _origin.X) * scale,
            -(point.Y + _origin.Y) * scale);
    }

    private void BuildFillMesh()
    {
        var vertexCount = 0;
        foreach (var polygon in _corners)
            vertexCount += polygon.Length * 3;

        var vertices = new Vector2[vertexCount];
        var colors = new Color[vertexCount];
        var index = 0;

        for (var i = 0; i < _corners.Length; i++)
        {
            var center = _centers[i];
            var polygon = _corners[i];
            var color = _fills[i];

            for (var v = 0; v < polygon.Length; v++)
            {
                vertices[index] = center;
                colors[index++] = color;
                vertices[index] = polygon[v];
                colors[index++] = color;
                vertices[index] = polygon[(v + 1) % polygon.Length];
                colors[index++] = color;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        _fillMesh = new ArrayMesh();
        _fillMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    private void DrawRoverTracks()
    {
        var scale = _unitsPerRadian * _zoom;
        foreach (var rover in _campaign.Rovers)
        {
            var plan = _campaign.RouteForRover(rover.Id);
            if (plan is null || plan.LegCount == 0)
                continue;

            var points = new List<Vector2>(plan.LegCount + 1);
            if (CellFor(plan.StartRegion, plan.StartHex) is { } start)
                points.Add(ToScreen(_centers[start], scale));

            foreach (var leg in plan.Legs)
            {
                if (CellFor(leg.ToRegion, leg.ToHex) is { } cell)
                    points.Add(ToScreen(_centers[cell], scale));
            }

            if (points.Count > 1)
                DrawPolyline(points.ToArray(), RouteColor, 3f, true);
        }
    }

    private void DrawRoverMarkers()
    {
        var scale = _unitsPerRadian * _zoom;

        foreach (var rover in _campaign.Rovers)
        {
            if (CellFor(rover.LocationRegion, rover.LocationHex) is not { } cell)
                continue;

            var position = ToScreen(_centers[cell], scale);
            var selected = rover.Id == SelectedRoverId;
            var color = rover.Status == RoverStatus.Enroute ? RouteColor : RoverColor;
            DrawCircle(position, selected ? 8f : 6f, selected ? SelectedColor : color);
            DrawCircle(position, selected ? 4f : 3f, Colors.Black);

            if (rover.DestinationRegion is { } destinationRegion
                && rover.DestinationHex is { } destinationHex
                && CellFor(destinationRegion, destinationHex) is { } destination)
            {
                DrawCircle(ToScreen(_centers[destination], scale), 7f, color, false, 2f);
            }

            DrawString(
                _font,
                position + new Vector2(10f, -10f),
                rover.Name,
                HorizontalAlignment.Left,
                -1,
                13,
                Colors.White);
        }
    }

    private int? CellFor(ulong? region, ulong? hex)
        => region is { } foundRegion
           && hex is { } foundHex
           && _cellByKey.TryGetValue((foundRegion, foundHex), out var cell)
            ? cell
            : null;

    private ulong CenterHexValue(RegionArea area)
    {
        var regionIndex = area.Index;
        var tile = _world.GetRegion(regionIndex);
        return (ulong)tile.Cells[area.CenterHex];
    }

    private string HoverDetail(RegionArea? area, RoverState? rover)
    {
        if (rover is not null)
            return RoverDetail(rover);

        if (area is null)
            return string.Empty;

        var tile = _world.GetRegion(area.Index);
        var biome = _world.Biomes[tile.DominantBiomeCode];
        var marker = SelectedRoverId is null
            ? "click to open · click a rover first to dispatch"
            : "click to dispatch selected rover";
        return $"region {area.Index} · {biome.DisplayName} · {tile.HexCount} hexes · {marker}";
    }

    private static string RoverDetail(RoverState rover)
    {
        var eta = rover.ArrivalGameHour is { } arrival
            ? $" · ETA hour {arrival:0.#}"
            : string.Empty;
        return $"{rover.Name} · {rover.Status} · {rover.Health:0.#}% health{eta}";
    }

    private static Vector2 Project(Vector3 unit, Vector3 east, Vector3 north)
        => new(unit.Dot(east), unit.Dot(north));

    private static (Vector3 East, Vector3 North) TangentBasis(Vector3 center)
    {
        var east = new Vector3(-center.Z, 0f, center.X);
        if (east.LengthSquared() < 1e-8f)
            east = new Vector3(1f, 0f, 0f);

        east = east.Normalized();
        return (east, east.Cross(center).Normalized());
    }

    private static Vector3 ToUnitVector(H3.Model.LatLng latLng)
    {
        var cosLat = Math.Cos(latLng.Latitude);
        return new Vector3(
            (float)(cosLat * Math.Cos(latLng.Longitude)),
            (float)Math.Sin(latLng.Latitude),
            (float)(cosLat * Math.Sin(latLng.Longitude)));
    }
}
