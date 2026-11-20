using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Godot;
using H3;
using H3.Extensions;
using Sim.Biomes;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Hex;
using Sim.Site;
using Sim.World;

/// <summary>Projection used to present the seven-tile area mosaic.</summary>
public enum AreaProjectionMode
{
    /// <summary>Original flat tangent-plane map.</summary>
    Flat,

    /// <summary>Isometric faceted diorama, like the colony view but area-wide.</summary>
    Isometric,
}

/// <summary>
/// Multi-region operations view. It shows the active region and its immediate H3
/// neighbours, with rover tracks and location markers. The same seven tiles can
/// be presented as a flat tangent-plane map or as one isometric faceted diorama,
/// and both projections share the five region overlays.
///
/// This is the M5 AreaView decision: cross-region logistics needs a scale between
/// the globe and one region, but it deliberately loads only seven region tiles.
/// Hovering draws a subdued shade around the whole region without hiding the terrain grid.
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

    /// <summary>Fraction of the area span the full elevation range should rise.</summary>
    private const float ReliefFraction = 0.18f;

    /// <summary>
    /// Presentation-only vertical contrast curve. Smoothstep keeps the endpoints
    /// and ordering of the baked elevations while making broad, nearly planar
    /// ramps readable as slope facets at area scale.
    /// </summary>
    internal static float ReliefCurve(float normalizedElevation)
    {
        var t = Mathf.Clamp(normalizedElevation, 0f, 1f);
        return t * t * (3f - 2f * t);
    }

    private static readonly Color BorderColor = new(0f, 0f, 0f, 0.28f);
    private static readonly Color HoverShade = new(0.06f, 0.08f, 0.12f, 0.42f);
    private static readonly Color RouteColor = new(0.35f, 0.85f, 1f, 0.95f);
    private static readonly Color RoverColor = new(1f, 0.62f, 0.28f, 1f);
    private static readonly Color SelectedColor = new(0.35f, 1f, 0.62f, 1f);
    private static readonly Vector2 LightDirection = new Vector2(-0.45f, 0.55f).Normalized();

    private static string CornerKey(Vector2 plane)
        => $"{plane.X.ToString("F6", CultureInfo.InvariantCulture)}:" +
           $"{plane.Y.ToString("F6", CultureInfo.InvariantCulture)}";

    /// <summary>Flat-shades one facet from its plane normal and the fixed light.</summary>
    internal static Color FacetShade(
        Color fill, Vector2 center, Vector2 a, Vector2 b,
        float centerHeight, float aHeight, float bHeight)
    {
        var ab = new Vector3(a.X - center.X, a.Y - center.Y, aHeight - centerHeight);
        var ac = new Vector3(b.X - center.X, b.Y - center.Y, bHeight - centerHeight);
        var normal = ab.Cross(ac);
        if (normal.Z < 0f)
            normal = -normal;

        if (normal.LengthSquared() < 1e-12f)
            return fill;

        var light = new Vector3(LightDirection.X, LightDirection.Y, 1.6f).Normalized();
        var diffuse = Mathf.Clamp(normal.Normalized().Dot(light), 0f, 1f);
        var factor = 0.70f + 0.34f * diffuse;
        return new Color(
            Mathf.Min(1f, fill.R * factor),
            Mathf.Min(1f, fill.G * factor),
            Mathf.Min(1f, fill.B * factor),
            fill.A);
    }

    private sealed class RegionArea
    {
        public required ulong Index { get; init; }

        public required Vector2 Center { get; init; }

        public required IReadOnlyList<int> Cells { get; init; }

        public required int CenterHex { get; init; }

        /// <summary>Global cell index of <see cref="CenterHex"/> in the mosaic.</summary>
        public required int CenterCell { get; init; }
    }

    private BodyWorld _world = null!;
    private CampaignState _campaign = null!;
    private SurfaceImagery? _imagery;
    private SiteScorerConfig _siteConfig = new() { BodyRadiusKm = 1737.4 };
    private double _radiusKm = 1737.4;

    private readonly List<RegionArea> _areas = new();
    private readonly Dictionary<ulong, RegionArea> _areaByIndex = new();
    private readonly Dictionary<(ulong Region, ulong Hex), int> _cellByKey = new();
    private readonly Dictionary<ulong, Vector2[]> _regionBorders = new();
    private readonly Dictionary<ulong, Vector2[]> _screenRegionBorders = new();
    private readonly Dictionary<ulong, Vector2[]> _isoScreenRegionBorders = new();

    private Vector2[] _centers = Array.Empty<Vector2>();
    private Vector2[][] _corners = Array.Empty<Vector2[]>();
    private float[] _elevations = Array.Empty<float>();
    private float[] _heights = Array.Empty<float>();
    private string[][] _cornerKeys = Array.Empty<string[]>();
    private float[][] _cornerHeights = Array.Empty<float[]>();
    private RegionTile[] _tiles = Array.Empty<RegionTile>();
    private int[] _ordinals = Array.Empty<int>();
    private readonly List<string> _biomesPresent = new();

    private Color[] _biomeFills = Array.Empty<Color>();
    private Color[] _terrainFills = Array.Empty<Color>();
    private Color[] _siteFills = Array.Empty<Color>();
    private Color[] _buildingFills = Array.Empty<Color>();
    private Color[] _imageryFills = Array.Empty<Color>();
    private Color[] _fills = Array.Empty<Color>();
    private double[]? _siteScores;
    private double _siteScoreMs;
    private double _siteScoreMin;
    private double _siteScoreMax = 1.0;

    private Vector2[] _borders = Array.Empty<Vector2>();
    private Vector2[] _screenBorders = Array.Empty<Vector2>();
    private Vector2[] _isoScreenBorders = Array.Empty<Vector2>();

    private ArrayMesh? _fillMesh;
    private AreaDiorama? _diorama;
    private ImageTexture _whiteTexture = null!;
    private Font _font = null!;

    // Flat camera.
    private Vector2 _origin;
    private Vector2 _span = Vector2.One;
    private float _unitsPerRadian = 1f;
    private float _flatZoom = 1f;
    private Vector2 _flatPan;

    // Isometric camera, kept separate so toggling G never loses either view.
    private float _isoPixelsPerRadian = 1f;
    private float _isoZoom = 1f;
    private Vector2 _isoCenter;
    private Vector2 _isoPan;

    private Vector2 _dragStart;
    private bool _screenDirty = true;

    private ulong _centerRegion;
    private int _hoverRegionIndex = -1;
    private string? _hoverRoverId;
    private RegionViewMode _overlay = RegionViewMode.Biome;
    private AreaProjectionMode _projection = AreaProjectionMode.Flat;

    private float Zoom
    {
        get => UsesIsometric ? _isoZoom : _flatZoom;
        set
        {
            if (UsesIsometric) _isoZoom = value;
            else _flatZoom = value;
        }
    }

    private Vector2 Pan
    {
        get => UsesIsometric ? _isoPan : _flatPan;
        set
        {
            if (UsesIsometric) _isoPan = value;
            else _flatPan = value;
        }
    }

    private float CurrentScale => UsesIsometric
        ? _isoPixelsPerRadian * _isoZoom
        : _unitsPerRadian * _flatZoom;

    /// <summary>Currently centred region.</summary>
    public ulong CenterRegion => _centerRegion;

    /// <summary>Current colour/relief overlay.</summary>
    public RegionViewMode Overlay => _overlay;

    /// <summary>Current flat or isometric presentation.</summary>
    public AreaProjectionMode Projection => _projection;

    /// <summary>True when the seven tiles are drawn as an isometric diorama.</summary>
    public bool UsesIsometric => _projection == AreaProjectionMode.Isometric;

    /// <summary>True when faceted terrain-slope shading is active.</summary>
    public bool UsesSlope => _overlay == RegionViewMode.Terrain;

    /// <summary>Number of loaded region tiles (normally seven).</summary>
    public int RegionCount => _areas.Count;

    /// <summary>Number of fine hexes across all loaded tiles.</summary>
    public int HexCount => _centers.Length;

    /// <summary>Distinct biome ids present in the area, in first-seen order.</summary>
    public IReadOnlyList<string> BiomesPresent => _biomesPresent;

    public short TerrainMinM { get; private set; }

    public short TerrainMaxM { get; private set; }

    public double SiteScoreMin => _siteScoreMin;

    public double SiteScoreMax => _siteScoreMax;

    public double SiteScoreMs => _siteScoreMs;

    /// <summary>True when every cell has a true-colour sidecar sample.</summary>
    public bool HasImagery => _imagery is not null && _imageryFills.Length == _centers.Length && _centers.Length > 0;

    /// <summary>Installations across all visible regions.</summary>
    public int BuildingCount => _areas.Sum(area => _campaign.BuildingCount(area.Index));

    /// <summary>Current diorama orbit angle, or 0 in the flat projection.</summary>
    public int CameraDegrees => _diorama?.CameraDegrees ?? 0;

    /// <summary>Current diorama relief multiplier, or 1 in the flat projection.</summary>
    public float ReliefScale => _diorama?.ReliefScale ?? 1f;

    /// <summary>Binds the optional surface-colour sidecar before an area is loaded.</summary>
    public void SetImagery(SurfaceImagery? imagery) => _imagery = imagery;

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
        _radiusKm = world.Manifest.Body.RadiusKm;
        _siteConfig = campaign.SiteConfig with { BodyRadiusKm = _radiusKm };

        _areas.Clear();
        _areaByIndex.Clear();
        _cellByKey.Clear();
        _regionBorders.Clear();
        _screenRegionBorders.Clear();
        _isoScreenRegionBorders.Clear();
        _biomesPresent.Clear();
        _siteScores = null;
        _siteScoreMs = 0;

        var regionIndices = new List<ulong> { centerRegion };
        regionIndices.AddRange(HexGrid.Neighbors(new H3Index(centerRegion))
            .Select(neighbour => (ulong)neighbour)
            .Where(index => world.TryGetRegionEntry(index, out _)));

        var center = new H3Index(centerRegion).ToLatLng();
        var regionCenter = ToUnitVector(center);
        var (east, north) = TangentBasis(regionCenter);

        var totalCells = regionIndices.Sum(index => world.GetRegion(index).HexCount);
        var centers = new List<Vector2>(totalCells);
        var corners = new List<Vector2[]>(totalCells);
        var biomeFills = new List<Color>(totalCells);
        var elevations = new List<float>(totalCells);
        var cornerKeys = new List<string[]>(totalCells);
        var tiles = new List<RegionTile>(totalCells);
        var ordinals = new List<int>(totalCells);
        var imageryFills = new Color[totalCells];
        var imageryComplete = _imagery is not null;
        var borderPoints = new List<Vector2>();
        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        var minElevation = float.MaxValue;
        var maxElevation = float.MinValue;

        foreach (var regionIndex in regionIndices)
        {
            var tile = world.GetRegion(regionIndex);
            var regionUnit = ToUnitVector(new H3Index(regionIndex).ToLatLng());
            var cells = new List<int>();
            var regionBorder = new List<Vector2>();
            var centerHex = 0;
            var centerCell = 0;
            var bestDot = -2f;
            var regionImagery = new Color[tile.HexCount];
            if (_imagery is null || !_imagery.TryCopyRegion(regionIndex, tile.HexCount, regionImagery))
                imageryComplete = false;

            for (var ordinal = 0; ordinal < tile.HexCount; ordinal++)
            {
                var cell = tile.Cells[ordinal];
                var cellCenter = ToUnitVector(cell.ToLatLng());

                var dot = cellCenter.Dot(regionUnit);
                if (dot > bestDot)
                {
                    bestDot = dot;
                    centerHex = ordinal;
                    centerCell = centers.Count;
                }

                var boundary = cell.GetCellBoundaryVertices().ToList();
                var polygon = new Vector2[boundary.Count];
                var keys = new string[boundary.Count];
                for (var v = 0; v < boundary.Count; v++)
                {
                    polygon[v] = Project(ToUnitVector(boundary[v]), east, north);
                    keys[v] = CornerKey(polygon[v]);
                    min = new Vector2(Mathf.Min(min.X, polygon[v].X), Mathf.Min(min.Y, polygon[v].Y));
                    max = new Vector2(Mathf.Max(max.X, polygon[v].X), Mathf.Max(max.Y, polygon[v].Y));
                }

                var globalIndex = centers.Count;
                centers.Add(Project(cellCenter, east, north));
                corners.Add(polygon);
                cornerKeys.Add(keys);
                _cellByKey[(regionIndex, (ulong)cell)] = globalIndex;
                cells.Add(globalIndex);
                tiles.Add(tile);
                ordinals.Add(ordinal);
                imageryFills[globalIndex] = regionImagery[ordinal];

                var biome = world.Biomes[tile.BiomeCode(ordinal)];
                var color = BiomePalette.For(biome.Id);
                if (regionIndex != centerRegion)
                    color = new Color(color.R * 0.72f, color.G * 0.72f, color.B * 0.72f, 1f);
                biomeFills.Add(color);
                if (!_biomesPresent.Contains(biome.Id))
                    _biomesPresent.Add(biome.Id);

                var elevation = tile.ElevationM(ordinal);
                elevations.Add(elevation);
                minElevation = Mathf.Min(minElevation, elevation);
                maxElevation = Mathf.Max(maxElevation, elevation);

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
                CenterCell = centerCell,
            };
            _areaByIndex[regionIndex] = area;
            _areas.Add(area);
            _regionBorders[regionIndex] = regionBorder.ToArray();
        }

        _centers = centers.ToArray();
        _corners = corners.ToArray();
        _biomeFills = biomeFills.ToArray();
        _imageryFills = imageryComplete ? imageryFills : Array.Empty<Color>();
        _cornerKeys = cornerKeys.ToArray();
        _borders = borderPoints.ToArray();
        _tiles = tiles.ToArray();
        _ordinals = ordinals.ToArray();

        _elevations = elevations.ToArray();
        TerrainMinM = (short)minElevation;
        TerrainMaxM = (short)maxElevation;
        var elevationSpan = Mathf.Max(1f, maxElevation - minElevation);
        _terrainFills = elevations
            .Select(elevation => TerrainPalette.For((elevation - minElevation) / elevationSpan))
            .ToArray();
        _siteFills = Array.Empty<Color>();
        _siteScoreMin = 0.0;
        _siteScoreMax = 1.0;
        _buildingFills = _biomeFills
            .Select(fill => new Color(fill.R * 0.32f, fill.G * 0.32f, fill.B * 0.32f, 1f))
            .ToArray();

        var radiusMetres = Math.Max(1.0, world.Manifest.Body.RadiusKm * 1000.0);
        var spanPlane = Mathf.Max(max.X - min.X, max.Y - min.Y);
        var elevationSpanPlane = (maxElevation - minElevation) / radiusMetres;
        var relief = elevationSpanPlane > 1e-9
            ? ReliefFraction * spanPlane / elevationSpanPlane
            : 0f;

        _heights = new float[_elevations.Length];
        var cornerSums = new Dictionary<string, (double Sum, int Count)>(StringComparer.Ordinal);
        var heightScale = (float)(elevationSpan / radiusMetres * relief);
        for (var i = 0; i < _elevations.Length; i++)
        {
            var normalized = (_elevations[i] - minElevation) / elevationSpan;
            _heights[i] = ReliefCurve(normalized) * heightScale;
            foreach (var vertex in _cornerKeys[i])
            {
                cornerSums.TryGetValue(vertex, out var entry);
                cornerSums[vertex] = (entry.Sum + _heights[i], entry.Count + 1);
            }
        }

        _cornerHeights = new float[_centers.Length][];
        for (var i = 0; i < _centers.Length; i++)
        {
            var heights = new float[_cornerKeys[i].Length];
            for (var v = 0; v < heights.Length; v++)
            {
                heights[v] = cornerSums.TryGetValue(_cornerKeys[i][v], out var entry) && entry.Count > 0
                    ? (float)(entry.Sum / entry.Count)
                    : _heights[i];
            }

            _cornerHeights[i] = heights;
        }

        GD.Print($"area: {_areas.Count} regions · {_centers.Length} hexes · " +
                 $"elev {minElevation:F0}..{maxElevation:F0} m · relief {relief:F2}");

        _origin = -((min + max) * 0.5f);
        _span = max - min;
        FitFlat();

        if (_overlay == RegionViewMode.Imagery && !HasImagery)
            _overlay = RegionViewMode.Biome;
        if (_overlay == RegionViewMode.SiteScore)
            EnsureSiteScores();
        UpdateFills();

        _diorama = new AreaDiorama(_centers, _corners, _cornerKeys, _elevations,
            minElevation, maxElevation, radiusMetres, _fills);
        FitIso();

        _hoverRegionIndex = -1;
        _hoverRoverId = null;
        SelectedRoverId = null;

        BuildFillMesh();
        _screenDirty = true;
        Visible = true;
        GD.Print($"area projection: {_projection} · overlay {_overlay}");
        QueueRedraw();
    }

    private void FitFlat()
    {
        var size = GetViewportRect().Size;
        _unitsPerRadian = Mathf.Max(
            1f,
            Mathf.Min(size.X / Mathf.Max(0.0001f, _span.X), size.Y / Mathf.Max(0.0001f, _span.Y)) * 0.86f);
        _flatZoom = 1f;
        _flatPan = Vector2.Zero;
    }

    private void FitIso()
    {
        if (_diorama is null)
            return;

        var bounds = _diorama.Bounds;
        var size = GetViewportRect().Size;
        var fit = Mathf.Min(
            size.X * 0.88f / Mathf.Max(1e-6f, bounds.Size.X),
            size.Y * 0.78f / Mathf.Max(1e-6f, bounds.Size.Y));
        _isoPixelsPerRadian = Mathf.Max(1f, fit);
        _isoCenter = size * 0.5f;
        _isoZoom = 1f;
        _isoPan = -(bounds.Position + bounds.Size * 0.5f) * _isoPixelsPerRadian;
    }

    /// <summary>Refreshes rover markers after a tick or an action.</summary>
    public void Refresh() => QueueRedraw();

    /// <summary>
    /// Switches the area overlay. Site scores are measured area-wide on first
    /// use; imagery falls back to biome when the sidecar has no area coverage.
    /// </summary>
    public void SetOverlay(RegionViewMode mode)
    {
        if (mode == RegionViewMode.Imagery && !HasImagery)
            mode = RegionViewMode.Biome;

        if (mode == _overlay)
            return;

        _overlay = mode;
        if (!IsLoaded)
            return;

        if (mode == RegionViewMode.SiteScore)
            EnsureSiteScores();

        UpdateFills();
        BuildFillMesh();
        GD.Print($"area overlay: {_overlay}");
        QueueRedraw();
    }

    /// <summary>Advances to the next available overlay (wraps).</summary>
    public void CycleOverlay()
    {
        var next = (int)_overlay + 1;
        for (var attempt = 0; attempt < 5; attempt++, next++)
        {
            var mode = (RegionViewMode)(next % 5);
            if (mode == RegionViewMode.Imagery && !HasImagery)
                continue;

            SetOverlay(mode);
            return;
        }
    }

    /// <summary>Toggles the area-scale terrain-slope presentation (automation hook).</summary>
    public void ToggleSlope()
        => SetOverlay(_overlay == RegionViewMode.Terrain ? RegionViewMode.Biome : RegionViewMode.Terrain);

    /// <summary>Switches between the flat map and the isometric diorama.</summary>
    public void ToggleProjection()
        => SetProjection(_projection == AreaProjectionMode.Flat
            ? AreaProjectionMode.Isometric
            : AreaProjectionMode.Flat);

    public void SetProjection(AreaProjectionMode projection)
    {
        if (projection == _projection)
            return;

        _projection = projection;
        _screenDirty = true;
        GD.Print($"area projection: {_projection}");
        QueueRedraw();
    }

    /// <summary>Orbits the isometric diorama by whole 60° steps.</summary>
    public void Rotate(int steps)
    {
        if (_diorama is null)
            return;

        var before = _diorama.CameraDegrees;
        _diorama.Rotate(steps);
        if (_diorama.CameraDegrees == before)
            return;

        _screenDirty = true;
        GD.Print($"area camera: {_diorama.CameraDegrees}°");
        QueueRedraw();
    }

    /// <summary>Changes the isometric relief exaggeration.</summary>
    public void SetRelief(float scale)
    {
        if (_diorama is null)
            return;

        var before = _diorama.ReliefScale;
        _diorama.SetRelief(scale);
        if (Mathf.IsEqualApprox(before, _diorama.ReliefScale))
            return;

        _screenDirty = true;
        QueueRedraw();
    }

    private void UpdateFills()
    {
        _fills = _overlay switch
        {
            RegionViewMode.Terrain => _terrainFills,
            RegionViewMode.SiteScore => _siteFills.Length == _centers.Length ? _siteFills : _biomeFills,
            RegionViewMode.Buildings => _buildingFills,
            RegionViewMode.Imagery when HasImagery => _imageryFills,
            _ => _biomeFills,
        };
        _diorama?.SetFills(_fills);
    }

    /// <summary>Scores every visible cell once and caches the area-wide ramp.</summary>
    private void EnsureSiteScores()
    {
        if (_siteScores is not null)
            return;

        var count = _centers.Length;
        var scores = new double[count];
        var buildable = new bool[count];
        var stopwatch = Stopwatch.StartNew();
        for (var i = 0; i < count; i++)
        {
            var score = _tiles[i].ScoreSite(_ordinals[i], _world, _siteConfig);
            scores[i] = score.CompositeWith(_siteConfig.Weights);
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

            min = Math.Min(min, scores[i]);
            max = Math.Max(max, scores[i]);
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
        UpdateFills();
        GD.Print($"area site scores: {count} hexes in {_siteScoreMs:F1} ms " +
                 $"(range {_siteScoreMin:P0} – {_siteScoreMax:P0})");
    }

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
        if (!IsLoaded)
            return;

        if (_screenDirty)
            RebuildScreenCache();

        var borders = UsesIsometric ? _isoScreenRegionBorders : _screenRegionBorders;
        var grid = UsesIsometric ? _isoScreenBorders : _screenBorders;

        if (UsesIsometric && _diorama is not null)
        {
            var isoScale = _isoPixelsPerRadian * _isoZoom;
            DrawMesh(_diorama.Mesh, _whiteTexture,
                new Transform2D(new Vector2(isoScale, 0f), new Vector2(0f, isoScale), _isoCenter + _isoPan));
        }
        else if (_fillMesh is not null)
        {
            var scale = _unitsPerRadian * _flatZoom;
            var origin = GetViewportRect().Size * 0.5f + _flatPan
                         + new Vector2(_origin.X * scale, -_origin.Y * scale);
            DrawMesh(_fillMesh, _whiteTexture,
                new Transform2D(new Vector2(scale, 0f), new Vector2(0f, -scale), origin));
        }

        DrawMultiline(grid, BorderColor, 1f, false);

        if (_hoverRegionIndex >= 0 && _hoverRegionIndex < _areas.Count)
        {
            var hovered = _areas[_hoverRegionIndex];
            if (borders.TryGetValue(hovered.Index, out var hoverBorder))
                DrawMultiline(hoverBorder, HoverShade, 1.5f, false);
        }

        DrawRoverTracks();
        DrawRoverMarkers();
        if (_overlay == RegionViewMode.Buildings)
            DrawBuildingMarkers();
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
            Pan += motion.Relative;
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
        var scale = CurrentScale;

        foreach (var area in _areas)
        {
            var screen = ScreenForCell(area.CenterCell, scale, area.Center);
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
        var scale = CurrentScale;
        foreach (var rover in _campaign.Rovers)
        {
            if (CellFor(rover.LocationRegion, rover.LocationHex) is not { } cell)
                continue;

            if (ScreenForCell(cell, scale, _centers[cell]).DistanceTo(position) <= 14f)
                return rover;
        }

        return null;
    }

    /// <summary>Screen position of one cell in the active projection.</summary>
    private Vector2 ScreenForCell(int cell, float scale, Vector2 planeFallback)
        => UsesIsometric && _diorama is not null
            ? IsoToScreen(_diorama.IsoCenters[cell], scale)
            : ToScreen(planeFallback, scale);

    private void ZoomAt(float factor, Vector2 position)
    {
        var before = Zoom;
        Zoom = Mathf.Clamp(before * factor, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(before, Zoom))
            return;

        // Keep the world point under the cursor stable in either projection.
        var viewport = GetViewportRect().Size * 0.5f;
        var world = (position - viewport - Pan) / before;
        Pan += world * (before - Zoom);
        _screenDirty = true;
        QueueRedraw();
    }

    private void OnViewportResized()
    {
        if (!IsLoaded)
            return;

        if (UsesIsometric)
        {
            FitIso();
        }
        else
        {
            var size = GetViewportRect().Size;
            _unitsPerRadian = Mathf.Max(
                1f,
                Mathf.Min(size.X / Mathf.Max(0.0001f, _span.X), size.Y / Mathf.Max(0.0001f, _span.Y)) * 0.86f);
        }

        _screenDirty = true;
        QueueRedraw();
    }

    private void RebuildScreenCache()
    {
        if (UsesIsometric && _diorama is not null)
        {
            var scale = _isoPixelsPerRadian * _isoZoom;
            _isoScreenBorders = IsoScreenEdges(Enumerable.Range(0, _corners.Length));
            _isoScreenRegionBorders.Clear();
            foreach (var area in _areas)
                _isoScreenRegionBorders[area.Index] = IsoScreenEdges(area.Cells);

            Vector2[] IsoScreenEdges(IEnumerable<int> cells)
            {
                var edges = new List<Vector2>();
                foreach (var cell in cells)
                {
                    var polygon = _diorama.IsoCorners[cell];
                    for (var v = 0; v < polygon.Length; v++)
                    {
                        edges.Add(IsoToScreen(polygon[v], scale));
                        edges.Add(IsoToScreen(polygon[(v + 1) % polygon.Length], scale));
                    }
                }

                return edges.ToArray();
            }
        }
        else
        {
            var scale = _unitsPerRadian * _flatZoom;
            _screenBorders = ToScreen(_borders, scale);
            _screenRegionBorders.Clear();
            foreach (var (region, points) in _regionBorders)
                _screenRegionBorders[region] = ToScreen(points, scale);
        }

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
        return viewport + _flatPan + new Vector2(
            (point.X + _origin.X) * scale,
            -(point.Y + _origin.Y) * scale);
    }

    private Vector2 IsoToScreen(Vector2 iso, float scale)
        => _isoCenter + _isoPan + iso * scale;

    private void BuildFillMesh()
    {
        var vertexCount = 0;
        foreach (var polygon in _corners)
            vertexCount += polygon.Length * 3;

        var vertices = new Vector2[vertexCount];
        var colors = new Color[vertexCount];
        var index = 0;

        var terrain = UsesSlope;
        var minShade = float.MaxValue;
        var maxShade = float.MinValue;
        for (var i = 0; i < _corners.Length; i++)
        {
            var center = _centers[i];
            var polygon = _corners[i];
            var color = terrain ? _terrainFills[i] : _fills[i];
            var baseLuma = Mathf.Max(1e-4f, color.R + color.G + color.B);

            for (var v = 0; v < polygon.Length; v++)
            {
                var next = (v + 1) % polygon.Length;
                var facetColor = terrain
                    ? FacetShade(color, center, polygon[v], polygon[next],
                        _heights[i], _cornerHeights[i][v], _cornerHeights[i][next])
                    : color;

                if (terrain)
                {
                    var shade = (facetColor.R + facetColor.G + facetColor.B) / baseLuma;
                    minShade = Mathf.Min(minShade, shade);
                    maxShade = Mathf.Max(maxShade, shade);
                }

                vertices[index] = center;
                colors[index++] = facetColor;
                vertices[index] = polygon[v];
                colors[index++] = facetColor;
                vertices[index] = polygon[next];
                colors[index++] = facetColor;
            }
        }

        if (terrain)
            GD.Print($"area slope facets: shade x{minShade:F2}..{maxShade:F2}");

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        _fillMesh = new ArrayMesh();
        _fillMesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
    }

    private void DrawRoverTracks()
    {
        var scale = CurrentScale;
        foreach (var rover in _campaign.Rovers)
        {
            var plan = _campaign.RouteForRover(rover.Id);
            if (plan is null || plan.LegCount == 0)
                continue;

            var points = new List<Vector2>(plan.LegCount + 1);
            if (CellFor(plan.StartRegion, plan.StartHex) is { } start)
                points.Add(ScreenForCell(start, scale, _centers[start]));

            foreach (var leg in plan.Legs)
            {
                if (CellFor(leg.ToRegion, leg.ToHex) is { } cell)
                    points.Add(ScreenForCell(cell, scale, _centers[cell]));
            }

            if (points.Count > 1)
                DrawPolyline(points.ToArray(), RouteColor, 3f, true);
        }
    }

    private void DrawRoverMarkers()
    {
        var scale = CurrentScale;

        foreach (var rover in _campaign.Rovers)
        {
            if (CellFor(rover.LocationRegion, rover.LocationHex) is not { } cell)
                continue;

            var position = ScreenForCell(cell, scale, _centers[cell]);
            var selected = rover.Id == SelectedRoverId;
            var color = rover.Status == RoverStatus.Enroute ? RouteColor : RoverColor;
            DrawCircle(position, selected ? 8f : 6f, selected ? SelectedColor : color);
            DrawCircle(position, selected ? 4f : 3f, Colors.Black);

            if (rover.DestinationRegion is { } destinationRegion
                && rover.DestinationHex is { } destinationHex
                && CellFor(destinationRegion, destinationHex) is { } destination)
            {
                DrawCircle(ScreenForCell(destination, scale, _centers[destination]), 7f, color, false, 2f);
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

    /// <summary>One aggregate pin per occupied hex across the seven regions.</summary>
    private void DrawBuildingMarkers()
    {
        var groups = new Dictionary<int, List<BuildingInstance>>();
        foreach (var area in _areas)
        {
            foreach (var building in _campaign.Buildings(area.Index))
            {
                if (!_cellByKey.TryGetValue((area.Index, (ulong)building.ParentHex), out var cell))
                    continue;

                if (!groups.TryGetValue(cell, out var list))
                {
                    list = new List<BuildingInstance>();
                    groups[cell] = list;
                }

                list.Add(building);
            }
        }

        var scale = CurrentScale;
        var radius = UsesIsometric && _diorama is not null
            ? Mathf.Max(5f, _diorama.CellRadius * scale * 0.42f)
            : Mathf.Max(5f, 0.0006f * scale * 0.42f);

        foreach (var (cell, installations) in groups)
        {
            var center = ScreenForCell(cell, scale, _centers[cell]);
            var color = BuildingStatusColor(AggregateStatus(installations));
            DrawCircle(center, radius + 2f, new Color(0f, 0f, 0f, 0.75f));
            DrawCircle(center, radius, color);

            if (installations.Count > 1)
            {
                DrawString(_font, center + new Vector2(radius + 4f, -radius - 6f),
                    installations.Count.ToString(), HorizontalAlignment.Left, -1, 11,
                    new Color(0.98f, 0.98f, 1f));
            }
        }
    }

    private static BuildingStatus AggregateStatus(IReadOnlyList<BuildingInstance> installations)
    {
        var statuses = installations.Select(building => building.Status).ToList();
        if (statuses.Contains(BuildingStatus.Destroyed)) return BuildingStatus.Destroyed;
        if (statuses.Contains(BuildingStatus.Damaged)) return BuildingStatus.Damaged;
        if (statuses.Contains(BuildingStatus.UnderConstruction)) return BuildingStatus.UnderConstruction;
        if (statuses.Contains(BuildingStatus.Idle)) return BuildingStatus.Idle;
        if (statuses.Contains(BuildingStatus.Operational)) return BuildingStatus.Operational;
        return BuildingStatus.Planned;
    }

    private static Color BuildingStatusColor(BuildingStatus status) => status switch
    {
        BuildingStatus.Planned => new Color(0.65f, 0.65f, 0.75f),
        BuildingStatus.UnderConstruction => new Color(1f, 0.78f, 0.32f),
        BuildingStatus.Operational => new Color(0.32f, 0.95f, 0.55f),
        BuildingStatus.Idle => new Color(0.45f, 0.72f, 1f),
        BuildingStatus.Damaged => new Color(0.95f, 0.5f, 0.3f),
        BuildingStatus.Destroyed => new Color(0.35f, 0.35f, 0.4f),
        _ => new Color(0.9f, 0.9f, 0.95f),
    };

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
            ? "click to centre here · Enter opens region · click a rover first to dispatch"
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
