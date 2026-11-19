using System;
using System.Collections.Generic;
using System.Diagnostics;
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

/// <summary>
/// Close-up colony management view: the region rendered as a procedural 2.5D
/// isometric diorama of extruded hex prisms.
///
/// The terrain is baked into a single <see cref="ArrayMesh"/> with painter-sorted
/// vertices (far prisms first), so drawing the whole region costs one mesh draw.
/// The camera is a plain 2D transform over isometric units, which keeps picking
/// and placement exact and cheap. Orbiting rotates the plane in 60° steps about
/// the centre of the view, so the diorama turns in place.
/// </summary>
public partial class ManagementView : Node2D
{
    /// <summary>Hovered hex, or -1 when the cursor is off the region.</summary>
    [Signal]
    public delegate void HexHoveredEventHandler(int ordinal, string biomeId, int elevationM, int craterKind);

    /// <summary>Clicked hex.</summary>
    [Signal]
    public delegate void HexSelectedEventHandler(int ordinal, string biomeId, int elevationM, int craterKind);

    /// <summary>Colour overlay changed.</summary>
    [Signal]
    public delegate void ViewModeChangedEventHandler(int mode);

    private const float MinZoom = 0.6f;
    private const float MaxZoom = 10f;
    private const float DragThresholdPixels = 4f;

    /// <summary>Fraction of the region width the full elevation range should rise.</summary>
    private const float ReliefFraction = 0.18f;

    /// <summary>Thickness of the terrain slab, as a fraction of the region width.</summary>
    private const float SlabFraction = 0.025f;

    /// <summary>How far the visible top is inset before the chamfered rim.</summary>
    private const float BevelInset = 0.12f;

    /// <summary>Drop from the flat top to the chamfered rim, relative to the slab.</summary>
    private const float BevelDropFraction = 0.35f;

    /// <summary>Strength of the directional top-slope shading test.</summary>
    private const float SlopeShadeStrength = 0.26f;

    private static readonly Color BorderColor = new(0f, 0f, 0f, 0.20f);
    private static readonly Color HoverColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color SelectionColor = new(0.55f, 0.85f, 1f, 0.95f);
    private static readonly Vector2 LightDirection = new Vector2(-0.45f, 0.55f).Normalized();

    private static Color ScaleColor(Color color, float factor)
    {
        factor = Mathf.Max(0f, factor);
        return new Color(
            Mathf.Min(1f, color.R * factor),
            Mathf.Min(1f, color.G * factor),
            Mathf.Min(1f, color.B * factor),
            color.A);
    }

    private BodyWorld _world = null!;
    private RegionTile _tile = null!;
    private SurfaceImagery? _imagery;
    private CampaignState? _campaign;
    private Font _font = null!;

    private Vector2[][] _corners = Array.Empty<Vector2[]>();
    private Vector2[][] _rotatedCorners = Array.Empty<Vector2[]>();
    private Vector2[][] _isoCorners = Array.Empty<Vector2[]>();
    private Vector2[] _rotatedCenters = Array.Empty<Vector2>();
    private Vector2[] _isoCenters = Array.Empty<Vector2>();
    private float[] _elevations = Array.Empty<float>();
    private float[] _heights = Array.Empty<float>();
    private float[] _bevelDrops = Array.Empty<float>();
    private float[] _slopeShades = Array.Empty<float>();
    private Color[] _biomeFills = Array.Empty<Color>();
    private Color[] _terrainFills = Array.Empty<Color>();
    private Color[] _imageryFills = Array.Empty<Color>();
    private Color[] _siteFills = Array.Empty<Color>();
    private Color[] _buildingFills = Array.Empty<Color>();
    private Color[] _fills = Array.Empty<Color>();
    private double[]? _siteScores;
    private double _siteScoreMs;
    private double _siteScoreMin;
    private double _siteScoreMax = 1.0;
    private string[] _biomeIds = Array.Empty<string>();
    private int[] _order = Array.Empty<int>();
    private readonly Dictionary<ulong, int> _ordinalByIndex = new();

    private float _autoRelief = 1f;
    private float _slab;
    private float _baseHeight;
    private float _cellRadius;
    private double _radiusKm = 1737.4;

    private ArrayMesh? _mesh;
    private bool _meshDirty = true;
    private Vector2[] _outlinePoints = Array.Empty<Vector2>();
    private readonly Dictionary<int, Vector2[]> _drawBuffers = new();
    private readonly Dictionary<int, Vector2[]> _closedBuffers = new();
    private ImageTexture _whiteTexture = null!;

    private Vector2 _center;
    private Vector2 _pan;
    private float _pixelsPerRadian = 8000f;
    private float _zoom = 1f;

    /// <summary>Plane point the diorama is rotated about; fixed until the next orbit.</summary>
    private Vector2 _pivot;

    /// <summary>Plane-space centre of the region, used by refits and the first orbit.</summary>
    private Vector2 _homePivot;

    /// <summary>True once the player pans or zooms; rotation then keeps their view.</summary>
    private bool _viewAdjusted;

    private int _yawSteps;
    private float _yaw;

    private int _hovered = -1;
    private int _selected = -1;

    private bool _panning;
    private Vector2 _lastMouse;

    private double _drawTotalMs;
    private int _drawCount;

    /// <summary>Region shown by this view, or 0 before the first <see cref="Load"/>.</summary>
    public ulong RegionIndex => _tile?.Entry.Index ?? 0;

    /// <summary>Region shown by this view, or 0 before the first <see cref="Load"/>.</summary>
    public bool IsLoaded => _tile is not null;

    public string RegionBiomeId { get; private set; } = string.Empty;

    /// <summary>Distinct biome ids present in this region, in first-seen order.</summary>
    public IReadOnlyList<string> BiomesPresent { get; private set; } = Array.Empty<string>();

    public int HexCount => _tile?.HexCount ?? 0;

    /// <summary>Installations placed in this region.</summary>
    public int BuildingCount => _campaign is null || _tile is null ? 0 : _campaign.BuildingCount(_tile.Entry.Index);

    /// <summary>Fine hexes in this region that contain at least one installation.</summary>
    public int SiteCount => _campaign is null || _tile is null ? 0 : _campaign.Sites(_tile.Entry.Index).Count;

    public int SelectedHex => _selected;

    /// <summary>The region tile displayed by this view.</summary>
    public RegionTile Tile => _tile;

    /// <summary>Number of 60° steps in a full camera orbit.</summary>
    public const int RotationStepCount = 6;

    /// <summary>Current orbit step (0-5).</summary>
    public int RotationSteps => _yawSteps;

    /// <summary>Current orbit angle in degrees (0, 60, … 300).</summary>
    public int CameraDegrees => _yawSteps * 60;

    public short TerrainMinM { get; private set; }

    public short TerrainMaxM { get; private set; }

    /// <summary>Colour overlay shown on the prisms.</summary>
    public RegionViewMode Overlay { get; private set; } = RegionViewMode.Terrain;

    /// <summary>True when the loaded region has valid imagery colours.</summary>
    public bool HasImagery => _imagery is not null && _imageryFills.Length == _corners.Length;

    /// <summary>Binds the optional surface-colour sidecar before a region is loaded.</summary>
    public void SetImagery(SurfaceImagery? imagery) => _imagery = imagery;

    /// <summary>User multiplier on the automatic relief exaggeration.</summary>
    public float ReliefScale { get; set; } = 1f;

    /// <summary>Scoring configuration used for the site-score overlay.</summary>
    public SiteScorerConfig SiteConfig { get; set; } = new() { BodyRadiusKm = 1737.4 };

    public double SiteScoreMin => _siteScoreMin;

    public double SiteScoreMax => _siteScoreMax;

    public double SiteScoreMs => _siteScoreMs;

    /// <summary>CPU time spent in the most recent <c>_Draw</c> call (ms).</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>Average CPU time per <c>_Draw</c> since <see cref="ResetDrawStats"/> (ms).</summary>
    public double AverageDrawMs => _drawCount > 0 ? _drawTotalMs / _drawCount : 0.0;

    public void ResetDrawStats()
    {
        _drawTotalMs = 0.0;
        _drawCount = 0;
    }

    /// <summary>Binds the campaign that owns building state and scoring config.</summary>
    public void SetCampaign(CampaignState campaign)
    {
        ArgumentNullException.ThrowIfNull(campaign);

        _campaign = campaign;
        SiteConfig = campaign.SiteConfig;
        _meshDirty = true;
        QueueRedraw();
    }

    public void Load(BodyWorld world, ulong regionIndex)
    {
        _world = world;
        _tile = world.GetRegion(regionIndex);
        RegionBiomeId = world.Biomes[_tile.DominantBiomeCode].Id;
        _radiusKm = world.Manifest.Body.RadiusKm;
        SiteConfig = (_campaign?.SiteConfig ?? SiteConfig) with { BodyRadiusKm = _radiusKm };

        var cells = _tile.Cells;
        var count = cells.Count;

        _corners = new Vector2[count][];
        _rotatedCorners = new Vector2[count][];
        _isoCorners = new Vector2[count][];
        _rotatedCenters = new Vector2[count];
        _isoCenters = new Vector2[count];
        _elevations = new float[count];
        _heights = new float[count];
        _bevelDrops = new float[count];
        _slopeShades = new float[count];
        _biomeFills = new Color[count];
        _terrainFills = new Color[count];
        _imageryFills = Array.Empty<Color>();
        if (_imagery is not null)
        {
            var imageryFills = new Color[count];
            if (_imagery.TryCopyRegion(regionIndex, count, imageryFills))
                _imageryFills = imageryFills;
        }

        _buildingFills = new Color[count];
        _siteFills = Array.Empty<Color>();
        _siteScores = null;
        _siteScoreMs = 0;
        _biomeIds = new string[count];
        _ordinalByIndex.Clear();

        var regionCenter = ToUnitVector(_tile.Region.ToLatLng());
        var (east, north) = TangentBasis(regionCenter);

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        var minElevation = short.MaxValue;
        var maxElevation = short.MinValue;
        var biomesPresent = new List<string>();

        for (var i = 0; i < count; i++)
        {
            var record = _tile[i];
            var biome = world.Biomes[record.BiomeCode];

            _biomeIds[i] = biome.Id;
            _biomeFills[i] = BiomePalette.For(biome.Id);

            if (!biomesPresent.Contains(biome.Id))
                biomesPresent.Add(biome.Id);

            _elevations[i] = record.ElevationM;
            minElevation = Math.Min(minElevation, record.ElevationM);
            maxElevation = Math.Max(maxElevation, record.ElevationM);
            _ordinalByIndex[(ulong)cells[i]] = i;

            var boundary = cells[i].GetCellBoundaryVertices().ToList();
            var corners = new Vector2[boundary.Count];

            for (var v = 0; v < boundary.Count; v++)
            {
                corners[v] = Project(ToUnitVector(boundary[v]), regionCenter, east, north);
                min = new Vector2(Mathf.Min(min.X, corners[v].X), Mathf.Min(min.Y, corners[v].Y));
                max = new Vector2(Mathf.Max(max.X, corners[v].X), Mathf.Max(max.Y, corners[v].Y));
            }

            _corners[i] = corners;
        }

        TerrainMinM = (short)minElevation;
        TerrainMaxM = (short)maxElevation;
        BiomesPresent = biomesPresent;
        _homePivot = (min + max) * 0.5f;

        var elevationSpan = Math.Max(1, maxElevation - minElevation);
        var spanPlane = Mathf.Max(max.X - min.X, max.Y - min.Y);
        var elevationSpanPlane = (maxElevation - minElevation) / (_radiusKm * 1000.0);

        _autoRelief = elevationSpanPlane > 1e-9
            ? ReliefFraction * spanPlane / (float)elevationSpanPlane
            : 0f;
        _slab = SlabFraction * spanPlane;

        for (var i = 0; i < count; i++)
        {
            var t = (_elevations[i] - minElevation) / (double)elevationSpan;
            _terrainFills[i] = TerrainPalette.For(t);

            var biomeFill = _biomeFills[i];
            _buildingFills[i] = new Color(biomeFill.R * 0.32f, biomeFill.G * 0.32f, biomeFill.B * 0.32f, 1f);
        }

        // The plane-space region centre is the orbit pivot until the player pans.
        _pivot = _homePivot;

        _outlinePoints = new Vector2[count * 6 * 2];
        _hovered = -1;
        _selected = -1;

        UpdateFills();
        RebuildGeometry();
        FitView();

        GD.Print($"colony: {count} hexes · span {spanPlane:F4} · elev {TerrainMinM}..{TerrainMaxM} m · " +
                 $"auto relief {_autoRelief:F2} · slab {_slab:F5} · fit {_pixelsPerRadian:F0} px/unit · " +
                 $"camera {CameraDegrees}°");

        Visible = true;
        EmitSignal(SignalName.ViewModeChanged, (int)Overlay);
        QueueRedraw();
    }

    /// <summary>Switches the colour overlay; site scores are computed on first use.</summary>
    public void SetOverlay(RegionViewMode mode)
    {
        // Never show a labelled imagery overlay over biome fallback colours.
        if (mode == RegionViewMode.Imagery && !HasImagery)
            mode = RegionViewMode.Biome;

        if (Overlay == mode)
            return;

        Overlay = mode;

        if (mode == RegionViewMode.SiteScore)
            EnsureSiteScores();

        UpdateFills();
        _meshDirty = true;
        EmitSignal(SignalName.ViewModeChanged, (int)mode);
        QueueRedraw();
    }

    /// <summary>Advances to the next available overlay (wraps).</summary>
    public void CycleOverlay()
    {
        var next = (int)Overlay + 1;
        for (var attempt = 0; attempt < 5; attempt++, next++)
        {
            var mode = (RegionViewMode)(next % 5);
            if (mode == RegionViewMode.Imagery && !HasImagery)
                continue;

            SetOverlay(mode);
            return;
        }
    }

    /// <summary>Orbits the camera by whole 60° steps (positive is clockwise).</summary>
    public void Rotate(int steps) => SetRotation(_yawSteps + steps);

    /// <summary>
    /// Sets the orbit step, wrapping into 0-5. The plane turns about the point at
    /// the centre of the screen, so the diorama rotates in place even after a pan.
    /// </summary>
    public void SetRotation(int steps)
    {
        var normalized = ((steps % RotationStepCount) + RotationStepCount) % RotationStepCount;
        if (normalized == _yawSteps)
            return;

        _yawSteps = normalized;
        _yaw = normalized * Mathf.Pi / 3f;

        if (_tile is null)
            return;

        if (_viewAdjusted)
        {
            UpdatePivot();
            RebuildGeometry();
        }
        else
        {
            _pivot = _homePivot;
            RebuildGeometry();
            FitView();
        }

        GD.Print($"camera: {CameraDegrees}° · step {_yawSteps + 1}/{RotationStepCount}");
        QueueRedraw();
    }

    /// <summary>Changes the relief multiplier and refits the diorama.</summary>
    public void SetRelief(float scale)
    {
        var clamped = Mathf.Clamp(scale, 0.2f, 4f);
        if (Mathf.IsEqualApprox(clamped, ReliefScale))
            return;

        ReliefScale = clamped;
        _pivot = _homePivot;
        RebuildGeometry();
        FitView();
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

        if (_meshDirty || _mesh is null)
            BuildMesh();

        DrawMesh(_mesh!, _whiteTexture,
            new Transform2D(new Vector2(scale, 0f), new Vector2(0f, scale), _center + _pan));

        DrawGridOutlines(scale);

        if (_campaign is not null && _tile is not null)
            DrawBuildingMarkers(scale);

        if (_hovered >= 0)
            DrawCellOutline(_hovered, scale, HoverColor, 2f);

        if (_selected >= 0 && _selected != _hovered)
            DrawCellOutline(_selected, scale, SelectionColor, 2f);

        LastDrawMs = (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency;
        _drawTotalMs += LastDrawMs;
        _drawCount++;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || _tile is null)
            return;

        switch (@event)
        {
            case InputEventMouseButton button:
                HandleMouseButton(button);
                break;

            case InputEventMouseMotion motion:
                HandleMouseMotion(motion);
                break;

            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.Bracketleft:
                SetRelief(ReliefScale / 1.15f);
                break;

            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.Bracketright:
                SetRelief(ReliefScale * 1.15f);
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

            case MouseButton.Left when !button.Pressed:
                SelectHovered();
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
            _viewAdjusted = true;
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
        _viewAdjusted = true;
        QueueRedraw();
    }

    private void UpdateHover(Vector2 mouse)
    {
        var hit = Pick(mouse);
        if (hit == _hovered)
            return;

        _hovered = hit;
        QueueRedraw();

        if (hit < 0)
        {
            EmitSignal(SignalName.HexHovered, -1, string.Empty, 0, 0);
            return;
        }

        EmitSignal(SignalName.HexHovered, hit, _biomeIds[hit], (int)_elevations[hit],
            (int)_tile[hit].Crater);
    }

    private void SelectHovered()
    {
        if (_hovered < 0)
        {
            _selected = -1;
            QueueRedraw();
            return;
        }

        _selected = _hovered;
        EmitSignal(SignalName.HexSelected, _selected, _biomeIds[_selected], (int)_elevations[_selected],
            (int)_tile[_selected].Crater);
        QueueRedraw();
    }

    private int Pick(Vector2 mouse)
    {
        if (_isoCenters.Length == 0)
            return -1;

        var scale = _pixelsPerRadian * _zoom;
        var maxDistance = Mathf.Max(6f, _cellRadius * scale * 1.1f);
        var maxDistanceSquared = maxDistance * maxDistance;

        var best = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _isoCenters.Length; i++)
        {
            var distance = ToScreen(_isoCenters[i], scale).DistanceSquaredTo(mouse);
            if (distance < bestDistance && distance <= maxDistanceSquared)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void UpdateFills()
    {
        _fills = Overlay switch
        {
            RegionViewMode.Biome => _biomeFills,
            RegionViewMode.Imagery when HasImagery => _imageryFills,
            RegionViewMode.SiteScore => _siteFills.Length == _biomeFills.Length ? _siteFills : _biomeFills,
            RegionViewMode.Buildings => _buildingFills,
            _ => _terrainFills,
        };
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
        UpdateFills();
        _meshDirty = true;

        GD.Print($"site scores: {count} hexes in {_siteScoreMs:F1} ms " +
                 $"(range {_siteScoreMin:P0} – {_siteScoreMax:P0})");
    }

    /// <summary>Recomputes elevations, rotated corners, isometric corners and depth order.</summary>
    private void RebuildGeometry()
    {
        if (_tile is null)
            return;

        var radiusMetres = _radiusKm * 1000.0;
        var cos = Mathf.Cos(_yaw);
        var sin = Mathf.Sin(_yaw);
        _baseHeight = -_slab;

        for (var i = 0; i < _corners.Length; i++)
        {
            var height = (float)((_elevations[i] - TerrainMinM) / radiusMetres * _autoRelief * ReliefScale);
            _heights[i] = height;

            // A small chamfer breaks up the flat prism top without changing the
            // cell's authoritative elevation or its picking centre.
            var bevelDrop = Mathf.Min(_slab * BevelDropFraction, _slab * 0.5f + Mathf.Max(0f, height) * 0.2f);
            _bevelDrops[i] = bevelDrop;
            var rimHeight = height - bevelDrop;

            var center = IsoProjection.Rotate(CellCenter(i), _pivot, cos, sin);
            _rotatedCenters[i] = center;
            _isoCenters[i] = IsoProjection.Project(center, height);

            var corners = _corners[i];
            var rotated = new Vector2[corners.Length];
            var iso = new Vector2[corners.Length];
            for (var v = 0; v < corners.Length; v++)
            {
                rotated[v] = IsoProjection.Rotate(corners[v], _pivot, cos, sin);
                iso[v] = IsoProjection.Project(rotated[v], rimHeight);
            }

            _rotatedCorners[i] = rotated;
            _isoCorners[i] = iso;
        }

        // Directional top shading is derived from neighbour elevation gradients.
        // It is only a vertex-colour multiplier; movement and placement still use
        // the H3 cell elevations unchanged.
        var cells = _tile.Cells;
        for (var i = 0; i < _corners.Length; i++)
        {
            var center = _rotatedCenters[i];
            var gradient = Vector2.Zero;
            foreach (var neighbour in HexGrid.Neighbors(cells[i]))
            {
                if (!_ordinalByIndex.TryGetValue((ulong)neighbour, out var j))
                    continue;

                var delta = _rotatedCenters[j] - center;
                var lengthSquared = delta.LengthSquared();
                if (lengthSquared < 1e-12f)
                    continue;

                gradient += delta * ((_heights[j] - _heights[i]) / lengthSquared);
            }

            _slopeShades[i] = Mathf.Clamp(
                -gradient.Dot(LightDirection) * SlopeShadeStrength, -0.22f, 0.22f);
        }

        _cellRadius = EstimateCellRadius();

        // Far prisms first so nearer ones paint over them.
        _order = Enumerable.Range(0, _corners.Length)
            .OrderByDescending(i => IsoProjection.Depth(_rotatedCenters[i]))
            .ToArray();

        _meshDirty = true;
        QueueRedraw();
    }

    private void BuildMesh()
    {
        // Per corner: two wall triangles (6 vertices), two bevel triangles (6)
        // and one top-fan triangle (3). Sizing from actual corner counts also
        // handles the rare pentagon cell.
        var vertexCount = _corners.Sum(corners => corners.Length * 15);
        var vertices = new Vector2[vertexCount];
        var colors = new Color[vertexCount];
        var index = 0;

        foreach (var i in _order)
        {
            var rim = _isoCorners[i];
            var plane = _rotatedCorners[i];
            var rotatedCenter = _rotatedCenters[i];
            var center = _isoCenters[i];
            var fill = _fills[i];
            var height = _heights[i];
            var rimHeight = height - _bevelDrops[i];
            var n = rim.Length;

            var inner = new Vector2[n];
            for (var v = 0; v < n; v++)
                inner[v] = IsoProjection.Project(plane[v].Lerp(rotatedCenter, BevelInset), height);

            for (var v = 0; v < n; v++)
            {
                var next = (v + 1) % n;
                var topA = rim[v];
                var topB = rim[next];
                var bottomA = new Vector2(topA.X, topA.Y + rimHeight - _baseHeight);
                var bottomB = new Vector2(topB.X, topB.Y + rimHeight - _baseHeight);

                var edge = plane[next] - plane[v];
                var normal = new Vector2(edge.Y, -edge.X).Normalized();
                var shade = 0.42f + 0.20f * Mathf.Max(0f, normal.Dot(LightDirection));
                var wall = ScaleColor(fill, shade);

                // Two triangles per wall, wound consistently with the top fan.
                vertices[index] = topA;
                colors[index++] = wall;
                vertices[index] = topB;
                colors[index++] = wall;
                vertices[index] = bottomB;
                colors[index++] = wall;

                vertices[index] = topA;
                colors[index++] = wall;
                vertices[index] = bottomB;
                colors[index++] = wall;
                vertices[index] = bottomA;
                colors[index++] = wall;
            }

            for (var v = 0; v < n; v++)
            {
                var next = (v + 1) % n;
                var edge = plane[next] - plane[v];
                var normal = new Vector2(edge.Y, -edge.X).Normalized();
                var shade = 0.74f + 0.18f * Mathf.Max(0f, normal.Dot(LightDirection))
                                   + _slopeShades[i] * 0.35f;
                var bevel = ScaleColor(fill, shade);

                vertices[index] = inner[v];
                colors[index++] = bevel;
                vertices[index] = rim[v];
                colors[index++] = bevel;
                vertices[index] = rim[next];
                colors[index++] = bevel;

                vertices[index] = inner[v];
                colors[index++] = bevel;
                vertices[index] = rim[next];
                colors[index++] = bevel;
                vertices[index] = inner[next];
                colors[index++] = bevel;
            }

            var top = ScaleColor(fill, 1.02f + _slopeShades[i]);

            for (var v = 0; v < n; v++)
            {
                vertices[index] = center;
                colors[index++] = top;
                vertices[index] = inner[v];
                colors[index++] = top;
                vertices[index] = inner[(v + 1) % n];
                colors[index++] = top;
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        _mesh = new ArrayMesh();
        _mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        _meshDirty = false;
    }

    private void DrawGridOutlines(float scale)
    {
        var index = 0;
        for (var i = 0; i < _isoCorners.Length; i++)
        {
            var iso = _isoCorners[i];
            for (var v = 0; v < iso.Length; v++)
            {
                _outlinePoints[index++] = ToScreen(iso[v], scale);
                _outlinePoints[index++] = ToScreen(iso[(v + 1) % iso.Length], scale);
            }
        }

        DrawMultiline(_outlinePoints, BorderColor, 1f, false);
    }

    /// <summary>
    /// One aggregate pin per site. The colony view is an inspector: a parent hex
    /// may hold many installations, so only the count and most urgent status are
    /// drawn here. Placement happens inside the site view.
    /// </summary>
    private void DrawBuildingMarkers(float scale)
    {
        foreach (var group in _campaign!.Buildings(_tile!.Entry.Index)
                     .GroupBy(building => (ulong)building.ParentHex))
        {
            if (!_ordinalByIndex.TryGetValue(group.Key, out var ordinal))
                continue;

            var installations = group.ToArray();
            var color = BuildingStatusColor(AggregateStatus(installations));
            var center = ToScreen(_isoCenters[ordinal], scale);
            var radius = Mathf.Max(5f, _cellRadius * scale * 0.42f);

            DrawCircle(center, radius + 2f, new Color(0f, 0f, 0f, 0.75f));
            DrawCircle(center, radius, color);

            if (installations.Length > 1)
            {
                DrawString(_font, center + new Vector2(radius + 4f, -radius - 6f),
                    installations.Length.ToString(), HorizontalAlignment.Left, -1, 11,
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

    private void DrawCellOutline(int ordinal, float scale, Color color, float width)
    {
        var iso = _isoCorners[ordinal];
        var screen = ScreenBuffer(iso.Length);

        for (var v = 0; v < iso.Length; v++)
            screen[v] = ToScreen(iso[v], scale);

        DrawPolyline(Close(screen), color, width, true);
    }

    private void FitView()
    {
        if (_isoCorners.Length == 0)
            return;

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);

        foreach (var corners in _isoCorners)
        {
            foreach (var corner in corners)
            {
                min = new Vector2(Mathf.Min(min.X, corner.X), Mathf.Min(min.Y, corner.Y));
                max = new Vector2(Mathf.Max(max.X, corner.X), Mathf.Max(max.Y, corner.Y));
            }
        }

        var size = GetViewportRect().Size;
        var span = new Vector2(Mathf.Max(max.X - min.X, 1e-6f), Mathf.Max(max.Y - min.Y, 1e-6f));
        var fit = Mathf.Min(size.X * 0.88f / span.X, size.Y * 0.78f / span.Y);

        _center = size * 0.5f;
        _zoom = 1f;
        _pixelsPerRadian = fit;
        _pan = -((min + max) * 0.5f) * fit;
        _pivot = _homePivot;
        _viewAdjusted = false;
    }

    /// <summary>
    /// Uses the plane point currently under the centre of the screen as the orbit
    /// pivot, so the view turns in place and a zoomed-in area stays in frame.
    /// </summary>
    private void UpdatePivot()
    {
        var scale = _pixelsPerRadian * _zoom;
        _pivot = IsoProjection.Unproject(-_pan / scale, 0f);
    }

    private Vector2 ToScreen(Vector2 iso, float scale) => _center + _pan + iso * scale;

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

    private Vector2 CellCenter(int ordinal)
    {
        // Average the corners: the gnomonic cell centre, without another lat/lon round trip.
        var corners = _corners[ordinal];
        var sum = Vector2.Zero;
        foreach (var corner in corners)
            sum += corner;

        return sum / corners.Length;
    }

    private float EstimateCellRadius()
    {
        var sum = 0f;
        var count = 0;

        for (var i = 0; i < _corners.Length; i++)
        {
            var center = CellCenter(i);
            foreach (var corner in _corners[i])
            {
                sum += center.DistanceTo(corner);
                count++;
            }
        }

        return count > 0 ? sum / count : 0.001f;
    }

    private void OnViewportResized()
    {
        if (!_viewAdjusted && _isoCorners.Length > 0)
        {
            FitView();
        }
        else
        {
            _center = GetViewportRect().Size * 0.5f;
        }

        QueueRedraw();
    }

    private static Vector2 Project(Vector3 v, Vector3 center, Vector3 east, Vector3 north)
    {
        var cos = v.Dot(center);
        if (cos < 0.2f)
            cos = 0.2f;

        return new Vector2(v.Dot(east) / cos, v.Dot(north) / cos);
    }

    private static (Vector3 East, Vector3 North) TangentBasis(Vector3 center)
    {
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
