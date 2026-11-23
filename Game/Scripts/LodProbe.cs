using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using H3;
using H3.Algorithms;
using H3.Extensions;
using H3.Model;
using Sim.World;

/// <summary>
/// Experimental seamless-H3 LOD probe. D50 iteration of the standalone worktree.
///
/// Five tiers (Res 3/4/5/6/7) are selected automatically from camera zoom with
/// threshold hysteresis. A core of Res 3 parents is drawn at the selected tier;
/// one outer ring is drawn one tier coarser so the density change moves away
/// from the focus point. The core radius is tier-dependent and capped by the
/// measured one-shot build budget. Tier builds are queued and drained against a
/// per-frame millisecond budget so a boundary crossing cannot spike a frame.
///
/// The coarse tier stays opaque underneath while the finer tier fades over it,
/// which avoids the mid-crossfade brightness dip of a two-way cross-dissolve.
/// Every tier is projected through one fixed gnomonic basis, so a boundary
/// shared by two parents maps to the same screen polyline regardless of tier.
///
/// Nothing here is authoritative simulation state: no Sim, save, manifest or
/// fingerprint changes are involved.
/// </summary>
public partial class LodProbe : Node2D
{
    private const float CrossfadeSeconds = 0.22f;
    private const float MinZoom = 1f;
    private const float MaxZoom = 128f;
    private const float Hysteresis = 0.95f;
    private const float LoadBudgetMs = 8f;
    private const int ParentCapacity = 96;

    private static readonly int[] TierResolutions = { 3, 4, 5, 6, 7 };

    // Radius in Res 3 parent hexes. Chosen so a full core rebuild stays under
    // the 100 ms tile-load target: Res 4/5 use 61 parents, Res 6 uses 19 and
    // Res 7 uses 7. The outer ring is one tier coarser.
    private static readonly int[] ParentRadii = { 4, 4, 4, 2, 1 };

    private static readonly float[] UpThresholds = BuildUpThresholds();
    private static readonly float[] DownThresholds =
        UpThresholds.Select(threshold => threshold * Hysteresis).ToArray();

    private readonly record struct TierBuild(ParentTile Tile, int Tier);

    private sealed class TierMesh
    {
        public required ArrayMesh Mesh { get; init; }
        public required MeshInstance2D Node { get; init; }
        public required int Cells { get; init; }
        public required long BuildMs { get; init; }
    }

    private sealed class ParentTile
    {
        public required H3Index Index { get; init; }
        public required Color BaseColor { get; init; }
        public TierMesh?[] Tiers { get; } = new TierMesh?[TierResolutions.Length];
        public int CurrentTier { get; set; } = -1;
        public int DesiredTier { get; set; } = -1;
        public int FromTier { get; set; } = -1;
        public float FadeElapsed { get; set; } = CrossfadeSeconds;
        public ulong LastUsed { get; set; }
        public bool Wanted { get; set; }
    }

    private BodyWorld _world = null!;
    private H3Index _anchor;
    private Vector3 _center;
    private Vector3 _east;
    private Vector3 _north;
    private ImageTexture _white = null!;

    private readonly Dictionary<H3Index, ParentTile> _tiles = new();
    private readonly Queue<TierBuild> _buildQueue = new();
    private readonly HashSet<(H3Index, int)> _queued = new();
    private ulong _clock;
    private H3Index _focus;
    private int _focusTier;

    private float _zoom = MinZoom;
    private float _sweepSeconds;
    private float _sweepTime;
    private float _stressSeconds;
    private float _stressTime;
    private float _logTime;

    private bool _dragging;
    private Vector2 _lastMouse;
    private Vector2 _pan;
    private bool _holdFade;

    private int _loads;
    private int _evictions;
    private int _focusChanges;
    private long _lastLoadMs;
    private long _maxLoadMs;
    private long _maxWorkingSetMb;

    /// <summary>Current focus tier: 0 = Res 3 … 4 = Res 7.</summary>
    public int CurrentTier => _focusTier;

    /// <summary>Current logarithmic camera zoom.</summary>
    public float Zoom => _zoom;

    /// <summary>Cells in the focus parent's currently selected tier.</summary>
    public int CellCount(int tier) => _tiles[_focus].Tiers[tier]?.Cells ?? 0;

    /// <summary>Queued tier builds not yet submitted to the GPU.</summary>
    public int PendingBuilds => _buildQueue.Count;

    /// <summary>Binds the probe to one region and builds the initial focus neighbourhood.</summary>
    public void Initialize(
        BodyWorld world,
        ulong parentIndex,
        float zoom,
        float sweepSeconds,
        float holdFadeFraction = -1f,
        float stressSeconds = 0f,
        float panX = 0f,
        float panY = 0f)
    {
        _world = world;
        _anchor = parentIndex != 0 ? new H3Index(parentIndex) : new H3Index(world.Regions[0].Index);
        _zoom = Mathf.Clamp(zoom, MinZoom, MaxZoom);
        _sweepSeconds = Mathf.Max(0f, sweepSeconds);
        _stressSeconds = Mathf.Max(0f, stressSeconds);
        _pan = new Vector2(panX, panY);

        SetProcessUnhandledInput(true);
        BuildBasis(_anchor);
        _focus = CellFor(CameraGround());
        _focusTier = TierForZoom(_zoom, -1);
        BuildWhiteTexture();
        RefreshTiles(immediate: true);
        ApplyTransforms();
        TrackMemory();

        GD.Print($"lod probe: anchor={_anchor} focus={_focus} zoom={_zoom:F2} " +
                 $"tiers={TierChain()} focusTier={_focusTier} " +
                 $"radius={ParentRadii[_focusTier]} tiles={_tiles.Count} pan={_pan}");
        GD.Print($"lod memory: working set {WorkingSetMegabytes()} MB after initial build; " +
                 $"loads={_loads} maxLoad={_maxLoadMs} ms");

        if (holdFadeFraction >= 0f)
            HoldFocusCrossfade(holdFadeFraction);
    }

    public override void _Process(double delta)
    {
        if (_holdFade)
            return;

        var step = (float)delta;

        if (_sweepSeconds > 0f)
        {
            _sweepTime += step;
            var progress = (_sweepTime % _sweepSeconds) / _sweepSeconds;
            SetZoom(MinZoom * Mathf.Pow(MaxZoom / MinZoom, progress));
        }

        if (_stressSeconds > 0f)
        {
            _stressTime += step;
            var progress = (_stressTime % _stressSeconds) / _stressSeconds;
            var angle = progress * Mathf.Tau;
            var radius = Mathf.Min(GetViewportRect().Size.X, GetViewportRect().Size.Y) * 0.42f;
            var amplitude = radius * 0.8f;
            _pan = new Vector2(
                Mathf.Sin(angle) * amplitude,
                Mathf.Sin(angle * 1.3f + 0.7f) * amplitude * 0.7f);
            UpdateFocus();
            ApplyTransforms();

            _logTime += step;
            if (_logTime >= 1f)
            {
                _logTime = 0f;
                TrackMemory();
                GD.Print($"lod stress: t={_stressTime:F1}s focus={_focus} tier={_focusTier} " +
                         $"radius={ParentRadii[_focusTier]} tiles={_tiles.Count} " +
                         $"queue={_buildQueue.Count} loads={_loads} evictions={_evictions} " +
                         $"focusChanges={_focusChanges} maxLoad={_maxLoadMs}ms " +
                         $"workingSet={WorkingSetMegabytes()}MB");
            }
        }

        if (ProcessBuildQueue())
        {
            ApplyTransforms();
            QueueRedraw();
        }

        var fading = false;
        foreach (var tile in _tiles.Values)
        {
            if (tile.FromTier == tile.CurrentTier || tile.FadeElapsed >= CrossfadeSeconds)
                continue;
            tile.FadeElapsed += step;
            if (tile.FadeElapsed >= CrossfadeSeconds)
                tile.FromTier = tile.CurrentTier;
            ApplyTileAppearance(tile);
            fading = true;
        }

        if (fading)
            QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        DrawRect(new Rect2(Vector2.Zero, size), new Color(0.02f, 0.03f, 0.05f, 1f));

        var focus = _tiles[_focus];
        var resolution = TierResolutions[_focusTier];
        var text = $"LOD PROBE · focus {_focus} · tier {_focusTier} (Res {resolution}) · " +
                   $"{focus.Tiers[_focusTier]?.Cells ?? 0} cells · radius {ParentRadii[_focusTier]} · " +
                   $"{_tiles.Count} parents · queue {_buildQueue.Count} · zoom {_zoom:F1} · " +
                   $"loads {_loads} / evictions {_evictions} / max {_maxLoadMs} ms · " +
                   "wheel zoom, right-drag pan";
        if (focus.FromTier != focus.CurrentTier && focus.FadeElapsed < CrossfadeSeconds)
            text += $" · crossfade {focus.FromTier}→{focus.CurrentTier}";

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
                UpdateFocus();
                ApplyTransforms();
                QueueRedraw();
                break;
        }
    }

    private void SetZoom(float value)
    {
        var next = Mathf.Clamp(value, MinZoom, MaxZoom);
        if (Mathf.IsEqualApprox(next, _zoom))
            return;

        var tier = TierForZoom(next, _focusTier);
        _zoom = next;
        if (tier != _focusTier)
            GD.Print($"lod tier: {_focusTier}->{tier} at zoom {_zoom:F2} · " +
                     $"radius {ParentRadii[tier]} · working set {WorkingSetMegabytes()} MB");
        UpdateFocus();
        ApplyTransforms();
        QueueRedraw();
    }

    private static int TierForZoom(float zoom, int currentTier)
    {
        if (currentTier < 0)
        {
            var tier = 0;
            while (tier < UpThresholds.Length && zoom >= UpThresholds[tier])
                tier++;
            return tier;
        }

        var next = Mathf.Clamp(currentTier, 0, TierResolutions.Length - 1);
        while (next < UpThresholds.Length && zoom >= UpThresholds[next])
            next++;
        while (next > 0 && zoom < DownThresholds[next - 1])
            next--;
        return next;
    }

    private static float[] BuildUpThresholds()
    {
        var thresholds = new float[TierResolutions.Length - 1];
        for (var i = 0; i < thresholds.Length; i++)
            thresholds[i] = MinZoom * Mathf.Pow(MaxZoom / MinZoom, (i + 1f) / TierResolutions.Length);
        return thresholds;
    }

    private static string TierChain()
        => string.Join("/", TierResolutions);

    private void UpdateFocus()
    {
        var cell = CellFor(CameraGround());
        var tier = TierForZoom(_zoom, _focusTier);
        if (cell == _focus && tier == _focusTier)
            return;

        if (cell != _focus)
        {
            _focus = cell;
            _focusChanges++;
            if (_focusChanges % 10 == 0)
                GD.Print($"lod focus: now {cell} (#{_focusChanges})");
        }

        _focusTier = tier;
        RefreshTiles();
    }

    private void RefreshTiles(bool immediate = false)
    {
        foreach (var tile in _tiles.Values)
            tile.Wanted = false;

        var radius = ParentRadii[_focusTier];
        var extent = radius + (_focusTier > 0 ? 1 : 0);

        foreach (var entry in _focus.GridDiskDistances(extent))
        {
            if (entry.Distance > extent)
                continue;

            var cell = entry.Index;
            if (!_tiles.TryGetValue(cell, out var tile))
            {
                tile = new ParentTile { Index = cell, BaseColor = ColorFor(cell) };
                _tiles.Add(cell, tile);
            }

            var tier = entry.Distance <= radius
                ? _focusTier
                : Math.Max(0, _focusTier - 1);
            tile.Wanted = true;
            tile.LastUsed = ++_clock;
            tile.DesiredTier = tier;

            if (tile.CurrentTier >= 0 && tile.CurrentTier != tier)
                QueueTierBuild(tile, tile.CurrentTier);
            QueueTierBuild(tile, tier);
        }

        // Keep recently used parents around so a short pan-back is a cache hit.
        // Only overflow eviction is needed; wanted parents are never victims.
        while (_tiles.Count > ParentCapacity)
        {
            var victim = _tiles.Values
                .Where(t => !t.Wanted)
                .OrderBy(t => t.LastUsed)
                .FirstOrDefault()
                ?? _tiles.Values
                    .Where(t => t.Index != _focus)
                    .OrderBy(t => t.LastUsed)
                    .FirstOrDefault();
            if (victim is null)
                break;
            Evict(victim);
        }

        if (immediate)
            ProcessBuildQueue(unlimited: true);
        TrackMemory();
    }

    private void QueueTierBuild(ParentTile tile, int tier)
    {
        if (tier < 0 || tier >= TierResolutions.Length)
            return;
        if (tile.Tiers[tier] is not null)
            return;
        if (!_queued.Add((tile.Index, tier)))
            return;
        _buildQueue.Enqueue(new TierBuild(tile, tier));
    }

    private bool ProcessBuildQueue(bool unlimited = false)
    {
        if (_buildQueue.Count == 0)
            return false;

        var built = false;
        var stopwatch = Stopwatch.StartNew();
        while (_buildQueue.Count > 0)
        {
            var request = _buildQueue.Dequeue();
            _queued.Remove((request.Tile.Index, request.Tier));

            if (!request.Tile.Wanted || !_tiles.ContainsKey(request.Tile.Index))
                continue;
            if (request.Tier != request.Tile.DesiredTier && request.Tier != request.Tile.CurrentTier)
                continue;

            EnsureTier(request.Tile, request.Tier);
            built = true;
            MaybeStartFade(request.Tile);

            if (!unlimited && stopwatch.Elapsed.TotalMilliseconds >= LoadBudgetMs)
                break;
        }
        return built;
    }

    private void MaybeStartFade(ParentTile tile)
    {
        var target = tile.DesiredTier;
        if (target < 0 || tile.Tiers[target] is null)
            return;

        if (tile.CurrentTier < 0)
        {
            tile.CurrentTier = target;
            tile.FromTier = target;
            tile.FadeElapsed = CrossfadeSeconds;
            ApplyTileAppearance(tile);
            return;
        }

        if (tile.CurrentTier == target)
        {
            if (tile.FromTier != tile.CurrentTier && tile.FadeElapsed >= CrossfadeSeconds)
            {
                tile.FromTier = tile.CurrentTier;
                ApplyTileAppearance(tile);
            }
            return;
        }

        // A zoom sweep can interrupt a fade. Continue from the visually dominant
        // tier instead of restarting from a mesh that is almost transparent.
        var from = VisibleTier(tile);
        if (tile.Tiers[from] is null)
        {
            QueueTierBuild(tile, from);
            return;
        }
        if (from == target)
        {
            tile.CurrentTier = target;
            tile.FromTier = target;
            tile.FadeElapsed = CrossfadeSeconds;
            ApplyTileAppearance(tile);
            return;
        }

        tile.FromTier = from;
        tile.CurrentTier = target;
        tile.FadeElapsed = 0f;
        ApplyTileAppearance(tile);
        if (tile.Index == _focus)
            GD.Print($"lod crossfade: focus {from}->{target} " +
                     $"cells={tile.Tiers[target]?.Cells ?? 0} res={TierResolutions[target]}");
    }

    private static int VisibleTier(ParentTile tile)
    {
        if (tile.FromTier == tile.CurrentTier || tile.FadeElapsed >= CrossfadeSeconds)
            return tile.CurrentTier;

        var t = Mathf.Clamp(tile.FadeElapsed / CrossfadeSeconds, 0f, 1f);
        var eased = t * t * (3f - 2f * t);
        var overAlpha = tile.FromTier < tile.CurrentTier ? eased : 1f - eased;
        return overAlpha >= 0.5f
            ? Math.Max(tile.FromTier, tile.CurrentTier)
            : Math.Min(tile.FromTier, tile.CurrentTier);
    }

    private void EnsureTier(ParentTile tile, int tier)
    {
        if (tile.Tiers[tier] is not null)
            return;

        var stopwatch = Stopwatch.StartNew();
        var cells = tier == 0
            ? new[] { tile.Index }
            : tile.Index.GetChildrenForResolution(TierResolutions[tier]).ToArray();
        var mesh = BuildMesh(cells, tier, tile.BaseColor);
        var node = new MeshInstance2D
        {
            Mesh = mesh,
            Texture = _white,
            ZIndex = 10 + tier,
        };
        AddChild(node);

        stopwatch.Stop();
        tile.Tiers[tier] = new TierMesh
        {
            Mesh = mesh,
            Node = node,
            Cells = cells.Length,
            BuildMs = stopwatch.ElapsedMilliseconds,
        };
        _loads++;
        _lastLoadMs = stopwatch.ElapsedMilliseconds;
        _maxLoadMs = Math.Max(_maxLoadMs, _lastLoadMs);
        if (tier > 0 || _lastLoadMs >= 5 || _loads <= 7)
            GD.Print($"lod load: parent={tile.Index} tier={tier} res={TierResolutions[tier]} " +
                     $"cells={cells.Length} ms={_lastLoadMs}");
    }

    private void Evict(ParentTile tile)
    {
        foreach (var tier in tile.Tiers)
        {
            if (tier is null)
                continue;
            tier.Node.Mesh = null;
            tier.Node.QueueFree();
            tier.Mesh.Dispose();
        }

        _tiles.Remove(tile.Index);
        _evictions++;
        if (_evictions % 10 == 0)
            GD.Print($"lod evict: parent={tile.Index} (#{_evictions}) tiles={_tiles.Count}");
    }

    private ArrayMesh BuildMesh(H3Index[] cells, int tier, Color baseColor)
    {
        var vertices = new List<Vector3>(cells.Length * 18);
        var colors = new List<Color>(cells.Length * 18);

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

    private Color ColorFor(H3Index cell)
    {
        foreach (var entry in _world.Regions)
        {
            if (new H3Index(entry.Index) == cell)
                return BiomePalette.For(_world.Biomes[entry.DominantBiomeCode].Id);
        }

        return Colors.Gray;
    }

    private static Color TierColor(Color baseColor, int tier, int index)
    {
        // Tier 0 is the authoritative parent colour. Finer tiers only carry a
        // small deterministic brightness variation so they stay readable
        // without pretending we have Res 4-7 biome samples.
        if (tier == 0)
            return baseColor;

        var hash = (uint)(index * 2654435761u + tier * 2246822519u);
        var factor = 0.95f + (hash & 0xFF) / 255f * 0.10f;
        return new Color(
            Mathf.Min(1f, baseColor.R * factor),
            Mathf.Min(1f, baseColor.G * factor),
            Mathf.Min(1f, baseColor.B * factor),
            1f);
    }

    private void BuildBasis(H3Index anchor)
    {
        var boundary = anchor.GetCellBoundaryVertices().ToList();
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

    // One fixed gnomonic basis for every parent: H3 child vertices on a shared
    // parent edge are the same points for both sides, so they project to the
    // same screen positions and there is no seam between differing tiers.
    private Vector2 Project(Vector3 unit)
    {
        var denominator = Mathf.Max(0.2f, unit.Dot(_center));
        return new Vector2(unit.Dot(_east), unit.Dot(_north)) / denominator;
    }

    private Vector3 CameraGround()
    {
        var size = GetViewportRect().Size;
        var radius = Mathf.Min(size.X, size.Y) * 0.42f;
        var x = -_pan.X / (radius * _zoom);
        var y = _pan.Y / (radius * _zoom);
        return (_center + _east * x + _north * y).Normalized();
    }

    private static H3Index CellFor(Vector3 unit)
    {
        var latitude = (float)Math.Asin(Math.Clamp(unit.Y, -1f, 1f));
        var longitude = (float)Math.Atan2(unit.Z, unit.X);
        return H3Index.FromLatLng(new LatLng(latitude, longitude), 3);
    }

    private static void WriteVertex(
        Vector2 point,
        Color color,
        List<Vector3> vertices,
        List<Color> colors)
    {
        vertices.Add(new Vector3(point.X, point.Y, 0f));
        colors.Add(color);
    }

    private static Vector3 ToUnitVector(LatLng latLng)
    {
        var cosLat = Math.Cos(latLng.Latitude);
        return new Vector3(
            (float)(cosLat * Math.Cos(latLng.Longitude)),
            (float)Math.Sin(latLng.Latitude),
            (float)(cosLat * Math.Sin(latLng.Longitude)));
    }

    private void ApplyTransforms()
    {
        var size = GetViewportRect().Size;
        var radius = Mathf.Min(size.X, size.Y) * 0.42f;
        var scale = new Vector2(radius * _zoom, -radius * _zoom);
        var origin = size * 0.5f + _pan;

        foreach (var tile in _tiles.Values)
        {
            foreach (var tier in tile.Tiers)
            {
                if (tier is null)
                    continue;
                tier.Node.Position = origin;
                tier.Node.Scale = scale;
            }
        }
    }

    private static void ApplyTileAppearance(ParentTile tile)
    {
        var fading = tile.FromTier != tile.CurrentTier && tile.FadeElapsed < CrossfadeSeconds;
        var t = fading ? Mathf.Clamp(tile.FadeElapsed / CrossfadeSeconds, 0f, 1f) : 1f;
        var eased = t * t * (3f - 2f * t);
        var under = Math.Min(tile.FromTier, tile.CurrentTier);
        var over = Math.Max(tile.FromTier, tile.CurrentTier);
        var overAlpha = fading
            ? tile.FromTier < tile.CurrentTier ? eased : 1f - eased
            : 0f;

        for (var i = 0; i < tile.Tiers.Length; i++)
        {
            var tier = tile.Tiers[i];
            if (tier is null)
                continue;

            float alpha;
            if (!fading)
                alpha = i == tile.CurrentTier ? 1f : 0f;
            else if (i == under)
                alpha = 1f;
            else if (i == over)
                alpha = overAlpha;
            else
                alpha = 0f;

            tier.Node.Visible = alpha > 0.001f;
            tier.Node.Modulate = new Color(1f, 1f, 1f, alpha);
        }
    }

    private void HoldFocusCrossfade(float fraction)
    {
        var tile = _tiles[_focus];
        var to = VisibleTier(tile);
        var from = Math.Max(0, to - 1);

        // This is a deterministic inspection hook, not a tier transition: build
        // the two meshes directly so the queue's desired-tier filter cannot
        // discard the outgoing tier.
        EnsureTier(tile, from);
        EnsureTier(tile, to);
        ApplyTransforms();

        tile.FromTier = from;
        tile.CurrentTier = to;
        tile.FadeElapsed = CrossfadeSeconds * Mathf.Clamp(fraction, 0f, 1f);
        _holdFade = true;
        ApplyTileAppearance(tile);
        GD.Print($"lod hold crossfade: parent={tile.Index} {from}->{to} " +
                 $"res={TierResolutions[from]}->{TierResolutions[to]} at {fraction:F2}");
    }

    private void TrackMemory()
    {
        _maxWorkingSetMb = Math.Max(_maxWorkingSetMb, WorkingSetMegabytes());
    }

    private static long WorkingSetMegabytes()
        => Process.GetCurrentProcess().WorkingSet64 / (1024 * 1024);
}
