using System;
using System.Collections.Generic;
using System.Linq;

namespace Sim.Site;

/// <summary>
/// A cell in the radius-3 local build grid inside one fine hex. Coordinates are
/// axial: the disk contains every cell with max(|q|, |r|, |q+r|) &lt;= 3.
/// </summary>
public readonly record struct LocalCell(int Q, int R)
{
    public override string ToString() => $"({Q},{R})";
}

/// <summary>
/// Geometry of the per-hex build grid. Engine-free: the isometric renderer maps
/// these cells to screen space, but placement validity is decided here.
/// </summary>
public static class LocalGrid
{
    /// <summary>Radius of the disk; 1 + 6 + 12 + 18 = 37 cells.</summary>
    public const int DiskRadius = 3;

    /// <summary>The single cell occupied by a radius-0 building.</summary>
    public static LocalCell Center => new(0, 0);

    private static readonly LocalCell[] AllCells = BuildCells();

    /// <summary>Every cell of the disk, centre first, then by ring and coordinate.</summary>
    public static IReadOnlyList<LocalCell> Cells => AllCells;

    /// <summary>True when the cell lies inside the disk.</summary>
    public static bool IsInside(LocalCell cell)
        => Math.Max(Math.Abs(cell.Q), Math.Max(Math.Abs(cell.R), Math.Abs(cell.Q + cell.R))) <= DiskRadius;

    /// <summary>Axial hex distance between two cells.</summary>
    public static int Distance(LocalCell a, LocalCell b)
    {
        var dq = a.Q - b.Q;
        var dr = a.R - b.R;
        return (Math.Abs(dq) + Math.Abs(dr) + Math.Abs(dq + dr)) / 2;
    }

    /// <summary>
    /// The complete footprint around a centre, without disk clipping. Callers must
    /// check every returned cell with <see cref="IsInside"/>; a radius-1 building
    /// centred in ring 3 deliberately fails that check.
    /// </summary>
    public static IReadOnlyList<LocalCell> Footprint(LocalCell centre, int radius)
    {
        if (radius < 0)
            throw new ArgumentOutOfRangeException(nameof(radius));

        var cells = new List<LocalCell>();
        for (var dq = -radius; dq <= radius; dq++)
        {
            for (var dr = -radius; dr <= radius; dr++)
            {
                if (Math.Max(Math.Abs(dq), Math.Max(Math.Abs(dr), Math.Abs(dq + dr))) <= radius)
                    cells.Add(new LocalCell(centre.Q + dq, centre.R + dr));
            }
        }

        return cells;
    }

    private static LocalCell[] BuildCells()
    {
        var cells = new List<LocalCell>();
        for (var q = -DiskRadius; q <= DiskRadius; q++)
        {
            for (var r = -DiskRadius; r <= DiskRadius; r++)
            {
                var cell = new LocalCell(q, r);
                if (IsInside(cell))
                    cells.Add(cell);
            }
        }

        return cells
            .OrderBy(cell => Distance(Center, cell))
            .ThenBy(cell => cell.Q)
            .ThenBy(cell => cell.R)
            .ToArray();
    }
}
