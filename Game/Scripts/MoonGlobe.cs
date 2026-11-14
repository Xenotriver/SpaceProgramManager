using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using H3;
using H3.Extensions;
using Sim.World;

/// <summary>
/// Planetary map: an orthographic globe of a body's regions, one flat hex per
/// region, coloured by dominant biome.
///
/// This is the navigation/site-selection view (design doc §2.6), so it is 2D:
/// every region is projected onto the sphere, rotated, and drawn as one instance
/// of a shared hex mesh. The whole globe is a single MultiMesh draw call; instance
/// transforms are only rebuilt when the camera or the hover changes.
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
        public Color Color;
        public ulong Index;
        public int HexCount;
        public byte BiomeCode;
        public Vector3[] Boundary;
    }

    private const float MinZoom = 0.6f;
    private const float MaxZoom = 12f;
    private const float DragThresholdPixels = 5f;
    private const float MaxPitch = 1.35f;
    private const float SeamFudge = 1.04f;

    private BodyWorld _world = null!;
    private RegionShape[] _regions = Array.Empty<RegionShape>();
    private MultiMesh _multimesh = null!;
    private MultiMeshInstance2D _instances = null!;
    private float _hexRadiusRadians;

    private Vector2 _center;
    private float _radius = 300f;
    private Vector2 _pan;

    private float _yaw = -Mathf.Pi * 0.5f; // face the near side (0° longitude) at startup
    private float _pitch = -0.25f;
    private float _zoom = 1f;

    private int _hovered = -1;
    private int _selected = -1;

    private bool _dragging;
    private bool _panning;
    private Vector2 _dragStart;
    private Vector2 _lastMouse;

    /// <summary>Dev/demo: rotate the globe automatically (used by <c>--spin</c>).</summary>
    public bool Spin { get; set; }

    /// <summary>Dev/demo camera: set the view for repeatable screenshots.</summary>
    public void SetView(float yawDegrees, float pitchDegrees, float zoom)
    {
        _yaw = Mathf.DegToRad(yawDegrees);
        _pitch = Mathf.DegToRad(pitchDegrees);
        _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        UpdateInstances();
        QueueRedraw();
    }

    public int RegionCount => _regions.Length;

    public void Initialize(BodyWorld world)
    {
        _world = world;
        BuildShapes();
        BuildMultiMesh();
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
        UpdateInstances();
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
            UpdateInstances();
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
            UpdateInstances();
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

        UpdateInstances();
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

    private void UpdateInstances()
    {
        if (_multimesh is null || _regions.Length == 0)
            return;

        var cosYaw = Mathf.Cos(_yaw);
        var sinYaw = Mathf.Sin(_yaw);
        var cosPitch = Mathf.Cos(_pitch);
        var sinPitch = Mathf.Sin(_pitch);
        var scale = _radius * _zoom;
        var origin = _center + _pan;
        var hexRadius = _hexRadiusRadians * scale * SeamFudge;
        var collapsed = new Transform2D(0f, Vector2.Zero, 0f, Vector2.Zero);

        for (var i = 0; i < _regions.Length; i++)
        {
            Rotate(_regions[i].Centroid, cosYaw, sinYaw, cosPitch, sinPitch, out var x, out var y, out var z);

            if (z <= 0.02f)
            {
                // Back of the globe: collapse the instance (zero scale).
                _multimesh.SetInstanceTransform2D(i, collapsed);
                continue;
            }

            // Compress along the radial direction to fake foreshortening at the limb.
            var angle = Mathf.Atan2(-y, x);
            var radiusX = hexRadius * Mathf.Max(z, 0.15f);

            _multimesh.SetInstanceTransform2D(
                i,
                new Transform2D(angle, new Vector2(radiusX, hexRadius), 0f,
                    new Vector2(origin.X + x * scale, origin.Y - y * scale)));
        }
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
                Color = BiomePalette.For(_world.Biomes[entry.DominantBiomeCode].Id),
                Index = entry.Index,
                HexCount = entry.HexCount,
                BiomeCode = entry.DominantBiomeCode,
            };
        }

        _regions = shapes;

        // Res-3 cells are near-equal-area, so one hex size fits all: derive the
        // angular circumradius from the number of cells on the sphere.
        _hexRadiusRadians = Mathf.Sqrt(4f * Mathf.Pi / shapes.Length / 2.598f);
    }

    private void BuildMultiMesh()
    {
        var white = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        white.Fill(Colors.White);
        var texture = ImageTexture.CreateFromImage(white);

        _multimesh = new MultiMesh
        {
            TransformFormat = MultiMesh.TransformFormatEnum.Transform2D,
            UseColors = true,
            Mesh = BuildHexMesh(),
            InstanceCount = _regions.Length,
        };

        // Colours never change: upload them once. Transforms follow the camera.
        for (var i = 0; i < _regions.Length; i++)
            _multimesh.SetInstanceColor(i, _regions[i].Color);

        _instances = new MultiMeshInstance2D
        {
            Multimesh = _multimesh,
            Texture = texture,
        };

        AddChild(_instances);
        UpdateInstances();
    }

    private static ArrayMesh BuildHexMesh()
    {
        // Flat-top hexagon with circumradius 1, as a triangle fan.
        var vertices = new Vector2[18];
        var colors = new Color[18];

        for (var i = 0; i < 6; i++)
        {
            var angle = Mathf.Pi / 3f * i;
            var current = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
            var nextAngle = Mathf.Pi / 3f * (i + 1);
            var next = new Vector2(Mathf.Cos(nextAngle), Mathf.Sin(nextAngle));

            vertices[i * 3] = Vector2.Zero;
            vertices[i * 3 + 1] = current;
            vertices[i * 3 + 2] = next;
        }

        Array.Fill(colors, Colors.White);

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Color] = colors;

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
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
        x = x1;
    }

    private void UpdateViewportMetrics()
    {
        var size = GetViewportRect().Size;
        _center = size * 0.5f;
        _radius = Mathf.Min(size.X, size.Y) * 0.42f;

        UpdateInstances();
        QueueRedraw();
    }
}
