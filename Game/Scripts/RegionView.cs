using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using H3;
using H3.Algorithms;
using H3.Extensions;
using Sim.Biomes;
using Sim.Buildings;
using Sim.Movement;
using Sim.Site;
using Sim.World;

/// <summary>Colour overlay shown on top of the region hex grid.</summary>
public enum RegionViewMode
{
    /// <summary>Biome colours (the default).</summary>
    Biome = 0,

    /// <summary>Elevation shading, normalised to this region's min/max.</summary>
    Terrain = 1,

    /// <summary>Composite site-score heatmap over a 7-hex footprint.</summary>
    SiteScore = 2,

    /// <summary>Dimmed base map with placed-building footprints and placement preview.</summary>
    Buildings = 3,
}

/// <summary>
/// One region, zoomed in: every fine hex of the region as a flat polygon on a
/// local tangent plane (gnomonic projection about the region centre).
///
/// The globe is the navigation view; this is the "look at the terrain" view
/// (design doc §2.6). Hexes are filled with their biome colour, outlined so the
/// grid reads, and craters are marked with a dot because they are an overlay,
/// not a biome.
/// </summary>
public partial class RegionView : Node2D
{
    /// <summary>Hovered hex, or -1 when the cursor is off the region.</summary>
    [Signal]
    public delegate void HexHoveredEventHandler(int ordinal, string biomeId, int elevationM, int craterKind);

    /// <summary>Clicked hex.</summary>
    [Signal]
    public delegate void HexSelectedEventHandler(int ordinal, string biomeId, int elevationM, int craterKind);

    /// <summary>Route preview computed: legs, kilometres, hours.</summary>
    [Signal]
    public delegate void RouteComputedEventHandler(int legs, double distanceKm, double hours);

    /// <summary>Route preview cleared (new start picked, or nothing selected).</summary>
    [Signal]
    public delegate void RouteClearedEventHandler();

    /// <summary>Overlay mode changed.</summary>
    [Signal]
    public delegate void ViewModeChangedEventHandler(int mode);

    /// <summary>Active building definition changed.</summary>
    [Signal]
    public delegate void ActiveBuildingChangedEventHandler(string defId);

    /// <summary>Building count in this region changed.</summary>
    [Signal]
    public delegate void BuildingsChangedEventHandler(int count);

    /// <summary>Result of a placement/removal attempt.</summary>
    [Signal]
    public delegate void PlacementFeedbackEventHandler(bool success, string message);

    private const float MinZoom = 0.6f;
    private const float MaxZoom = 12f;
    private const float DragThresholdPixels = 4f;

    private static readonly Color BorderColor = new(0f, 0f, 0f, 0.22f);
    private static readonly Color HoverColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color SingleCraterColor = new(0.91f, 0.69f, 0.29f, 0.9f);
    private static readonly Color MultiCraterColor = new(0.89f, 0.41f, 0.29f, 0.9f);

    private BodyWorld _world = null!;
    private RegionTile _tile = null!;

    private Vector2[] _centers = Array.Empty<Vector2>();
    private Vector3[] _unitCenters = Array.Empty<Vector3>();
    private Vector2[][] _corners = Array.Empty<Vector2[]>();
    private Color[] _biomeFills = Array.Empty<Color>();
    private Color[] _terrainFills = Array.Empty<Color>();
    private Color[] _siteFills = Array.Empty<Color>();
    private Color[] _buildingFills = Array.Empty<Color>();
    private Color[] _activeFills = Array.Empty<Color>();
    private double[]? _siteScores;
    private double _siteScoreMs;
    private double _siteScoreMin;
    private double _siteScoreMax = 1.0;
    private short _terrainMin;
    private short _terrainMax;
    private string[] _biomeIds = Array.Empty<string>();
    private short[] _elevations = Array.Empty<short>();
    private CraterKind[] _craters = Array.Empty<CraterKind>();
    private int[][] _neighbours = Array.Empty<int[]>();
    private readonly Dictionary<ulong, int> _ordinalByIndex = new();
    private float _cellRadius;
    private double _radiusKm = 1737.4;

    private BuildingRegistry? _registry;
    private List<BuildingDef> _buildingChoices = new();
    private BuildingDef? _activeBuilding;
    private List<BuildingInstance> _buildings = new();
    private Dictionary<ulong, BuildingInstance> _occupancy = new();
    private readonly Dictionary<ulong, List<BuildingInstance>> _buildingsByRegion = new();
    private readonly Dictionary<string, Color> _buildingColorCache = new();
    private int[] _ghostOrdinals = Array.Empty<int>();
    private bool _ghostValid;
    private string _ghostError = string.Empty;
    private Font _font = null!;

    private RoutePlan? _route;
    private int _routeFrom = -1;

    /// <summary>Route preview for the current pair of clicks, or null.</summary>
    public RoutePlan? Route => _route;

    /// <summary>Ordinal of the route start hex, or -1.</summary>
    public int RouteFrom => _routeFrom;

    /// <summary>Rover cruise speed used by the movement preview (km/h).</summary>
    public double BaseSpeedKmh { get; set; } = 10.0;

    private readonly Dictionary<int, Vector2[]> _drawBuffers = new();
    private readonly Dictionary<int, Vector2[]> _closedBuffers = new();
    private Vector2[] _borderPoints = Array.Empty<Vector2>();
    private ArrayMesh? _fillMesh;
    private bool _fillMeshDirty = true;
    private ImageTexture _whiteTexture = null!;
    private double _drawTotalMs;
    private int _drawCount;

    private Vector2 _center;
    private Vector2 _pan;
    private float _pixelsPerRadian = 8000f;
    private float _zoom = 1f;

    private int _hovered = -1;
    private int _selected = -1;

    private bool _dragging;
    private bool _panning;
    private Vector2 _dragStart;
    private Vector2 _lastMouse;

    /// <summary>Region shown by this view, or 0 before the first <see cref="Load"/>.</summary>
    public ulong RegionIndex => _tile?.Entry.Index ?? 0;

    public string RegionBiomeId { get; private set; } = string.Empty;

    /// <summary>Distinct biome ids present in this region, in first-seen order.</summary>
    public IReadOnlyList<string> BiomesPresent { get; private set; } = Array.Empty<string>();

    public int HexCount => _tile?.HexCount ?? 0;

    public bool IsLoaded => _tile is not null;

    /// <summary>Current colour overlay.</summary>
    public RegionViewMode ViewMode { get; private set; } = RegionViewMode.Biome;

    /// <summary>Site-scoring configuration used by the heatmap and placement validation.</summary>
    public SiteScorerConfig SiteConfig { get; set; } = new() { BodyRadiusKm = 1737.4 };

    /// <summary>Building selected for placement, or null before a registry is set.</summary>
    public BuildingDef? ActiveBuilding => _activeBuilding;

    /// <summary>Number of building definitions available for cycling.</summary>
    public int BuildingChoiceCount => _buildingChoices.Count;

    /// <summary>Buildings placed in the current region (in-memory until Phase B persistence).</summary>
    public IReadOnlyList<BuildingInstance> Buildings => _buildings;

    /// <summary>Validation error for the hovered placement preview, or empty.</summary>
    public string GhostError => _ghostError;

    /// <summary>Whether the hovered placement preview is valid.</summary>
    public bool GhostValid => _ghostValid;

    /// <summary>Lowest elevation in the current region (metres).</summary>
    public short TerrainMinM => _terrainMin;

    /// <summary>Highest elevation in the current region (metres).</summary>
    public short TerrainMaxM => _terrainMax;

    /// <summary>Lowest composite site score in the current region.</summary>
    public double SiteScoreMin => _siteScoreMin;

    /// <summary>Highest composite site score in the current region.</summary>
    public double SiteScoreMax => _siteScoreMax;

    /// <summary>Time spent computing the site-score heatmap for the current region (ms).</summary>
    public double SiteScoreMs => _siteScoreMs;

    /// <summary>Composite site score at a hex ordinal, or null before scores are computed.</summary>
    public double? SiteScoreAt(int ordinal)
        => _siteScores is not null && (uint)ordinal < (uint)_siteScores.Length
            ? _siteScores[ordinal]
            : null;

    /// <summary>CPU time spent in the most recent <c>_Draw</c> call (ms).</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>Average CPU time per <c>_Draw</c> since <see cref="ResetDrawStats"/> (ms).</summary>
    public double AverageDrawMs => _drawCount > 0 ? _drawTotalMs / _drawCount : 0.0;

    /// <summary>Clears the draw-time accumulator used by the performance HUD.</summary>
    public void ResetDrawStats()
    {
        _drawTotalMs = 0.0;
        _drawCount = 0;
    }

    public void Load(BodyWorld world, ulong regionIndex)
    {
        _world = world;
        _tile = world.GetRegion(regionIndex);
        RegionBiomeId = world.Biomes[_tile.DominantBiomeCode].Id;

        var cells = _tile.Cells;
        var count = cells.Count;

        _centers = new Vector2[count];
        _unitCenters = new Vector3[count];
        _corners = new Vector2[count][];
        _biomeFills = new Color[count];
        _terrainFills = new Color[count];
        _buildingFills = new Color[count];
        _siteFills = Array.Empty<Color>();
        _siteScores = null;
        _siteScoreMs = 0;
        _biomeIds = new string[count];
        _elevations = new short[count];
        _craters = new CraterKind[count];
        _radiusKm = world.Manifest.Body.RadiusKm;
        SiteConfig = SiteConfig with { BodyRadiusKm = _radiusKm };

        var regionCenter = ToUnitVector(_tile.Region.ToLatLng());
        var (east, north) = TangentBasis(regionCenter);
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        var biomesPresent = new List<string>();
        var borderPointCount = 0;

        for (var i = 0; i < count; i++)
        {
            var record = _tile[i];
            var biome = world.Biomes[record.BiomeCode];

            _biomeFills[i] = BiomePalette.For(biome.Id);
            _biomeIds[i] = biome.Id;
            _elevations[i] = record.ElevationM;
            _craters[i] = record.Crater;

            if (!biomesPresent.Contains(biome.Id))
                biomesPresent.Add(biome.Id);

            var boundary = cells[i].GetCellBoundaryVertices().ToList();
            var corners = new Vector2[boundary.Count];

            for (var v = 0; v < boundary.Count; v++)
            {
                corners[v] = Project(ToUnitVector(boundary[v]), regionCenter, east, north);
                min = new Vector2(Mathf.Min(min.X, corners[v].X), Mathf.Min(min.Y, corners[v].Y));
                max = new Vector2(Mathf.Max(max.X, corners[v].X), Mathf.Max(max.Y, corners[v].Y));
            }

            _corners[i] = corners;
            _unitCenters[i] = ToUnitVector(cells[i].ToLatLng());
            _centers[i] = Project(_unitCenters[i], regionCenter, east, north);
            borderPointCount += corners.Length * 2;
        }

        _borderPoints = new Vector2[borderPointCount];
        _fillMeshDirty = true;

        _terrainMin = short.MaxValue;
        _terrainMax = short.MinValue;
        for (var i = 0; i < count; i++)
        {
            if (_elevations[i] < _terrainMin) _terrainMin = _elevations[i];
            if (_elevations[i] > _terrainMax) _terrainMax = _elevations[i];
        }

        BiomesPresent = biomesPresent;

        var elevationSpan = Math.Max(1, _terrainMax - _terrainMin);
        for (var i = 0; i < count; i++)
        {
            _terrainFills[i] = TerrainPalette.For((_elevations[i] - _terrainMin) / (double)elevationSpan);

            var biomeFill = _biomeFills[i];
            _buildingFills[i] = new Color(biomeFill.R * 0.32f, biomeFill.G * 0.32f, biomeFill.B * 0.32f, 1f);
        }

        BuildAdjacency(cells);
        ClearRoute();
        _cellRadius = EstimateCellRadius();

        // Restore in-memory buildings for this region (persistence is Phase B).
        if (!_buildingsByRegion.TryGetValue(regionIndex, out var buildings))
            _buildingsByRegion[regionIndex] = buildings = new List<BuildingInstance>();

        _buildings = buildings;
        _occupancy = new Dictionary<ulong, BuildingInstance>(BuildingRegistry.BuildOccupancy(_buildings));

        _hovered = -1;
        _selected = -1;

        if (ViewMode == RegionViewMode.SiteScore)
            EnsureSiteScores();

        UpdateActiveFills();
        UpdateGhost();

        var size = GetViewportRect().Size;
        var span = new Vector2(Mathf.Max(max.X - min.X, 1e-6f), Mathf.Max(max.Y - min.Y, 1e-6f));
        var fit = Mathf.Min(size.X * 0.92f / span.X, size.Y * 0.84f / span.Y);

        _center = size * 0.5f;
        _zoom = 1f;
        _pixelsPerRadian = fit;
        _pan = -((min + max) * 0.5f) * fit;

        Visible = true;
        EmitSignal(SignalName.BuildingsChanged, _buildings.Count);
        QueueRedraw();
    }

    public override void _Ready()
    {
        _font = ThemeDB.FallbackFont;

        var white = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        white.Fill(Colors.White);
        _whiteTexture = ImageTexture.CreateFromImage(white);

        GetViewport().SizeChanged += OnViewportResized;
        SetProcessUnhandledInput(true);
    }

    public override void _Draw()
    {
        if (_tile is null)
            return;

        var drawStart = Stopwatch.GetTimestamp();
        var scale = _pixelsPerRadian * _zoom;
        var fills = _activeFills.Length == _corners.Length ? _activeFills : _biomeFills;

        var borderIndex = 0;
        for (var i = 0; i < _corners.Length; i++)
        {
            var corners = _corners[i];
            var screen = ScreenBuffer(corners.Length);

            for (var v = 0; v < corners.Length; v++)
                screen[v] = ToScreen(corners[v], scale);

            for (var v = 0; v < corners.Length; v++)
            {
                _borderPoints[borderIndex++] = screen[v];
                _borderPoints[borderIndex++] = screen[(v + 1) % corners.Length];
            }
        }

        if (_fillMeshDirty || _fillMesh is null)
            BuildFillMesh(fills);

        var meshTransform = new Transform2D(
            new Vector2(scale, 0f),
            new Vector2(0f, -scale),
            _center + _pan);

        DrawMesh(_fillMesh, _whiteTexture, meshTransform);
        DrawMultiline(_borderPoints, BorderColor, 1f, false);

        // The heatmap already encodes crater clearance; dots would fight it.
        if (ViewMode != RegionViewMode.SiteScore)
        {
            for (var i = 0; i < _corners.Length; i++)
            {
                if (_craters[i] == CraterKind.None)
                    continue;

                var position = ToScreen(_centers[i], scale);
                var radius = Mathf.Max(1.5f, _cellRadius * scale * 0.28f);
                var color = _craters[i] == CraterKind.MultiCell ? MultiCraterColor : SingleCraterColor;
                DrawCircle(position, radius, color);
            }
        }

        if (ViewMode == RegionViewMode.Buildings)
            DrawBuildings(scale);

        DrawGhost(scale);

        if (ViewMode != RegionViewMode.Buildings && _routeFrom >= 0 && _route is null)
            DrawMarker(_routeFrom, scale, new Color(0.55f, 0.85f, 1f, 0.95f));

        if (ViewMode != RegionViewMode.Buildings && _route is not null && _route.LegCount > 0)
            DrawRoute(_route, scale);

        if (_hovered >= 0)
        {
            var screen = ScreenBuffer(_corners[_hovered].Length);
            for (var v = 0; v < screen.Length; v++)
                screen[v] = ToScreen(_corners[_hovered][v], scale);

            DrawPolyline(Close(screen), HoverColor, 2f, true);
        }

        if (_selected >= 0 && _selected != _hovered)
        {
            var screen = ScreenBuffer(_corners[_selected].Length);
            for (var v = 0; v < screen.Length; v++)
                screen[v] = ToScreen(_corners[_selected][v], scale);

            DrawPolyline(Close(screen), new Color(0.55f, 0.85f, 1f, 0.95f), 2f, true);
        }

        if (ViewMode == RegionViewMode.Buildings && _hovered >= 0 && _activeBuilding is not null)
            DrawPlacementLabel(scale);

        LastDrawMs = (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency;
        _drawTotalMs += LastDrawMs;
        _drawCount++;
    }

    private void BuildFillMesh(Color[] fills)
    {
        var vertexCount = 0;
        for (var i = 0; i < _corners.Length; i++)
            vertexCount += _corners[i].Length * 3;

        var vertices = new Vector2[vertexCount];
        var colors = new Color[vertexCount];
        var index = 0;

        for (var i = 0; i < _corners.Length; i++)
        {
            var center = _centers[i];
            var corners = _corners[i];
            var color = fills[i];

            for (var v = 0; v < corners.Length; v++)
            {
                vertices[index] = center;
                colors[index++] = color;

                vertices[index] = corners[v];
                colors[index++] = color;

                vertices[index] = corners[(v + 1) % corners.Length];
                colors[index++] = color;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        _fillMesh = new ArrayMesh();
        _fillMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _fillMeshDirty = false;
    }

    private void DrawBuildings(float scale)
    {
        foreach (var building in _buildings)
        {
            var color = BuildingColor(building.DefId);
            var fill = new Color(color.R, color.G, color.B, 0.55f);

            foreach (var hex in building.Footprint)
            {
                if (!_ordinalByIndex.TryGetValue((ulong)hex, out var ordinal))
                    continue;

                var corners = _corners[ordinal];
                var screen = ScreenBuffer(corners.Length);
                for (var v = 0; v < corners.Length; v++)
                    screen[v] = ToScreen(corners[v], scale);

                DrawColoredPolygon(screen, fill);
                DrawPolyline(Close(screen), color, 1.5f, true);
            }
        }
    }

    private void DrawGhost(float scale)
    {
        if (ViewMode != RegionViewMode.Buildings || _ghostOrdinals.Length == 0)
            return;

        var color = _ghostValid ? new Color(0.35f, 1f, 0.55f) : new Color(1f, 0.35f, 0.3f);
        var fill = new Color(color.R, color.G, color.B, 0.32f);

        foreach (var ordinal in _ghostOrdinals)
        {
            var corners = _corners[ordinal];
            var screen = ScreenBuffer(corners.Length);
            for (var v = 0; v < corners.Length; v++)
                screen[v] = ToScreen(corners[v], scale);

            DrawColoredPolygon(screen, fill);
            DrawPolyline(Close(screen), color, 2f, true);
        }
    }

    private void DrawPlacementLabel(float scale)
    {
        var anchor = ToScreen(_centers[_hovered], scale) + new Vector2(14, 6);
        var text = _ghostOrdinals.Length == 0
            ? _activeBuilding!.DisplayName
            : _ghostValid
                ? $"{_activeBuilding!.DisplayName} · place"
                : _ghostError;

        if (text.Length > 64)
            text = text[..61] + "...";

        var color = _ghostValid
            ? new Color(0.65f, 1f, 0.72f)
            : new Color(1f, 0.68f, 0.62f);

        DrawString(_font, anchor, text, HorizontalAlignment.Left, -1, 11, color);
    }


    private void DrawMarker(int ordinal, float scale, Color color)
    {
        var screen = ScreenBuffer(_corners[ordinal].Length);
        for (var v = 0; v < screen.Length; v++)
            screen[v] = ToScreen(_corners[ordinal][v], scale);

        DrawPolyline(Close(screen), color, 2.5f, true);
    }

    private void DrawRoute(RoutePlan route, float scale)
    {
        var points = new Vector2[route.LegCount + 1];
        points[0] = ToScreen(_centers[route.Start], scale);

        for (var i = 0; i < route.LegCount; i++)
            points[i + 1] = ToScreen(_centers[route.Legs[i].To], scale);

        DrawPolyline(points, new Color(0.35f, 0.85f, 1f, 0.95f), 3f, true);
        DrawCircle(points[0], 5f, new Color(0.55f, 0.85f, 1f));
        DrawCircle(points[^1], 5f, new Color(1f, 0.6f, 0.35f));
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible)
            return;

        switch (@event)
        {
            case InputEventMouseButton button:
                HandleMouseButton(button);
                break;

            case InputEventMouseMotion motion:
                HandleMouseMotion(motion);
                break;

            case InputEventKey { Pressed: true, Echo: false } key
                when (key.Keycode is Key.Delete or Key.Backspace) && ViewMode == RegionViewMode.Buildings:
                RemoveHoveredBuilding();
                break;
        }
    }

    private void HandleMouseButton(InputEventMouseButton button)
    {
        switch (button.ButtonIndex)
        {
            case MouseButton.WheelUp when button.Pressed:
                ZoomBy(1.12f, button.Position);
                break;

            case MouseButton.WheelDown when button.Pressed:
                ZoomBy(1f / 1.12f, button.Position);
                break;

            case MouseButton.Left:
                if (button.Pressed)
                {
                    _dragging = true;
                    _dragStart = button.Position;
                    _lastMouse = button.Position;
                }
                else if (_dragging)
                {
                    _dragging = false;
                    if (button.Position.DistanceTo(_dragStart) <= DragThresholdPixels)
                        SelectHovered();
                }

                break;

            case MouseButton.Right:
                _panning = button.Pressed;
                _lastMouse = button.Position;
                break;
        }
    }

    private void HandleMouseMotion(InputEventMouseMotion motion)
    {
        if (_panning)
        {
            _pan += motion.Position - _lastMouse;
            _lastMouse = motion.Position;
            QueueRedraw();
            return;
        }

        if (_dragging)
        {
            _pan += motion.Position - _lastMouse;
            _lastMouse = motion.Position;
            _hovered = -1;
            UpdateGhost();
            QueueRedraw();
            return;
        }

        UpdateHover(motion.Position);
    }

    private void ZoomBy(float factor, Vector2 anchor)
    {
        var before = _zoom;
        _zoom = Mathf.Clamp(_zoom * factor, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(before, _zoom))
            return;

        var origin = _center + _pan;
        _pan += (anchor - origin) * (1f - _zoom / before);
        QueueRedraw();
    }

    private void UpdateHover(Vector2 mouse)
    {
        var hit = Pick(mouse);
        if (hit == _hovered)
            return;

        _hovered = hit;
        UpdateGhost();
        QueueRedraw();

        if (hit < 0)
        {
            EmitSignal(SignalName.HexHovered, -1, string.Empty, 0, 0);
            return;
        }

        EmitSignal(SignalName.HexHovered, hit, _biomeIds[hit], (int)_elevations[hit], (int)_craters[hit]);
    }

    private void SelectHovered()
    {
        if (_hovered < 0)
        {
            _selected = -1;
            ClearRoute();
            UpdateGhost();
            QueueRedraw();
            return;
        }

        _selected = _hovered;
        EmitSignal(SignalName.HexSelected, _selected, _biomeIds[_selected],
            (int)_elevations[_selected], (int)_craters[_selected]);

        if (ViewMode == RegionViewMode.Buildings)
        {
            PlaceActiveBuilding();
            QueueRedraw();
            return;
        }

        if (_routeFrom < 0 || _route is not null)
        {
            // First click, or a new pair: this hex becomes the route start.
            _routeFrom = _hovered;
            ClearRoute();
        }
        else if (_hovered != _routeFrom)
        {
            var plan = RoutePlanner.Plan(
                _routeFrom, _hovered, _neighbours, BiomeAt, CraterAt, DistanceKm, BaseSpeedKmh);

            if (plan is not null)
            {
                _route = plan;
                EmitSignal(SignalName.RouteComputed, plan.LegCount, plan.TotalDistanceKm, plan.TotalHours);
            }
            else
            {
                ClearRoute();
            }
        }

        QueueRedraw();
    }

    private void ClearRoute()
    {
        if (_route is null)
            return;

        _route = null;
        EmitSignal(SignalName.RouteCleared);
    }

    /// <summary>Sets the building catalogue used for placement and cycling.</summary>
    public void SetBuildingRegistry(BuildingRegistry registry)
    {
        _registry = registry;
        _buildingChoices = registry.All.Values
            .OrderBy(def => def.Category)
            .ThenBy(def => def.Id, StringComparer.Ordinal)
            .ToList();

        _activeBuilding = _buildingChoices.FirstOrDefault();
        if (_activeBuilding is not null)
            EmitSignal(SignalName.ActiveBuildingChanged, _activeBuilding.Id);

        UpdateGhost();
        QueueRedraw();
    }

    /// <summary>Switches the colour overlay. Site scores are computed on first use.</summary>
    public void SetViewMode(RegionViewMode mode)
    {
        if (ViewMode == mode)
            return;

        ViewMode = mode;

        if (mode == RegionViewMode.SiteScore)
            EnsureSiteScores();

        UpdateActiveFills();
        UpdateGhost();
        EmitSignal(SignalName.ViewModeChanged, (int)mode);
        QueueRedraw();
    }

    /// <summary>Advances to the next overlay mode (wraps).</summary>
    public void CycleViewMode()
        => SetViewMode((RegionViewMode)(((int)ViewMode + 1) % 4));

    /// <summary>Selects the previous/next building definition.</summary>
    public void CycleActiveBuilding(int direction)
    {
        if (_buildingChoices.Count == 0)
            return;

        var index = _activeBuilding is null
            ? 0
            : _buildingChoices.FindIndex(def => def.Id == _activeBuilding.Id);
        if (index < 0)
            index = 0;

        index = ((index + direction) % _buildingChoices.Count + _buildingChoices.Count) % _buildingChoices.Count;
        _activeBuilding = _buildingChoices[index];

        EmitSignal(SignalName.ActiveBuildingChanged, _activeBuilding.Id);
        UpdateGhost();
        QueueRedraw();
    }

    /// <summary>Selects a building definition by id.</summary>
    public bool SetActiveBuilding(string defId)
    {
        var def = _buildingChoices.FirstOrDefault(candidate => candidate.Id == defId);
        if (def is null)
            return false;

        _activeBuilding = def;
        EmitSignal(SignalName.ActiveBuildingChanged, _activeBuilding.Id);
        UpdateGhost();
        QueueRedraw();
        return true;
    }

    /// <summary>Places the active building on the hovered hex.</summary>
    public bool PlaceActiveBuilding() => _hovered >= 0 && PlaceAt(_hovered);

    /// <summary>Dev/demo: places the active building on a specific hex ordinal.</summary>
    public bool PlaceActiveBuildingAt(int ordinal)
    {
        if (_tile is null || (uint)ordinal >= (uint)_tile.HexCount)
            return false;

        _hovered = ordinal;
        _selected = ordinal;
        return PlaceAt(ordinal);
    }

    private bool PlaceAt(int ordinal)
    {
        if (_tile is null || _activeBuilding is null)
            return false;

        var result = _activeBuilding.CanPlaceAt(_tile, ordinal, _world, SiteConfig, _occupancy);
        if (!result.Success)
        {
            _ghostValid = false;
            _ghostError = result.Error;
            EmitSignal(SignalName.PlacementFeedback, false, result.Error);
            QueueRedraw();
            return false;
        }

        _buildings.Add(new BuildingInstance
        {
            DefId = _activeBuilding.Id,
            CenterHex = _tile.Cells[ordinal],
            Footprint = result.Footprint,
        });

        _occupancy = new Dictionary<ulong, BuildingInstance>(BuildingRegistry.BuildOccupancy(_buildings));
        UpdateGhost();

        var message = $"Placed {_activeBuilding.DisplayName}";
        if (result.Warnings.Count > 0)
            message += $" · {string.Join(" · ", result.Warnings)}";

        EmitSignal(SignalName.BuildingsChanged, _buildings.Count);
        EmitSignal(SignalName.PlacementFeedback, true, message);
        QueueRedraw();
        return true;
    }

    /// <summary>Removes the building occupying the hovered hex, if any.</summary>
    public bool RemoveHoveredBuilding()
    {
        if (_tile is null || _hovered < 0)
            return false;

        if (!_occupancy.TryGetValue((ulong)_tile.Cells[_hovered], out var building))
        {
            EmitSignal(SignalName.PlacementFeedback, false, "No building on this hex");
            return false;
        }

        _buildings.Remove(building);
        _occupancy = new Dictionary<ulong, BuildingInstance>(BuildingRegistry.BuildOccupancy(_buildings));
        UpdateGhost();

        EmitSignal(SignalName.BuildingsChanged, _buildings.Count);
        EmitSignal(SignalName.PlacementFeedback, true, $"Removed {building.DefId}");
        QueueRedraw();
        return true;
    }

    /// <summary>Removes every building in the current region.</summary>
    public void ClearBuildings()
    {
        if (_buildings.Count == 0)
        {
            EmitSignal(SignalName.PlacementFeedback, false, "No buildings to clear");
            return;
        }

        var removed = _buildings.Count;
        _buildings.Clear();
        _occupancy.Clear();
        UpdateGhost();

        EmitSignal(SignalName.BuildingsChanged, 0);
        EmitSignal(SignalName.PlacementFeedback, true, $"Cleared {removed} building(s)");
        QueueRedraw();
    }

    private void EnsureSiteScores()
    {
        if (_tile is null || _siteScores is not null)
            return;

        var count = _tile.HexCount;
        var scores = new double[count];
        var buildable = new bool[count];

        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var score = _tile.ScoreSite(i, _world, SiteConfig);
            scores[i] = score.CompositeWith(SiteConfig.Weights);
            buildable[i] = score.Factors.Buildable;
        }

        stopwatch.Stop();
        _siteScoreMs = stopwatch.Elapsed.TotalMilliseconds;

        var min = double.MaxValue;
        var max = double.MinValue;
        for (var i = 0; i < count; i++)
        {
            if (!buildable[i])
                continue;

            if (scores[i] < min) min = scores[i];
            if (scores[i] > max) max = scores[i];
        }

        if (min > max)
        {
            min = 0.0;
            max = 1.0;
        }
        else if (max - min < 1e-6)
        {
            min = Math.Max(0.0, min - 0.05);
            max = Math.Min(1.0, max + 0.05);
        }

        _siteScoreMin = min;
        _siteScoreMax = max;

        var span = Math.Max(1e-6, max - min);
        var fills = new Color[count];
        for (var i = 0; i < count; i++)
        {
            var t = Math.Clamp((scores[i] - min) / span, 0.0, 1.0);
            fills[i] = SiteScorePalette.For(t, buildable[i]);
        }

        _siteScores = scores;
        _siteFills = fills;

        GD.Print($"site scores: {count} hexes in {_siteScoreMs:F1} ms " +
                 $"(range {_siteScoreMin:P0} – {_siteScoreMax:P0})");
    }

    private void UpdateActiveFills()
    {
        _activeFills = ViewMode switch
        {
            RegionViewMode.Terrain => _terrainFills,
            RegionViewMode.SiteScore => _siteFills,
            RegionViewMode.Buildings => _buildingFills,
            _ => _biomeFills,
        };

        _fillMeshDirty = true;
    }

    private void UpdateGhost()
    {
        _ghostValid = false;
        _ghostError = string.Empty;
        _ghostOrdinals = Array.Empty<int>();

        if (ViewMode != RegionViewMode.Buildings || _activeBuilding is null || _tile is null || _hovered < 0)
            return;

        var result = _activeBuilding.CanPlaceAt(_tile, _hovered, _world, SiteConfig, _occupancy);
        _ghostValid = result.Success;
        _ghostError = result.Success ? string.Empty : result.Error;

        var footprint = result.Success
            ? result.Footprint
            : _activeBuilding.BuildFootprint(_tile, _hovered);

        var ordinals = new List<int>(footprint.Count);
        foreach (var hex in footprint)
        {
            if (_ordinalByIndex.TryGetValue((ulong)hex, out var ordinal))
                ordinals.Add(ordinal);
        }

        _ghostOrdinals = ordinals.ToArray();
    }

    private Color BuildingColor(string defId)
    {
        if (_buildingColorCache.TryGetValue(defId, out var cached))
            return cached;

        var color = new Color(0.85f, 0.85f, 0.92f, 0.85f);
        if (_registry is not null && _registry.TryGet(defId, out var def) && !string.IsNullOrWhiteSpace(def!.FootprintColor))
            color = Color.FromHtml(def.FootprintColor);

        _buildingColorCache[defId] = color;
        return color;
    }

    /// <summary>Dev/demo: plans a route between two hex ordinals without clicking.</summary>
    public bool ShowRoute(int from, int to)
    {
        if (_tile is null || (uint)from >= (uint)_tile.HexCount || (uint)to >= (uint)_tile.HexCount)
            return false;

        _routeFrom = from;
        _selected = to;

        var plan = RoutePlanner.Plan(from, to, _neighbours, BiomeAt, CraterAt, DistanceKm, BaseSpeedKmh);
        if (plan is null)
        {
            ClearRoute();
            QueueRedraw();
            return false;
        }

        _route = plan;
        EmitSignal(SignalName.RouteComputed, plan.LegCount, plan.TotalDistanceKm, plan.TotalHours);
        QueueRedraw();
        return true;
    }

    private void BuildAdjacency(IReadOnlyList<H3Index> cells)
    {
        _ordinalByIndex.Clear();
        _neighbours = new int[cells.Count][];

        for (var i = 0; i < cells.Count; i++)
            _ordinalByIndex[(ulong)cells[i]] = i;

        for (var i = 0; i < cells.Count; i++)
        {
            var adjacent = new List<int>(6);

            foreach (var neighbour in cells[i].GridDiskDistances(1))
            {
                if (neighbour.Distance == 1 && _ordinalByIndex.TryGetValue((ulong)neighbour.Index, out var ordinal))
                    adjacent.Add(ordinal);
            }

            _neighbours[i] = adjacent.ToArray();
        }
    }

    private Biome BiomeAt(int ordinal) => _world.Biomes[_tile[ordinal].BiomeCode];

    private CraterKind CraterAt(int ordinal) => _craters[ordinal];

    private double DistanceKm(int from, int to)
        => _unitCenters[from].AngleTo(_unitCenters[to]) * _radiusKm;

    private int Pick(Vector2 mouse)
    {
        if (_centers.Length == 0)
            return -1;

        var scale = _pixelsPerRadian * _zoom;
        var maxDistance = Mathf.Max(6f, _cellRadius * scale * 1.1f);
        var maxDistanceSquared = maxDistance * maxDistance;

        var best = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _centers.Length; i++)
        {
            var distance = ToScreen(_centers[i], scale).DistanceSquaredTo(mouse);
            if (distance < bestDistance && distance <= maxDistanceSquared)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private Vector2 ToScreen(Vector2 plane, float scale)
        => _center + _pan + new Vector2(plane.X * scale, -plane.Y * scale);

    private Vector2[] ScreenBuffer(int length)
    {
        if (!_drawBuffers.TryGetValue(length, out var buffer))
        {
            buffer = new Vector2[length];
            _drawBuffers[length] = buffer;
        }

        return buffer;
    }

    private Vector2[] Close(Vector2[] polygon)
    {
        if (!_closedBuffers.TryGetValue(polygon.Length, out var closed))
        {
            closed = new Vector2[polygon.Length + 1];
            _closedBuffers[polygon.Length] = closed;
        }

        polygon.CopyTo(closed, 0);
        closed[^1] = polygon[0];
        return closed;
    }

    private float EstimateCellRadius()
    {
        var sum = 0f;
        var count = 0;

        for (var i = 0; i < _centers.Length; i++)
        {
            foreach (var corner in _corners[i])
            {
                sum += _centers[i].DistanceTo(corner);
                count++;
            }
        }

        return count > 0 ? sum / count : 0.001f;
    }

    private void OnViewportResized()
    {
        _center = GetViewportRect().Size * 0.5f;
        QueueRedraw();
    }

    private static Vector2 Project(Vector3 v, Vector3 center, Vector3 east, Vector3 north)
    {
        // Gnomonic: divide by the cosine of the angle from the region centre.
        var cos = v.Dot(center);
        if (cos < 0.2f)
            cos = 0.2f;

        return new Vector2(v.Dot(east) / cos, v.Dot(north) / cos);
    }

    private static (Vector3 East, Vector3 North) TangentBasis(Vector3 center)
    {
        // East = normalise(cross(polarAxis, center)); north completes the frame.
        var east = new Vector3(-center.Z, 0f, center.X);
        if (east.LengthSquared() < 1e-8f)
            east = new Vector3(1f, 0f, 0f);

        east = east.Normalized();
        var north = east.Cross(center).Normalized();
        return (east, north);
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
