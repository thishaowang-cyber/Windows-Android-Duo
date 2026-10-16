#version 300 es
precision highp float;

// Fullscreen triangle generated from gl_VertexID — no VBO/IBO needed.
// uv comes out as (0,0) (2,0) (0,2) for the three vertices, so the visible
// area is covered by the interpolated 0..1 range, with uv.y = 0 at the top.
void main() {
    vec2 uv = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));
    gl_Position = vec4(uv.x * 2.0 - 1.0, 1.0 - uv.y * 2.0, 0.0, 1.0);
}
