#version 300 es
precision highp float;

// Vertical-keystone compensation.
//
// The image's top and bottom edges stay glued to the phone's top and bottom
// edges, so the full height is always shown and never stretched or cropped.
//
// Tilting the screen about a horizontal axis compresses the vertical by cos θ
// as seen by the eye, while leaving the horizontal untouched. Narrowing the
// width by that same cos θ cancels the compression, so the picture still reads
// as a vertical one at any tilt; the slack turns into background on the left
// and right. The angle alone determines everything — there is no eye distance
// or viewing distance to tune.

uniform vec2 uSize;      // drawable size in pixels
uniform float uAngle;    // virtual tilt, degrees (0 = facing you, 80 = max)
uniform float uFrost;    // frost strength, 0 .. 0.18
uniform sampler2D uTex;
uniform float uAspect;   // source image width / height
uniform float uFlip;     // >0.5 rotates the whole frame 180 degrees
uniform float uHingeTop; // >0.5 mirrors the grazing highlight

out vec4 fragColor;

#define PI 3.14159265359

const vec3 kBackground = vec3(0.015, 0.018, 0.025);

// Aspect-fill lookup so the source covers the whole drawable, including the
// area beside a notch. `point` is in pixels with y measured from the top.
vec3 sampleScene(vec2 point, float lod) {
    float screenAspect = uSize.x / uSize.y;
    vec2 fit = screenAspect > uAspect
        ? vec2(1.0, screenAspect / uAspect)
        : vec2(uAspect / screenAspect, 1.0);
    vec2 uv = (point / uSize - 0.5) / fit + 0.5;
    if (any(lessThan(uv, vec2(0.0))) || any(greaterThan(uv, vec2(1.0)))) {
        return kBackground;
    }
    return textureLod(uTex, uv, lod).rgb;
}

void main() {
    // gl_FragCoord is measured from the bottom-left of the drawable; a phone
    // taped to a laptop lid is often mounted rotated 180 degrees, so the whole
    // frame can be flipped here.
    vec2 coord = gl_FragCoord.xy;
    if (uFlip > 0.5) coord = uSize - coord;

    vec2 pixel = vec2(coord.x, uSize.y - coord.y);      // y measured from the top

    float theta = uAngle * PI / 180.0;
    float c = max(cos(theta), 0.15);

    // y stays 1:1, so the source's top row lands exactly on the screen's top
    // edge and its bottom row on the bottom edge.
    vec2 uv = vec2((pixel.x / uSize.x - 0.5) / c + 0.5, pixel.y / uSize.y);
    vec2 point = uv * uSize;

    // The narrowing drives frost and dimming, keeping the "the more it tilts,
    // the softer it gets" feel.
    float gap = uSize.x * (1.0 / c - 1.0) * 0.5;

    float radius = min(abs(gap) * uFrost, uSize.y * 0.055);
    float lod = max(0.0, log2(max(1.0, radius * 0.22)));

    // 24-tap golden-angle disk: spatially varying frost without hard banding.
    vec3 color = vec3(0.0);
    for (int i = 0; i < 24; i++) {
        float r = sqrt((float(i) + 0.5) / 24.0) * radius;
        float a = float(i) * 2.39996323;
        color += sampleScene(point + vec2(cos(a), sin(a)) * r, lod);
    }
    color /= 24.0;

    float depth = abs(gap) / uSize.y;
    color *= 1.0 - min(depth * 0.45, 0.35);

    // Grazing reflection, mirrored with the hinge edge.
    float uvY = 1.0 - coord.y / uSize.y;
    float bandY = (uHingeTop > 0.5) ? (1.0 - uvY) : uvY;
    float t = (bandY - (0.25 + sin(theta) * 0.7)) / 0.19;
    color += vec3(0.78, 0.87, 1.0) * exp(-t * t) * abs(sin(theta)) * 0.055;

    fragColor = vec4(color, 1.0);
}
