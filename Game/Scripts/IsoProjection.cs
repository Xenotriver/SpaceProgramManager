using Godot;

/// <summary>
/// Fixed 2:1 isometric projection used by the close-up management view.
///
/// Input plane coordinates are the same gnomonic tangent-plane values produced by
/// <see cref="RegionView"/> (radians on the unit sphere); <c>z</c> is elevation
/// already converted to plane units. The projection is deliberately small and
/// engine-light so it can be unit-tested and reused for picking.
///
/// The camera orbits by rotating the plane before projecting. Hexes are regular
/// enough that a 60° turn maps the lattice onto itself, so every step keeps the
/// same prism shapes and only the terrain silhouette changes.
/// </summary>
public static class IsoProjection
{
    /// <summary>cos(30°) — horizontal spread of the isometric axes.</summary>
    public const float Cos30 = 0.8660254f;

    /// <summary>sin(30°) — vertical spread of the isometric axes.</summary>
    public const float Sin30 = 0.5f;

    /// <summary>
    /// Projects a plane point plus elevation into isometric units. North rises up
    /// the screen and higher elevation also rises, so the camera looks from the
    /// south-east at a 30° tilt.
    /// </summary>
    public static Vector2 Project(Vector2 plane, float z)
        => new((plane.X - plane.Y) * Cos30, -(plane.X + plane.Y) * Sin30 - z);

    /// <summary>
    /// Inverse of <see cref="Project"/> for a known elevation. Used to turn the
    /// mouse position back into the plane for picking.
    /// </summary>
    public static Vector2 Unproject(Vector2 iso, float z)
    {
        var difference = iso.X / Cos30; // x - y
        var sum = (-iso.Y - z) / Sin30; // x + y

        return new Vector2((difference + sum) * 0.5f, (sum - difference) * 0.5f);
    }

    /// <summary>Painter depth: larger values are farther away and drawn first.</summary>
    public static float Depth(Vector2 plane) => plane.X + plane.Y;

    /// <summary>Rotates a plane point around <paramref name="pivot"/> by <paramref name="yaw"/> radians.</summary>
    public static Vector2 Rotate(Vector2 plane, Vector2 pivot, float yaw)
        => Rotate(plane, pivot, Mathf.Cos(yaw), Mathf.Sin(yaw));

    /// <summary>
    /// Allocation-free variant for loops that rotate many points by the same angle;
    /// pass <c>Mathf.Cos(yaw)</c> and <c>Mathf.Sin(yaw)</c>.
    /// </summary>
    public static Vector2 Rotate(Vector2 plane, Vector2 pivot, float cos, float sin)
    {
        var x = plane.X - pivot.X;
        var y = plane.Y - pivot.Y;

        return new Vector2(pivot.X + x * cos - y * sin, pivot.Y + x * sin + y * cos);
    }
}
