using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Godot;

/// <summary>
/// Isometric presentation of the seven-region <see cref="AreaView"/> mosaic.
///
/// The geometry mirrors the colony diorama: every fine hex is a six-triangle fan
/// from its own elevation to corner heights averaged across the hexes that meet
/// there, with a skirt only on the outer edge of the seven-tile slab. The result
/// is one <see cref="ArrayMesh"/> so the whole area costs a single draw call.
///
/// Corner heights and the palette are recomputed when relief, orbit or overlay
/// changes; the underlying baked data is never modified.
/// </summary>
public sealed class AreaDiorama
{
    /// <summary>Number of 60° orbit steps.</summary>
    public const int RotationStepCount = 6;

    /// <summary>Fraction of the plane span the full elevation range should rise.</summary>
    private const float ReliefFraction = 0.18f;

    /// <summary>Fraction of the plane span used for the outer slab wall.</summary>
    private const float SlabFraction = 0.03f;

    private static readonly Vector2 LightDirection = new Vector2(-0.45f, 0.55f).Normalized();

    private readonly Vector2[] _centers;
    private readonly Vector2[][] _corners;
    private readonly float[] _elevations;
    private readonly int[][] _cornerIds;
    private readonly bool[][] _boundaryEdges;
    private readonly int _cornerCount;
    private readonly double _radiusMetres;
    private readonly float _autoRelief;
    private readonly float _slab;
    private readonly float _minElevation;
    private readonly float _elevationSpan;
    private readonly Vector2 _homePivot;

    private Vector2[][] _rotatedCorners;
    private Vector2[][] _isoCorners;
    private Vector2[] _rotatedCenters;
    private Vector2[] _isoCenters;
    private float[][] _cornerHeights;
    private float[] _heights;
    private int[] _order;
    private Color[] _fills;
    private Rect2 _bounds;

    private int _yawSteps;
    private float _yaw;
    private float _reliefScale = 1f;
    private float _cellRadius;
    private ArrayMesh? _mesh;
    private bool _meshDirty = true;

    public AreaDiorama(
        Vector2[] centers,
        Vector2[][] corners,
        string[][] cornerKeys,
        float[] elevations,
        float minElevation,
        float elevationSpan,
        double radiusMetres,
        Color[] fills)
    {
        _centers = centers;
        _corners = corners;
        _elevations = elevations;
        _minElevation = minElevation;
        _elevationSpan = Mathf.Max(1e-6f, elevationSpan);
        _radiusMetres = Math.Max(1.0, radiusMetres);
        _fills = fills;

        var cornerIdsByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        _cornerIds = new int[corners.Length][];
        for (var i = 0; i < corners.Length; i++)
        {
            var ids = new int[cornerKeys[i].Length];
            for (var v = 0; v < ids.Length; v++)
            {
                if (!cornerIdsByKey.TryGetValue(cornerKeys[i][v], out var id))
                {
                    id = cornerIdsByKey.Count;
                    cornerIdsByKey[cornerKeys[i][v]] = id;
                }

                ids[v] = id;
            }

            _cornerIds[i] = ids;
        }

        _cornerCount = cornerIdsByKey.Count;
        _boundaryEdges = new bool[corners.Length][];
        var edgeCounts = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < _cornerIds.Length; i++)
        {
            var ids = _cornerIds[i];
            for (var v = 0; v < ids.Length; v++)
            {
                var key = EdgeKey(ids[v], ids[(v + 1) % ids.Length]);
                edgeCounts.TryGetValue(key, out var seen);
                edgeCounts[key] = seen + 1;
            }
        }

        var skirtEdges = 0;
        for (var i = 0; i < _cornerIds.Length; i++)
        {
            var ids = _cornerIds[i];
            var boundary = new bool[ids.Length];
            for (var v = 0; v < ids.Length; v++)
            {
                // Only the rim of the seven-tile mosaic is a boundary; internal
                // region joints are shared by two hexes and get no skirt.
                boundary[v] = edgeCounts[EdgeKey(ids[v], ids[(v + 1) % ids.Length])] == 1;
                if (boundary[v])
                    skirtEdges++;
            }

            _boundaryEdges[i] = boundary;
        }

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        foreach (var polygon in corners)
        {
            foreach (var point in polygon)
            {
                min = new Vector2(Mathf.Min(min.X, point.X), Mathf.Min(min.Y, point.Y));
                max = new Vector2(Mathf.Max(max.X, point.X), Mathf.Max(max.Y, point.Y));
            }
        }

        _homePivot = (min + max) * 0.5f;
        var spanPlane = Mathf.Max(max.X - min.X, max.Y - min.Y);
        var elevationSpanPlane = _elevationSpan / _radiusMetres;
        _autoRelief = elevationSpanPlane > 1e-9
            ? ReliefFraction * spanPlane / (float)elevationSpanPlane
            : 0f;
        _slab = SlabFraction * spanPlane;

        _rotatedCorners = new Vector2[corners.Length][];
        _isoCorners = new Vector2[corners.Length][];
        _rotatedCenters = new Vector2[corners.Length];
        _isoCenters = new Vector2[corners.Length];
        _cornerHeights = new float[corners.Length][];
        _heights = new float[corners.Length];
        _order = Array.Empty<int>();

        RebuildGeometry();
        GD.Print($"area diorama: {centers.Length} hexes · {_cornerCount} shared corners · " +
                 $"{skirtEdges} skirt edges · auto relief {_autoRelief:F2} · slab {_slab:F5}");
    }

    /// <summary>Current orbit angle in degrees (0, 60, … 300).</summary>
    public int CameraDegrees => _yawSteps * 60;

    /// <summary>User multiplier on the automatic relief exaggeration.</summary>
    public float ReliefScale => _reliefScale;

    /// <summary>Average plane-space hex radius, used for picking tolerance.</summary>
    public float CellRadius => _cellRadius;

    /// <summary>Bounding rectangle of the projected surface in isometric units.</summary>
    public Rect2 Bounds => _bounds;

    /// <summary>Projected region centres, indexed by the area's global cell index.</summary>
    public IReadOnlyList<Vector2> IsoCenters => _isoCenters;

    /// <summary>Projected hex corners, indexed by the area's global cell index.</summary>
    public IReadOnlyList<Vector2[]> IsoCorners => _isoCorners;

    /// <summary>The single shaded surface mesh.</summary>
    public ArrayMesh Mesh
    {
        get
        {
            if (_meshDirty || _mesh is null)
                BuildMesh();

            return _mesh!;
        }
    }

    /// <summary>Orbits the diorama by whole 60° steps.</summary>
    public void Rotate(int steps)
    {
        var normalized = ((steps % RotationStepCount) + RotationStepCount) % RotationStepCount;
        if (normalized == _yawSteps)
            return;

        _yawSteps = normalized;
        _yaw = normalized * Mathf.Pi / 3f;
        RebuildGeometry();
    }

    /// <summary>Changes the relief exaggeration and refits the caller's camera.</summary>
    public void SetRelief(float scale)
    {
        var next = Mathf.Clamp(scale, 0.25f, 4f);
        if (Mathf.IsEqualApprox(next, _reliefScale))
            return;

        _reliefScale = next;
        RebuildGeometry();
    }

    /// <summary>Swaps the palette used by the next mesh rebuild.</summary>
    public void SetFills(Color[] fills)
    {
        _fills = fills;
        _meshDirty = true;
    }

    /// <summary>Recomputes shared corner heights, projected points and paint order.</summary>
    private void RebuildGeometry()
    {
        var cos = Mathf.Cos(_yaw);
        var sin = Mathf.Sin(_yaw);

        for (var i = 0; i < _heights.Length; i++)
        {
            // Same presentation-only smoothstep as the flat slope mesh: ordering
            // and endpoints survive, broad ramps gain readable facet contrast.
            var normalized = (float)((_elevations[i] - _minElevation) / _elevationSpan);
            _heights[i] = (float)(AreaView.ReliefCurve(normalized)
                * (_elevationSpan / _radiusMetres) * _autoRelief * _reliefScale);
        }

        var cornerSums = new float[_cornerCount];
        var cornerCounts = new int[_cornerCount];
        for (var i = 0; i < _heights.Length; i++)
        {
            var ids = _cornerIds[i];
            for (var v = 0; v < ids.Length; v++)
            {
                cornerSums[ids[v]] += _heights[i];
                cornerCounts[ids[v]]++;
            }
        }

        for (var i = 0; i < _heights.Length; i++)
        {
            var ids = _cornerIds[i];
            _cornerHeights[i] = new float[ids.Length];
            for (var v = 0; v < ids.Length; v++)
            {
                _cornerHeights[i][v] = cornerCounts[ids[v]] > 0
                    ? cornerSums[ids[v]] / cornerCounts[ids[v]]
                    : _heights[i];
            }
        }

        var min = new Vector2(float.MaxValue, float.MaxValue);
        var max = new Vector2(float.MinValue, float.MinValue);
        for (var i = 0; i < _corners.Length; i++)
        {
            var center = IsoProjection.Rotate(CellCenter(i), _homePivot, cos, sin);
            _rotatedCenters[i] = center;
            _isoCenters[i] = IsoProjection.Project(center, _heights[i]);

            var corners = _corners[i];
            var rotated = new Vector2[corners.Length];
            var iso = new Vector2[corners.Length];
            for (var v = 0; v < corners.Length; v++)
            {
                rotated[v] = IsoProjection.Rotate(corners[v], _homePivot, cos, sin);
                iso[v] = IsoProjection.Project(rotated[v], _cornerHeights[i][v]);
                min = new Vector2(Mathf.Min(min.X, iso[v].X), Mathf.Min(min.Y, iso[v].Y));
                max = new Vector2(Mathf.Max(max.X, iso[v].X), Mathf.Max(max.Y, iso[v].Y));
            }

            _rotatedCorners[i] = rotated;
            _isoCorners[i] = iso;
        }

        _bounds = new Rect2(min, max - min);
        _cellRadius = EstimateCellRadius();
        _order = Enumerable.Range(0, _corners.Length)
            .OrderByDescending(i => IsoProjection.Depth(_rotatedCenters[i]))
            .ToArray();
        _meshDirty = true;
    }

    private void BuildMesh()
    {
        var topVertices = _corners.Sum(polygon => polygon.Length * 3);
        var skirtVertices = _boundaryEdges.Sum(edges => edges.Count(boundary => boundary) * 6);
        var vertices = new Vector2[topVertices + skirtVertices];
        var colors = new Color[topVertices + skirtVertices];
        var index = 0;
        var baseHeight = -_slab;
        var minShade = float.MaxValue;
        var maxShade = float.MinValue;

        foreach (var i in _order)
        {
            var plane = _rotatedCorners[i];
            var center = _rotatedCenters[i];
            var isoCorners = _isoCorners[i];
            var fill = _fills[i];
            var height = _heights[i];
            var n = plane.Length;
            var baseLuma = Mathf.Max(1e-4f, fill.R + fill.G + fill.B);

            for (var v = 0; v < n; v++)
            {
                var next = (v + 1) % n;
                var facet = AreaView.FacetShade(fill, center, plane[v], plane[next],
                    height, _cornerHeights[i][v], _cornerHeights[i][next]);
                var shade = (facet.R + facet.G + facet.B) / baseLuma;
                minShade = Mathf.Min(minShade, shade);
                maxShade = Mathf.Max(maxShade, shade);

                vertices[index] = _isoCenters[i];
                colors[index++] = facet;
                vertices[index] = isoCorners[v];
                colors[index++] = facet;
                vertices[index] = isoCorners[next];
                colors[index++] = facet;
            }

            for (var v = 0; v < n; v++)
            {
                if (!_boundaryEdges[i][v])
                    continue;

                var next = (v + 1) % n;
                var heightA = _cornerHeights[i][v];
                var heightB = _cornerHeights[i][next];
                var topA = isoCorners[v];
                var topB = isoCorners[next];
                var bottomA = new Vector2(topA.X, topA.Y + heightA - baseHeight);
                var bottomB = new Vector2(topB.X, topB.Y + heightB - baseHeight);

                var edge = plane[next] - plane[v];
                var normal = new Vector2(edge.Y, -edge.X).Normalized();
                var shade = 0.38f + 0.22f * Mathf.Max(0f, normal.Dot(LightDirection));
                var wall = ScaleColor(fill, shade);

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
        }

        if (minShade <= maxShade)
            GD.Print($"area diorama facets: shade x{minShade:F2}..{maxShade:F2}");

        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Godot.Mesh.ArrayType.Max);
        arrays[(int)Godot.Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Godot.Mesh.ArrayType.Color] = colors;

        _mesh = new ArrayMesh();
        _mesh.AddSurfaceFromArrays(Godot.Mesh.PrimitiveType.Triangles, arrays);
        _meshDirty = false;
    }

    private Vector2 CellCenter(int ordinal)
    {
        var sum = Vector2.Zero;
        foreach (var corner in _corners[ordinal])
            sum += corner;

        return sum / _corners[ordinal].Length;
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

    private static string EdgeKey(int a, int b) => a < b
        ? $"{a.ToString(CultureInfo.InvariantCulture)}:{b.ToString(CultureInfo.InvariantCulture)}"
        : $"{b.ToString(CultureInfo.InvariantCulture)}:{a.ToString(CultureInfo.InvariantCulture)}";

    private static Color ScaleColor(Color color, float factor) => new(
        Mathf.Min(1f, color.R * factor),
        Mathf.Min(1f, color.G * factor),
        Mathf.Min(1f, color.B * factor),
        color.A);
}
