using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Godot;
using Sim.Biomes;
using Sim.Buildings;
using Sim.Campaign;
using Sim.Site;
using Sim.World;

/// <summary>
/// One fine hex opened as a building site: a radius-3 axial grid of 37 local
/// cells rendered as a small isometric diorama. The parent hex is the container;
/// every installation inside it lives at a <see cref="LocalCell"/>.
///
/// State stays in <see cref="CampaignState"/>. This view only projects cells,
/// picks the nearest centre, previews placements and draws the resulting state.
/// </summary>
public partial class SiteView : Node2D
{
    /// <summary>Hovered local cell in axial coordinates.</summary>
    [Signal]
    public delegate void HexHoveredEventHandler(int q, int r);

    /// <summary>Clicked local cell in axial coordinates.</summary>
    [Signal]
    public delegate void HexSelectedEventHandler(int q, int r);

    /// <summary>Active building definition changed.</summary>
    [Signal]
    public delegate void ActiveBuildingChangedEventHandler(string defId);

    /// <summary>Result of a placement/removal attempt.</summary>
    [Signal]
    public delegate void PlacementFeedbackEventHandler(bool success, string message);

    /// <summary>Installation count in this site changed.</summary>
    [Signal]
    public delegate void BuildingsChangedEventHandler(int count);

    /// <summary>Plane-space radius of one local hex top.</summary>
    private const float CellRadius = 0.5f;

    /// <summary>Visual thickness of every local hex tile, in projected units.</summary>
    private const float SlabDepth = 0.16f;

    private const float MinZoom = 0.5f;
    private const float MaxZoom = 10f;
    private const float DragThresholdPixels = 4f;

    private static readonly Color BorderColor = new(0f, 0f, 0f, 0.28f);
    private static readonly Color HoverColor = new(1f, 1f, 1f, 0.95f);
    private static readonly Color SelectionColor = new(0.55f, 0.85f, 1f, 0.9f);
    private static readonly Vector2 LightDirection = new Vector2(-0.45f, 0.55f).Normalized();

    private BodyWorld _world = null!;
    private RegionTile _tile = null!;
    private int _hexOrdinal = -1;
    private CampaignState? _campaign;

    private BuildingDef? _activeBuilding;
    private readonly List<BuildingDef> _buildingChoices = new();
    private IReadOnlyList<BuildingInstance> _installations = Array.Empty<BuildingInstance>();

    private LocalCell[] _cells = Array.Empty<LocalCell>();
    private readonly Dictionary<LocalCell, int> _indexByCell = new();
    private Vector2[] _planeCenters = Array.Empty<Vector2>();
    private Vector2[][] _planeCorners = Array.Empty<Vector2[]>();
    private Vector2[] _isoCenters = Array.Empty<Vector2>();
    private Vector2[][] _isoCorners = Array.Empty<Vector2[]>();
    private Color[] _fills = Array.Empty<Color>();
    private int[] _order = Array.Empty<int>();

    private ArrayMesh? _mesh;
    private bool _meshDirty = true;
    private Vector2[] _outlinePoints = Array.Empty<Vector2>();
    private readonly Dictionary<int, Vector2[]> _drawBuffers = new();
    private readonly Dictionary<int, Vector2[]> _closedBuffers = new();
    private ImageTexture _whiteTexture = null!;
    private Font _font = null!;

    private Vector2 _center;
    private Vector2 _pan;
    private float _pixelsPerUnit = 1f;
    private float _zoom = 1f;
    private float _cellRadiusIso;

    private int _hovered = -1;
    private int _selected = -1;
    private IReadOnlyList<LocalCell> _ghostFootprint = Array.Empty<LocalCell>();
    private bool _ghostValid;
    private string _ghostError = string.Empty;

    private bool _panning;
    private bool _dragging;
    private Vector2 _lastMouse;
    private Vector2 _dragStart;

    private double _drawTotalMs;
    private int _drawCount;

    /// <summary>True once a tile has been loaded.</summary>
    public bool IsLoaded => _tile is not null;

    /// <summary>Ordinal of the fine hex whose site is open.</summary>
    public int HexOrdinal => _hexOrdinal;

    /// <summary>Installations in the open site.</summary>
    public IReadOnlyList<BuildingInstance> Installations => _installations;

    /// <summary>Installations in the open site.</summary>
    public int InstallationCount => _installations.Count;

    /// <summary>Building selected for placement, or null before a registry is set.</summary>
    public BuildingDef? ActiveBuilding => _activeBuilding;

    /// <summary>Number of building definitions available for cycling.</summary>
    public int BuildingChoiceCount => _buildingChoices.Count;

    /// <summary>Validation error for the hovered preview, or empty.</summary>
    public string GhostError => _ghostError;

    /// <summary>Whether the hovered preview is valid.</summary>
    public bool GhostValid => _ghostValid;

    /// <summary>CPU time spent in the most recent <c>_Draw</c> call (ms).</summary>
    public double LastDrawMs { get; private set; }

    /// <summary>Average CPU time per <c>_Draw</c> since the last reset (ms).</summary>
    public double AverageDrawMs => _drawCount > 0 ? _drawTotalMs / _drawCount : 0.0;

    /// <summary>Clears the draw-time accumulator used by the performance HUD.</summary>
    public void ResetDrawStats()
    {
        _drawTotalMs = 0.0;
        _drawCount = 0;
    }

    /// <summary>Opens the site on one fine hex of a loaded region tile.</summary>
    public void Load(BodyWorld world, RegionTile tile, int hexOrdinal, CampaignState campaign, BuildingRegistry registry)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(tile);
        ArgumentNullException.ThrowIfNull(campaign);
        ArgumentNullException.ThrowIfNull(registry);

        if ((uint)hexOrdinal >= (uint)tile.HexCount)
            throw new ArgumentOutOfRangeException(nameof(hexOrdinal));

        _world = world;
        _tile = tile;
        _hexOrdinal = hexOrdinal;
        _campaign = campaign;
        campaign.MarkVisited(tile.Entry.Index);

        _buildingChoices.Clear();
        _buildingChoices.AddRange(registry.All.Values
            .OrderBy(def => def.Category)
            .ThenBy(def => def.Id, StringComparer.Ordinal));
        _activeBuilding = _buildingChoices.FirstOrDefault();

        BuildGeometry();
        RefreshInstallations();
        UpdateGhost();

        _hovered = -1;
        _selected = -1;
        Visible = true;
        EmitSignal(SignalName.BuildingsChanged, _installations.Count);
        if (_activeBuilding is not null)
            EmitSignal(SignalName.ActiveBuildingChanged, _activeBuilding.Id);

        GD.Print($"site view: hex {hexOrdinal} · H3 {tile.Cells[hexOrdinal]} · {_cells.Length} local cells");
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
        if (!IsLoaded)
            return;

        var drawStart = Stopwatch.GetTimestamp();
        var scale = _pixelsPerUnit * _zoom;

        if (_meshDirty || _mesh is null)
            BuildMesh();

        DrawMesh(_mesh!, _whiteTexture,
            new Transform2D(new Vector2(scale, 0f), new Vector2(0f, scale), _center + _pan));
        DrawGridOutlines(scale);
        DrawInstallations(scale);
        DrawGhost(scale);

        if (_hovered >= 0)
            DrawCellOutline(_cells[_hovered], HoverColor, 2f, scale);

        if (_selected >= 0 && _selected != _hovered)
            DrawCellOutline(_cells[_selected], SelectionColor, 2f, scale);

        if (_hovered >= 0 && _ghostFootprint.Count > 0 && _activeBuilding is not null)
            DrawGhostLabel(scale);

        LastDrawMs = (Stopwatch.GetTimestamp() - drawStart) * 1000.0 / Stopwatch.Frequency;
        _drawTotalMs += LastDrawMs;
        _drawCount++;
    }

    public override void _UnhandledInput(InputEvent @event)
    {
        if (!Visible || !IsLoaded)
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

    /// <summary>Places the active building at a local grid cell.</summary>
    public bool PlaceAt(int q, int r)
    {
        var cell = new LocalCell(q, r);
        if (_campaign is null || _tile is null || _activeBuilding is null || !LocalGrid.IsInside(cell))
        {
            var reason = _activeBuilding is null ? "no active building" : $"cell {cell} is outside the site grid";
            EmitSignal(SignalName.PlacementFeedback, false, reason);
            return false;
        }

        var result = _campaign.TryPlace(_activeBuilding, _tile, _hexOrdinal, cell);
        if (!result.Success)
        {
            _ghostValid = false;
            _ghostError = result.Error;
            EmitSignal(SignalName.PlacementFeedback, false, result.Error);
            QueueRedraw();
            return false;
        }

        RefreshInstallations();
        UpdateGhost();

        var message = $"Placed {_activeBuilding.DisplayName} at {cell}";
        if (result.Warnings.Count > 0)
            message += $" · {string.Join(" · ", result.Warnings)}";

        EmitSignal(SignalName.BuildingsChanged, _installations.Count);
        EmitSignal(SignalName.PlacementFeedback, true, message);
        QueueRedraw();
        return true;
    }

    /// <summary>Removes the installation occupying a local grid cell.</summary>
    public bool RemoveAt(int q, int r)
    {
        var cell = new LocalCell(q, r);
        if (_campaign is null || _tile is null)
            return false;

        if (!_campaign.TryRemove(_tile, _hexOrdinal, cell, out var building) || building is null)
        {
            EmitSignal(SignalName.PlacementFeedback, false, $"No installation at {cell}");
            return false;
        }

        RefreshInstallations();
        UpdateGhost();
        EmitSignal(SignalName.BuildingsChanged, _installations.Count);
        EmitSignal(SignalName.PlacementFeedback, true, $"Removed {building.DefId} at {cell}");
        QueueRedraw();
        return true;
    }

    /// <summary>Removes the installation under the current selection (or cursor).</summary>
    public bool RemoveSelected()
    {
        var index = _selected >= 0 ? _selected : _hovered;
        return index >= 0 && index < _cells.Length && RemoveAt(_cells[index].Q, _cells[index].R);
    }

    /// <summary>Removes every installation in this site, leaving other sites alone.</summary>
    public int ClearInstallations()
    {
        if (_campaign is null || _tile is null)
            return 0;

        var snapshot = _campaign.Sites(_tile.Entry.Index)
            .Values
            .Where(site => (ulong)site.ParentHex == (ulong)_tile.Cells[_hexOrdinal])
            .SelectMany(site => site.Installations)
            .ToArray();

        var removed = 0;
        foreach (var installation in snapshot)
        {
            if (_campaign.TryRemove(_tile, _hexOrdinal, installation.LocalCenter, out _))
                removed++;
        }

        if (removed > 0)
        {
            RefreshInstallations();
            UpdateGhost();
            EmitSignal(SignalName.BuildingsChanged, _installations.Count);
            EmitSignal(SignalName.PlacementFeedback, true, $"Cleared {removed} installation(s) from this site");
            QueueRedraw();
        }
        else
        {
            EmitSignal(SignalName.PlacementFeedback, false, "No installations in this site");
        }

        return removed;
    }

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
        SetActiveBuilding(_buildingChoices[index].Id);
    }

    /// <summary>Selects a building definition by id.</summary>
    public bool SetActiveBuilding(string defId)
    {
        var def = _buildingChoices.FirstOrDefault(candidate => candidate.Id == defId);
        if (def is null)
            return false;

        _activeBuilding = def;
        EmitSignal(SignalName.ActiveBuildingChanged, def.Id);
        UpdateGhost();
        QueueRedraw();
        return true;
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
        if (_panning || _dragging)
        {
            _pan += motion.Position - _lastMouse;
            _lastMouse = motion.Position;
            if (_dragging)
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
            EmitSignal(SignalName.HexHovered, -1, -1);
            return;
        }

        var cell = _cells[hit];
        EmitSignal(SignalName.HexHovered, cell.Q, cell.R);
    }

    private void SelectHovered()
    {
        if (_hovered < 0)
        {
            _selected = -1;
            QueueRedraw();
            return;
        }

        var cell = _cells[_hovered];
        _selected = _hovered;
        EmitSignal(SignalName.HexSelected, cell.Q, cell.R);
        PlaceAt(cell.Q, cell.R);
    }

    private int Pick(Vector2 mouse)
    {
        if (_isoCenters.Length == 0)
            return -1;

        var scale = _pixelsPerUnit * _zoom;
        var maxDistance = Mathf.Max(8f, _cellRadiusIso * scale * 1.05f);
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

    private void RefreshInstallations()
    {
        if (_campaign is null || _tile is null)
        {
            _installations = Array.Empty<BuildingInstance>();
            return;
        }

        var parent = (ulong)_tile.Cells[_hexOrdinal];
        _installations = _campaign.Buildings(_tile.Entry.Index)
            .Where(building => (ulong)building.ParentHex == parent)
            .ToArray();
    }

    private void UpdateGhost()
    {
        _ghostFootprint = Array.Empty<LocalCell>();
        _ghostValid = false;
        _ghostError = string.Empty;

        if (_campaign is null || _tile is null || _activeBuilding is null || _hovered < 0)
            return;

        var cell = _cells[_hovered];
        var result = _campaign.Preview(_activeBuilding, _tile, _hexOrdinal, cell);
        _ghostFootprint = result.LocalFootprint;
        _ghostValid = result.Success;
        _ghostError = result.Success ? string.Empty : result.Error;
    }

    private void BuildGeometry()
    {
        _cells = LocalGrid.Cells.ToArray();
        _indexByCell.Clear();
        _planeCenters = new Vector2[_cells.Length];
        _planeCorners = new Vector2[_cells.Length][];
        _isoCenters = new Vector2[_cells.Length];
        _isoCorners = new Vector2[_cells.Length][];
        _fills = new Color[_cells.Length];

        var parentFill = BiomePalette.For(_world.Biomes[_tile[_hexOrdinal].BiomeCode].Id);

        for (var i = 0; i < _cells.Length; i++)
        {
            var cell = _cells[i];
            _indexByCell[cell] = i;

            var center = AxialToPlane(cell);
            var corners = new Vector2[6];
            for (var v = 0; v < corners.Length; v++)
            {
                var angle = (v * 60f - 30f) * Mathf.Pi / 180f;
                corners[v] = center + new Vector2(
                    Mathf.Cos(angle) * CellRadius,
                    Mathf.Sin(angle) * CellRadius);
            }

            _planeCenters[i] = center;
            _planeCorners[i] = corners;
            _isoCenters[i] = IsoProjection.Project(center, 0f);
            _isoCorners[i] = corners.Select(corner => IsoProjection.Project(corner, 0f)).ToArray();

            // Deterministic subtle variation keeps the tray readable without
            // pretending the parent hex has sub-cell elevation data.
            var shade = 0.84f + 0.04f * ((cell.Q - cell.R + 9) % 3);
            _fills[i] = new Color(
                parentFill.R * shade,
                parentFill.G * shade,
                parentFill.B * shade,
                1f);
        }

        _order = Enumerable.Range(0, _cells.Length)
            .OrderByDescending(i => IsoProjection.Depth(_planeCenters[i]))
            .ToArray();

        _cellRadiusIso = EstimateCellRadius();
        BuildOutlines();
        _meshDirty = true;
        FitView();
    }

    private void BuildMesh()
    {
        const int perCell = 18 + 36; // top fan (6 triangles) + six wall quads
        var vertices = new Vector2[_cells.Length * perCell];
        var colors = new Color[_cells.Length * perCell];
        var index = 0;

        foreach (var i in _order)
        {
            var iso = _isoCorners[i];
            var plane = _planeCorners[i];
            var fill = _fills[i];
            var n = iso.Length;

            for (var v = 0; v < n; v++)
            {
                var next = (v + 1) % n;
                var topA = iso[v];
                var topB = iso[next];
                var bottomA = topA + new Vector2(0f, SlabDepth);
                var bottomB = topB + new Vector2(0f, SlabDepth);

                var edge = plane[next] - plane[v];
                var normal = new Vector2(edge.Y, -edge.X).Normalized();
                var shade = 0.42f + 0.20f * Mathf.Max(0f, normal.Dot(LightDirection));
                var wall = new Color(fill.R * shade, fill.G * shade, fill.B * shade, 1f);

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

            var top = new Color(
                Mathf.Min(1f, fill.R * 1.04f),
                Mathf.Min(1f, fill.G * 1.04f),
                Mathf.Min(1f, fill.B * 1.04f),
                1f);
            var center = _isoCenters[i];

            for (var v = 0; v < n; v++)
            {
                vertices[index] = center;
                colors[index++] = top;
                vertices[index] = iso[v];
                colors[index++] = top;
                vertices[index] = iso[(v + 1) % n];
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

    private void BuildOutlines()
    {
        var count = 0;
        foreach (var corners in _isoCorners)
            count += corners.Length * 2;

        _outlinePoints = new Vector2[count];
        var index = 0;
        foreach (var corners in _isoCorners)
        {
            for (var v = 0; v < corners.Length; v++)
            {
                _outlinePoints[index++] = corners[v];
                _outlinePoints[index++] = corners[(v + 1) % corners.Length];
            }
        }
    }

    private void DrawGridOutlines(float scale)
    {
        var screen = ScreenBuffer(_outlinePoints.Length);
        for (var i = 0; i < _outlinePoints.Length; i++)
            screen[i] = ToScreen(_outlinePoints[i], scale);

        DrawMultiline(screen, BorderColor, 1f, false);
    }

    private void DrawInstallations(float scale)
    {
        foreach (var building in _installations)
        {
            var color = BuildingColor(building.DefId);
            var footprint = new Color(color.R, color.G, color.B, 0.45f);

            foreach (var cell in building.LocalFootprint)
                DrawCellOutline(cell, footprint, 1.5f, scale);

            var center = IsoProjection.Project(AxialToPlane(building.LocalCenter), 0f);
            var screen = ToScreen(center, scale);
            var radius = Mathf.Max(5f, _cellRadiusIso * scale * 0.42f);
            var status = BuildingStatusColor(building.Status);

            DrawCircle(screen, radius + 2f, new Color(0f, 0f, 0f, 0.75f));
            DrawCircle(screen, radius, status);

            if (building.Status == BuildingStatus.UnderConstruction)
                DrawArc(screen, radius + 4f, 0f, Mathf.Tau * (float)building.Progress, 20,
                    new Color(1f, 0.85f, 0.4f, 0.95f), 2f, true);
        }
    }

    private void DrawGhost(float scale)
    {
        if (_ghostFootprint.Count == 0)
            return;

        var color = _ghostValid
            ? new Color(0.35f, 1f, 0.55f)
            : new Color(1f, 0.35f, 0.3f);
        var fill = new Color(color.R, color.G, color.B, 0.32f);

        foreach (var cell in _ghostFootprint)
            DrawCellOutline(cell, fill, 2f, scale, fill);
    }

    private void DrawGhostLabel(float scale)
    {
        var cell = _cells[_hovered];
        var anchor = ToScreen(IsoProjection.Project(AxialToPlane(cell), 0f), scale)
                     + new Vector2(14f, -10f);
        var text = _ghostValid
            ? $"{_activeBuilding!.DisplayName} · place"
            : _ghostError;

        if (text.Length > 64)
            text = text[..61] + "...";

        DrawString(_font, anchor, text, HorizontalAlignment.Left, -1, 11,
            _ghostValid ? new Color(0.65f, 1f, 0.72f) : new Color(1f, 0.68f, 0.62f));
    }

    private void DrawCellOutline(LocalCell cell, Color color, float width, float scale, Color? fill = null)
    {
        var planeCenter = AxialToPlane(cell);
        var screen = ScreenBuffer(6);

        for (var v = 0; v < 6; v++)
        {
            var angle = (v * 60f - 30f) * Mathf.Pi / 180f;
            var corner = planeCenter + new Vector2(
                Mathf.Cos(angle) * CellRadius,
                Mathf.Sin(angle) * CellRadius);
            screen[v] = ToScreen(IsoProjection.Project(corner, 0f), scale);
        }

        if (fill is not null)
            DrawColoredPolygon(screen, fill.Value);

        DrawPolyline(Close(screen), color, width, true);
    }

    private void DrawCellOutline(int index, Color color, float width, float scale)
        => DrawCellOutline(_cells[index], color, width, scale);

    private Color BuildingColor(string defId)
    {
        if (_activeBuilding is not null && _activeBuilding.Id == defId
            && !string.IsNullOrWhiteSpace(_activeBuilding.FootprintColor))
            return Color.FromHtml(_activeBuilding.FootprintColor);

        if (_campaign is not null
            && _campaign.Registry.TryGet(defId, out var def)
            && def is not null
            && !string.IsNullOrWhiteSpace(def.FootprintColor))
            return Color.FromHtml(def.FootprintColor);

        return new Color(0.85f, 0.85f, 0.92f, 0.85f);
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
                max = new Vector2(Mathf.Max(max.X, corner.X), Mathf.Max(max.Y, corner.Y + SlabDepth));
            }
        }

        var size = GetViewportRect().Size;
        var span = new Vector2(Mathf.Max(max.X - min.X, 1e-6f), Mathf.Max(max.Y - min.Y, 1e-6f));
        var fit = Mathf.Min(size.X * 0.72f / span.X, size.Y * 0.74f / span.Y);

        _center = size * 0.5f + new Vector2(70f, 0f);
        _zoom = 1f;
        _pixelsPerUnit = fit;
        _pan = -((min + max) * 0.5f) * fit + new Vector2(-70f, 0f);
    }

    private float EstimateCellRadius()
    {
        var sum = 0f;
        var count = 0;
        for (var i = 0; i < _isoCenters.Length; i++)
        {
            foreach (var corner in _isoCorners[i])
            {
                sum += _isoCenters[i].DistanceTo(corner);
                count++;
            }
        }

        return count > 0 ? sum / count : 0.001f;
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

    /// <summary>Pointy-top axial layout matching LocalGrid's distance metric.</summary>
    private static Vector2 AxialToPlane(LocalCell cell)
        => new(
            Mathf.Sqrt(3f) * (cell.Q + cell.R * 0.5f) * CellRadius,
            1.5f * cell.R * CellRadius);

    private void OnViewportResized()
    {
        if (IsLoaded)
            FitView();

        QueueRedraw();
    }
}
