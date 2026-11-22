using System;
using System.Diagnostics;
using System.Linq;
using Godot;
using H3;
using H3.Extensions;
using Sim.World;

/// <summary>
/// Experimental seamless-H3 LOD probe. Stage A of the standalone worktree:
/// one Res 3 parent is drawn at Res 3 (aggregate), Res 6 (shipped fine grid)
/// and Res 7 (derived detail), with a timed crossfade when camera zoom crosses
/// a tier boundary. Nothing here is authoritative simulation state.
/// </summary>
/// <remarks>
/// The tier meshes are derived at runtime with the same H3 library the bake
/// uses, so Stage A needs no new fixture, save schema or manifest field. Only
/// <c>Game/Scripts/</c> is touched; <c>Sim/</c> stays engine-free.
/// </remarks>
public partial class LodProbe : Node2D
{
    private const float CrossfadeSeconds = 0.22f;
    private const float MinZoom = 1f;
    private const float MaxZoom = 128f;
    private const float Tier1Zoom = 4f;
    private const float Tier2Zoom = 24f;

    private sealed class Tier
    {
        public required ArrayMesh Mesh { get; init; }
        public required MeshInstance2D Node { get; init; }
        public required int Cells { get; init; }
    }

    private BodyWorld _world = null!;
    private ulong _parentIndex;
    private Vector3 _center;
    private Vector3 _east;
    private Vector3 _north;

    private Tier[] _tiers = Array.Empty<Tier>();
    private ImageTexture _white = null!;

    private float _zoom = MinZoom;
    private float _sweepSeconds;
    private float _sweepTime;
    private int _currentTier;
    private int _fromTier;
    private float _fadeElapsed = CrossfadeSeconds;

    private bool _dragging;
    private Vector2 _lastMouse;
    private Vector2 _pan;
    private bool _holdFade;

    /// <summary>Current H3 resolution tier: 0 = Res 3, 1 = Res 6, 2 = Res 7.</summary>
    public int CurrentTier => _currentTier;

    /// <summary>Current logarithmic camera zoom.</summary>
    public float Zoom => _zoom;

    /// <summary>Number of H3 cells in one tier.</summary>
    public int CellCount(int tier) => _tiers[tier].Cells;

    /// <summary>Binds the probe to one region and builds all three tiers once.</summary>
    public void Initialize(
        BodyWorld world,
        ulong parentIndex,
        float zoom,
        float sweepSeconds,
        float holdFadeFraction = -1f)
    {
        _world = world;
        _parentIndex = parentIndex != 0 ? parentIndex : world.Regions[0].Index;
        _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        _sweepSeconds = Mathf.Max(0f, sweepSeconds);
        _currentTier = TierForZoom(_zoom);
        _fromTier = _currentTier;

        SetProcessUnhandledInput(true);
        BuildBasis();
        BuildWhiteTexture();
        BuildTiers();
        ApplyTransform();
        ApplyTier(_currentTier);

        GD.Print($"lod probe: parent={_parentIndex} " +
                 $"tiers={_tiers[0].Cells}/{_tiers[1].Cells}/{_tiers[2].Cells} " +
                 $"zoom={_zoom:F2}");
        GD.Print($"lod memory: working set {WorkingSetMegabytes()} MB after mesh build");

        if (holdFadeFraction >= 0f && _currentTier + 1 < _tiers.Length)
        {
            _fromTier = _currentTier;
            _currentTier++;
            _fadeElapsed = CrossfadeSeconds * Mathf.Clamp(holdFadeFraction, 0f, 1f);
            _holdFade = true;
            ApplyFade();
            GD.Print($"lod hold crossfade: {_fromTier}->{_currentTier} at {holdFadeFraction:F2}");
        }
    }

    public override void _Process(double delta)
    {
        if (_holdFade)
            return;

        if (_sweepSeconds > 0f)
        {
            _sweepTime += (float)delta;
            var progress = (_sweepTime % _sweepSeconds) / _sweepSeconds;
            SetZoom(MinZoom * Mathf.Pow(MaxZoom / MinZoom, progress));
        }

        if (_fadeElapsed >= CrossfadeSeconds)
            return;

        _fadeElapsed += (float)delta;
        ApplyFade();
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.03f, 0.05f, 1f));

        var tier = _currentTier;
        var resolution = tier == 0 ? 3 : tier == 1 ? 6 : 7;
        var text = $"LOD PROBE · Res {resolution} · zoom {_zoom:F1} · " +
                   $"{_tiers[tier].Cells} cells · wheel to zoom, right-drag to pan";
        if (_fadeElapsed < CrossfadeSeconds)
            text += $" · crossfade {_fromTier}→{tier}";

        DrawString(ThemeDB.FallbackFont, new Vector2(18f, 30f), text, fontSize: 15);
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton button:
                if (button.ButtonIndex == MouseButton.WheelUp && button.Pressed)
                    SetZoom(_zoom * 1.25f);
                else if (button.ButtonIndex == MouseButton.WheelDown && button.Pressed)
                    SetZoom(_zoom / 1.25f);
                else if (button.ButtonIndex == MouseButton.Right)
                {
                    _dragging = button.Pressed;
                    _lastMouse = button.Position;
                }
                break;

            case InputEventMouseMotion motion when _dragging:
                _pan += motion.Position - _lastMouse;
                _lastMouse = motion.Position;
                ApplyTransform();
                QueueRedraw();
                break;
        }
    }

    private void SetZoom(float value)
    {
        var next = Mathf.Clamp(value, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(next, _zoom))
            return;

        var target = TierForZoom(next);
        _zoom = next;
        if (target != _currentTier)
        {
            GD.Print($"lod crossfade: {_currentTier}->{target} at zoom {_zoom:F2} · " +
                     $"working set {WorkingSetMegabytes()} MB");
            _fromTier = _currentTier;
            _currentTier = target;
            _fadeElapsed = 0f;
        }

        ApplyTransform();
        if (_fadeElapsed >= CrossfadeSeconds)
            ApplyTier(_currentTier);
        QueueRedraw();
    }

    private static int TierForZoom(float zoom)
        => zoom < Tier1Zoom ? 0 : zoom < Tier2Zoom ? 1 : 2;

    private void BuildBasis()
    {
        var parent = new H3Index(_parentIndex);
        var boundary = parent.GetCellBoundaryVertices().ToList();
        var sum = Vector3.Zero;
        foreach (var vertex in boundary)
            sum += ToUnitVector(vertex);

        _center = sum.Normalized();
        _east = _center.Cross(Vector3.Up);
        if (_east.LengthSquared() < 1e-6f)
            _east = Vector3.Right;
        _east = _east.Normalized();
        _north = _east.Cross(_center).Normalized();
    }

    private void BuildWhiteTexture()
    {
        var image = Image.CreateEmpty(1, 1, false, Image.Format.Rgba8);
        image.Fill(Colors.White);
        _white = ImageTexture.CreateFromImage(image);
    }

    private void BuildTiers()
    {
        var parent = new H3Index(_parentIndex);
        var resolutions = new[] { 3, 6, 7 };
        _tiers = new Tier[resolutions.Length];

        for (var tier = 0; tier < resolutions.Length; tier++)
        {
            var cells = tier == 0
                ? new[] { parent }
                : parent.GetChildrenForResolution(resolutions[tier]).ToArray();
            var mesh = BuildMesh(cells, tier);
            var node = new MeshInstance2D
            {
                Mesh = mesh,
                Texture = _white,
                ZIndex = tier,
            };
            AddChild(node);
            _tiers[tier] = new Tier { Mesh = mesh, Node = node, Cells = cells.Length };
        }
    }

    private ArrayMesh BuildMesh(H3Index[] cells, int tier)
    {
        var vertices = new System.Collections.Generic.List<Vector3>(cells.Length * 18);
        var colors = new System.Collections.Generic.List<Color>(cells.Length * 18);
        var baseColor = ParentColor();

        for (var i = 0; i < cells.Length; i++)
        {
            var boundary = cells[i].GetCellBoundaryVertices().ToList();
            var sum = Vector3.Zero;
            foreach (var vertex in boundary)
                sum += ToUnitVector(vertex);

            var origin = Project(sum.Normalized());
            var fill = TierColor(baseColor, tier, i);

            for (var v = 0; v < boundary.Count; v++)
            {
                WriteVertex(origin, fill, vertices, colors);
                WriteVertex(Project(ToUnitVector(boundary[v])), fill, vertices, colors);
                WriteVertex(Project(ToUnitVector(boundary[(v + 1) % boundary.Count])), fill, vertices, colors);
            }
        }

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices.ToArray();
        arrays[(int)Mesh.ArrayType.Color] = colors.ToArray();

        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    private Color ParentColor()
    {
        foreach (var entry in _world.Regions)
        {
            if (entry.Index == _parentIndex)
                return BiomePalette.For(_world.Biomes[entry.DominantBiomeCode].Id);
        }

        return Colors.Gray;
    }

    private static Color TierColor(Color baseColor, int tier, int index)
    {
        // Deterministic presentation-only variation so the finer tiers remain
        // readable without pretending we have Res 6/7 biome samples.
        var hash = (uint)(index * 2654435761u + tier * 2246822519u);
        var factor = 0.86f + (hash & 0xFF) / 255f * 0.28f;
        return new Color(
            Mathf.Min(1f, baseColor.R * factor),
            Mathf.Min(1f, baseColor.G * factor),
            Mathf.Min(1f, baseColor.B * factor),
            1f);
    }

    private Vector2 Project(Vector3 unit)
    {
        var denominator = Mathf.Max(0.2f, unit.Dot(_center));
        return new Vector2(unit.Dot(_east), unit.Dot(_north)) / denominator;
    }

    private static void WriteVertex(
        Vector2 point,
        Color color,
        System.Collections.Generic.List<Vector3> vertices,
        System.Collections.Generic.List<Color> colors)
    {
        vertices.Add(new Vector3(point.X, point.Y, 0f));
        colors.Add(color);
    }

    private static Vector3 ToUnitVector(H3.Model.LatLng latLng)
    {
        var cosLat = Math.Cos(latLng.Latitude);
        return new Vector3(
            (float)(cosLat * Math.Cos(latLng.Longitude)),
            (float)Math.Sin(latLng.Latitude),
            (float)(cosLat * Math.Sin(latLng.Longitude)));
    }

    private void ApplyTransform()
    {
        var size = GetViewportRect().Size;
        var radius = Mathf.Min(size.X, size.Y) * 0.42f;
        var scale = new Vector2(radius * _zoom, -radius * _zoom);
        var origin = size * 0.5f + _pan;

        foreach (var tier in _tiers)
        {
            tier.Node.Position = origin;
            tier.Node.Scale = scale;
        }
    }

    private void ApplyTier(int active)
    {
        for (var i = 0; i < _tiers.Length; i++)
        {
            var visible = i == active;
            _tiers[i].Node.Visible = visible;
            _tiers[i].Node.Modulate = Colors.White;
        }
    }

    private void ApplyFade()
    {
        var t = Mathf.Clamp(_fadeElapsed / CrossfadeSeconds, 0f, 1f);
        var eased = t * t * (3f - 2f * t);

        for (var i = 0; i < _tiers.Length; i++)
        {
            var alpha = i == _fromTier ? 1f - eased : i == _currentTier ? eased : 0f;
            _tiers[i].Node.Visible = alpha > 0.001f;
            _tiers[i].Node.Modulate = new Color(1f, 1f, 1f, alpha);
        }

        if (_fadeElapsed >= CrossfadeSeconds)
            ApplyTier(_currentTier);
    }

    private static long WorkingSetMegabytes()
        => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
}
