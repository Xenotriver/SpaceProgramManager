shader_type canvas_item;
render_mode unshaded;

// Rotates baked H3 cell boundaries in the vertex shader, so the globe is a
// continuous mesh instead of 41k independent flat hexagons. Adjacent cells
// share boundary vectors, which removes the gaps between them. Fragments on
// the far hemisphere are discarded, giving an exact orthographic limb.

uniform float yaw = 0.0;
uniform float pitch = 0.0;
uniform vec2 globe_center = vec2(0.0, 0.0);
uniform float globe_scale = 300.0;

varying vec4 cell_color;
varying float front_z;

vec3 rotate_sphere(vec3 v) {
    float cos_yaw = cos(yaw);
    float sin_yaw = sin(yaw);
    float cos_pitch = cos(pitch);
    float sin_pitch = sin(pitch);

    // Same convention as MoonGlobe.Rotate: yaw about the polar axis, pitch
    // towards the camera, then invert screen X so west is left and east right.
    float x1 = v.x * cos_yaw + v.z * sin_yaw;
    float z1 = -v.x * sin_yaw + v.z * cos_yaw;
    float y = v.y * cos_pitch - z1 * sin_pitch;
    float z = v.y * sin_pitch + z1 * cos_pitch;
    return vec3(-x1, y, z);
}

void vertex() {
    // VERTEX carries sphere X/Y and UV.x carries sphere Z. Both are baked in
    // BodyWorld/RegionTileScoring-independent H3 coordinates, so every body
    // uses the same shader without per-body data or pipeline changes.
    vec3 rotated = rotate_sphere(vec3(VERTEX.x, VERTEX.y, UV.x));

    VERTEX = globe_center + vec2(rotated.x, -rotated.y) * globe_scale;
    front_z = rotated.z;
    cell_color = COLOR;
}

void fragment() {
    if (front_z <= 0.0)
        discard;

    COLOR = cell_color;
}
