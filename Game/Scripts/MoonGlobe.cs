using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using H3;
using H3.Extensions;
using Sim.World;

/// <summary>
/// Planetary map: an orthographic globe of a body's H3 regions, coloured by
/// dominant biome or mean imagery.
///
/// This is the navigation/site-selection view (design doc §2.6), so it is 2D:
/// every region's real H3 boundary is baked into one static mesh and rotated in
/// a canvas-item shader. Adjacent cells share boundary vectors, so the globe has
/// no seams; camera changes only update four shader uniforms.
/// </summary>
public partial class MoonGlobe : Node2D
{
    /// <summary>Hovered region index, or 0 when nothing is under the cursor.</summary>
    [Signal]
    public delegate void RegionHoveredEventHandler(ulong regionIndex, string biomeId, int hexCount);

    /// <summary>Region clicked by the player.</summary>
    [Signal]
    public delegate void RegionSelectedEventHandler(ulong regionIndex, string biomeId, int hexCount);

    private struct RegionShape
    {
        public Vector3 Centroid;
        public Color BiomeColor;
        public Color ImageryColor;
        public ulong Index;
        public int HexCount;
        public byte BiomeCode;
        public Vector3[] Boundary;
    }

    private const float MinZoom = 0.6f;
    private const float MaxZoom = 12f;
    private const float DragThresholdPixels = 5f;
    private const float MaxPitch = 1.35f;

    private BodyWorld _world = null!;
    private RegionShape[] _regions = Array.Empty<RegionShape>();
    private SurfaceImagery? _imagery;
    private bool _imageryEnabled;
    private ArrayMesh _cellMesh = null!;
    private MeshInstance2D _cells = null!;
    private ShaderMaterial _material = null!;
    private Color[] _vertexColors = Array.Empty<Color>();

    private Vector2 _center;
    private float _radius = 300f;
    private Vector2 _pan;

    private float _yaw = -Mathf.Pi * 0.5f; // face the near side (0° longitude) at startup
    private float _pitch = -0.25f;
    private float _zoom = 1f;

    private readonly Dictionary<ulong, int> _indexLookup = new();

    private int _hovered = -1;
    private int _selected = -1;

    private HashSet<ulong> _visitedMarkers = new();
    private HashSet<ulong> _builtMarkers = new();
    private ulong? _activeMarker;

    private bool _dragging;
    private bool _panning;
    private Vector2 _dragStart;
    private Vector2 _lastMouse;

    /// <summary>Dev/demo: rotate the globe automatically (used by <c>--spin</c>).</summary>
    public bool Spin { get; set; }

    /// <summary>Current yaw in degrees.</summary>
    public float YawDegrees => Mathf.RadToDeg(_yaw);

    /// <summary>Current pitch in degrees.</summary>
    public float PitchDegrees => Mathf.RadToDeg(_pitch);

    /// <summary>Current zoom factor.</summary>
    public float Zoom => _zoom;

    /// <summary>Unit vector at the centre of the current camera view.</summary>
    public Vector3 CameraFocusUnit => new(
        -Mathf.Sin(_yaw) * Mathf.Cos(_pitch),
        Mathf.Sin(_pitch),
        Mathf.Cos(_yaw) * Mathf.Cos(_pitch));

    /// <summary>Clamp applied to pitch when handing the camera back from the LOD view.</summary>
    public float MaxPitchRadians => MaxPitch;

    /// <summary>Draws visited, occupied and active markers on the globe.</summary>
    public void SetMarkers(
        IReadOnlyCollection<ulong> visited,
        IReadOnlyCollection<ulong> built,
        ulong? active)
    {
        _visitedMarkers = new HashSet<ulong>(visited);
        _builtMarkers = new HashSet<ulong>(built);
        _activeMarker = active;
        QueueRedraw();
    }

    /// <summary>Centres the camera on a region and selects it.</summary>
    public void FocusRegion(ulong regionIndex)
    {
        if (!_indexLookup.TryGetValue(regionIndex, out var index))
            return;

        var centroid = _regions[index].Centroid;
        _yaw = Mathf.Atan2(-centroid.X, centroid.Z);
        _pitch = Mathf.Clamp(
            Mathf.Asin(Mathf.Clamp(centroid.Y, -1f, 1f)), -MaxPitch, MaxPitch);
        _selected = index;
        _hovered = -1;

        UpdateShader();
        QueueRedraw();
    }

    /// <summary>Dev/demo camera: set the view for repeatable screenshots.</summary>
    public void SetView(float yawDegrees, float pitchDegrees, float zoom)
    {
        _yaw = Mathf.DegToRad(yawDegrees);
        _pitch = Mathf.DegToRad(pitchDegrees);
        _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        UpdateShader();
        QueueRedraw();
    }

    public int RegionCount => _regions.Length;

    /// <summary>True when the globe is currently showing per-region imagery means.</summary>
    public bool UsesImagery => _imagery is not null && _imageryEnabled;

    /// <summary>
    /// Binds optional imagery and precomputes one mean colour per region. The
    /// static cell mesh is recoloured once here and when the overlay toggles;
    /// camera changes never rebuild geometry.
    /// </summary>
    public void SetImagery(SurfaceImagery? imagery, bool enabled)
    {
        _imagery = imagery;
        _imageryEnabled = enabled && imagery is not null;

        for (var i = 0; i < _regions.Length; i++)
        {
            var entry = _world.Regions[i];
            _regions[i].ImageryColor = imagery is null
                ? _regions[i].BiomeColor
                : imagery.MeanFor(entry.Index);
        }

        ApplyCellColors();
        QueueRedraw();
    }

    /// <summary>Switches between surface imagery and analytical biome colours.</summary>
    public void SetImageryOverlay(bool enabled)
    {
        var next = enabled && _imagery is not null;
        if (next == _imageryEnabled)
            return;

        _imageryEnabled = next;
        ApplyCellColors();
        QueueRedraw();
    }

    public void Initialize(BodyWorld world)
    {
        _world = world;
        BuildShapes();
        BuildCells();
        UpdateViewportMetrics();
    }

    public override void _Ready()
    {
        GetViewport().SizeChanged += UpdateViewportMetrics;
        SetProcessUnhandledInput(true);
    }

    public override void _Process(double delta)
    {
        if (!Spin || !Visible)
            return;

        _yaw += (float)delta * 0.5f;
        UpdateShader();
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_regions.Length == 0)
            return;

        var cosYaw = Mathf.Cos(_yaw);
        var sinYaw = Mathf.Sin(_yaw);
        var cosPitch = Mathf.Cos(_pitch);
        var sinPitch = Mathf.Sin(_pitch);
        var scale = _radius * _zoom;
        var origin = _center + _pan;

        if (_selected >= 0 && _selected < _regions.Length)
            DrawOutline(_selected, Colors.White, 2.5f, scale, origin, cosYaw, sinYaw, cosPitch, sinPitch);

        if (_hovered >= 0 && _hovered != _selected && _hovered < _regions.Length)
            DrawOutline(_hovered, new Color(1f, 1f, 1f, 0.75f), 1.5f, scale, origin, cosYaw, sinYaw, cosPitch, sinPitch);

        DrawMarkers(scale, origin, cosYaw, sinYaw, cosPitch, sinPitch);

        // Limb: a soft edge so the globe reads as a sphere.
        DrawArc(origin, scale, 0f, Mathf.Tau, 128, new Color(1f, 1f, 1f, 0.12f), 1.5f, true);
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
            UpdateShader();
            QueueRedraw();
            return;
        }

        if (_dragging)
        {
            var delta = motion.Position - _lastMouse;
            _lastMouse = motion.Position;

            _yaw -= delta.X * 0.006f;
            _pitch = Mathf.Clamp(_pitch + delta.Y * 0.006f, -MaxPitch, MaxPitch);

            _hovered = -1;
            UpdateShader();
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

        // Keep the point under the cursor fixed while zooming.
        var origin = _center + _pan;
        var offset = anchor - origin;
        _pan += offset * (1f - _zoom / before);

        UpdateShader();
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
            EmitSignal(SignalName.RegionHovered, 0UL, string.Empty, 0);
            return;
        }

        var region = _regions[hit];
        var biome = _world.Biomes[region.BiomeCode];
        EmitSignal(SignalName.RegionHovered, region.Index, biome.Id, region.HexCount);
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
        var region = _regions[_selected];
        var biome = _world.Biomes[region.BiomeCode];
        EmitSignal(SignalName.RegionSelected, region.Index, biome.Id, region.HexCount);
        QueueRedraw();
    }

    private int Pick(Vector2 mouse)
    {
        var cosYaw = Mathf.Cos(_yaw);
        var sinYaw = Mathf.Sin(_yaw);
        var cosPitch = Mathf.Cos(_pitch);
        var sinPitch = Mathf.Sin(_pitch);
        var scale = _radius * _zoom;
        var origin = _center + _pan;
        var maxDistance = Mathf.Max(10f, scale * 0.03f);
        var maxDistanceSquared = maxDistance * maxDistance;

        var best = -1;
        var bestDistance = float.MaxValue;

        for (var i = 0; i < _regions.Length; i++)
        {
            Rotate(_regions[i].Centroid, cosYaw, sinYaw, cosPitch, sinPitch, out var x, out var y, out var z);
            if (z <= 0.02f)
                continue;

            var point = new Vector2(origin.X + x * scale, origin.Y - y * scale);
            var distance = point.DistanceSquaredTo(mouse);
            if (distance < bestDistance && distance <= maxDistanceSquared)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    private void UpdateShader()
    {
        if (_material is null)
            return;

        _material.SetShaderParameter("yaw", _yaw);
        _material.SetShaderParameter("pitch", _pitch);
        _material.SetShaderParameter("globe_center", _center + _pan);
        _material.SetShaderParameter("globe_scale", _radius * _zoom);
    }

    private void DrawMarkers(
        float scale,
        Vector2 origin,
        float cosYaw,
        float sinYaw,
        float cosPitch,
        float sinPitch)
    {
        if (_visitedMarkers.Count == 0 && _builtMarkers.Count == 0 && _activeMarker is null)
            return;

        // An occupied region is drawn first and larger: it must be findable at a
        // glance in every globe overlay, even when the terrain is unremarkable.
        foreach (var regionIndex in _builtMarkers)
        {
            if (!_indexLookup.TryGetValue(regionIndex, out var index))
                continue;

            if (!TryProjectCentroid(index, scale, origin, cosYaw, sinYaw, cosPitch, sinPitch, out var point))
                continue;

            DrawCircle(point, 4.2f, new Color(0.25f, 1f, 0.55f, 0.28f));
            DrawArc(point, 6.5f, 0f, Mathf.Tau, 20, new Color(0.35f, 1f, 0.6f, 0.95f), 1.8f, true);
            DrawCircle(point, 1.6f, new Color(0.75f, 1f, 0.82f, 1f));
        }

        foreach (var regionIndex in _visitedMarkers)
        {
            if (_builtMarkers.Contains(regionIndex))
                continue;
            if (!_indexLookup.TryGetValue(regionIndex, out var index))
                continue;

            if (!TryProjectCentroid(index, scale, origin, cosYaw, sinYaw, cosPitch, sinPitch, out var point))
                continue;

            DrawCircle(point, 2.2f, new Color(0.55f, 0.85f, 1f, 0.9f));
        }

        if (_activeMarker is { } active
            && _indexLookup.TryGetValue(active, out var activeIndex)
            && TryProjectCentroid(activeIndex, scale, origin, cosYaw, sinYaw, cosPitch, sinPitch, out var activePoint))
        {
            DrawCircle(activePoint, 3.2f, new Color(1f, 0.86f, 0.35f, 1f));
            DrawArc(activePoint, 7f, 0f, Mathf.Tau, 20, new Color(1f, 0.86f, 0.35f, 0.9f), 1.5f, true);
        }
    }

    private bool TryProjectCentroid(
        int index,
        float scale,
        Vector2 origin,
        float cosYaw,
        float sinYaw,
        float cosPitch,
        float sinPitch,
        out Vector2 point)
    {
        Rotate(_regions[index].Centroid, cosYaw, sinYaw, cosPitch, sinPitch, out var x, out var y, out var z);

        if (z <= 0.02f)
        {
            point = default;
            return false;
        }

        point = new Vector2(origin.X + x * scale, origin.Y - y * scale);
        return true;
    }

    private void DrawOutline(
        int index,
        Color color,
        float width,
        float scale,
        Vector2 origin,
        float cosYaw,
        float sinYaw,
        float cosPitch,
        float sinPitch)
    {
        var boundary = _regions[index].Boundary;
        var points = new Vector2[boundary.Length + 1];

        for (var i = 0; i < boundary.Length; i++)
        {
            Rotate(boundary[i], cosYaw, sinYaw, cosPitch, sinPitch, out var x, out var y, out _);
            points[i] = new Vector2(origin.X + x * scale, origin.Y - y * scale);
        }

        points[^1] = points[0];
        DrawPolyline(points, color, width, true);
    }

    private void BuildShapes()
    {
        var entries = _world.Regions;
        var shapes = new RegionShape[entries.Count];

        for (var i = 0; i < entries.Count; i++)
        {
            var entry = entries[i];
            var cell = new H3Index(entry.Index);
            var boundary = cell.GetCellBoundaryVertices().ToList();

            var vectors = new Vector3[boundary.Count];
            var sum = Vector3.Zero;

            for (var v = 0; v < boundary.Count; v++)
            {
                vectors[v] = ToUnitVector(boundary[v]);
                sum += vectors[v];
            }

            shapes[i] = new RegionShape
            {
                Boundary = vectors,
                Centroid = sum.Normalized(),
                BiomeColor = BiomePalette.For(_world.Biomes[entry.DominantBiomeCode].Id),
                ImageryColor = BiomePalette.For(_world.Biomes[entry.DominantBiomeCode].Id),
                Index = entry.Index,
                HexCount = entry.HexCount,
                BiomeCode = entry.DominantBiomeCode,
            };
        }

        _regions = shapes;

        _indexLookup.Clear();
        for (var i = 0; i < shapes.Length; i++)
            _indexLookup[shapes[i].Index] = i;
    }

    private void BuildCells()
    {
        _material = new ShaderMaterial
        {
            Shader = GD.Load<Shader>("res://Shaders/GlobeCells.gdshader"),
        };

        _cells = new MeshInstance2D
        {
            // Behind the parent's outlines, markers and limb arc.
            ZIndex = -1,
            Material = _material,
            Texture = WhiteTexture(),
        };

        AddChild(_cells);

        // Build through the same path used by imagery/biome toggles, so the
        // cached mesh field is always populated before the first recolour.
        ApplyCellColors();
        UpdateShader();
    }

    private void ApplyCellColors()
    {
        if (_cells is null)
            return;

        var previous = _cellMesh;
        var source = UsesImagery ? "imagery" : "biome";
        _cellMesh = BuildCellMesh();
        _cells.Mesh = _cellMesh;
        _cells.QueueRedraw();
        previous?.Dispose();
        GD.Print($"globe cell colours: {source} · {_vertexColors.Length} vertices");
    }

    private ArrayMesh BuildCellMesh()
    {
        var vertexCount = 0;
        foreach (var region in _regions)
            vertexCount += region.Boundary.Length * 3;

        var vertices = new Vector3[vertexCount];
        var uvs = new Vector2[vertexCount];
        _vertexColors = new Color[vertexCount];

        var cursor = 0;
        for (var i = 0; i < _regions.Length; i++)
        {
            var region = _regions[i];
            var color = UsesImagery ? region.ImageryColor : region.BiomeColor;
            var boundary = region.Boundary;

            for (var v = 0; v < boundary.Length; v++)
            {
                WriteCellVertex(region.Centroid, color, vertices, uvs, _vertexColors, ref cursor);
                WriteCellVertex(boundary[v], color, vertices, uvs, _vertexColors, ref cursor);
                WriteCellVertex(
                    boundary[(v + 1) % boundary.Length], color, vertices, uvs, _vertexColors, ref cursor);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Color] = _vertexColors;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private static void WriteCellVertex(
        Vector3 unit,
        Color color,
        Vector3[] vertices,
        Vector2[] uvs,
        Color[] colors,
        ref int cursor)
    {
        // CanvasItem mesh vertices are 2D; UV.x carries the sphere's Z so the
        // shader can rotate the original 3D vector without extra attributes.
        vertices[cursor] = new Vector3(unit.X, unit.Y, 0f);
        uvs[cursor] = new Vector2(unit.Z, 0f);
        colors[cursor] = color;
        cursor++;
    }

    private static Texture2D WhiteTexture()
    {
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        return ImageTexture.CreateFromImage(image);
    }

    private static Vector3 ToUnitVector(H3.Model.LatLng latLng)
    {
        var cosLat = Math.Cos(latLng.Latitude);
        return new Vector3(
            (float)(cosLat * Math.Cos(latLng.Longitude)),
            (float)Math.Sin(latLng.Latitude),
            (float)(cosLat * Math.Sin(latLng.Longitude)));
    }

    private static void Rotate(
        Vector3 v,
        float cosYaw,
        float sinYaw,
        float cosPitch,
        float sinPitch,
        out float x,
        out float y,
        out float z)
    {
        // Yaw around the polar axis, then pitch towards the camera.
        var x1 = v.X * cosYaw + v.Z * sinYaw;
        var z1 = -v.X * sinYaw + v.Z * cosYaw;

        y = v.Y * cosPitch - z1 * sinPitch;
        z = v.Y * sinPitch + z1 * cosPitch;

        // Screen X is deliberately inverted: the world uses east-positive
        // longitude, but the camera looks down +z1, so x1 grows towards the
        // west. Flipping here keeps drawing, picking and FocusRegion on the
        // same projection and makes west/left and east/right agree with maps.
        x = -x1;
    }

    private void UpdateViewportMetrics()
    {
        var size = GetViewportRect().Size;
        _center = size * 0.5f;
        _radius = Mathf.Min(size.X, size.Y) * 0.42f;

        UpdateShader();
        QueueRedraw();
    }
}
