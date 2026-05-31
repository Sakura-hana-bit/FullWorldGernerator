#ifndef TERRAIN_NOISE_HLSL
#define TERRAIN_NOISE_HLSL

float2 hash(in float2 x) {
    const float2 k = float2(0.3183099, 0.3678794);
    x = x * k + k.yx;
    return -1.0 + 2.0 * frac(16.0 * k * frac(x.x * x.y * (x.x + x.y)));
}

// Returns gradient noise (in x) and its derivatives (in yz).
// From https://www.shadertoy.com/view/XdXBRH
// seed: offsets grid coordinates in the hash, changing gradients without shifting UVs.
float3 noised(in float2 p, in float2 seed) {
    float2 i = floor(p);
    float2 f = frac(p);

    float2 u = f * f * f * (f * (f * 6.0 - 15.0) + 10.0);
    float2 du = 30.0 * f * f * (f * (f - 2.0) + 1.0);

    float2 ga = hash(i + float2(0.0, 0.0) + seed);
    float2 gb = hash(i + float2(1.0, 0.0) + seed);
    float2 gc = hash(i + float2(0.0, 1.0) + seed);
    float2 gd = hash(i + float2(1.0, 1.0) + seed);

    float va = dot(ga, f - float2(0.0, 0.0));
    float vb = dot(gb, f - float2(1.0, 0.0));
    float vc = dot(gc, f - float2(0.0, 1.0));
    float vd = dot(gd, f - float2(1.0, 1.0));

    return float3(
        va + u.x * (vb - va) + u.y * (vc - va) + u.x * u.y * (va - vb - vc + vd),
        ga + u.x * (gb - ga) + u.y * (gc - ga) + u.x * u.y * (ga - gb - gc + gd) +
        du * (u.yx * (va - vb - vc + vd) + float2(vb, vc) - va));
}

// Fractal Brownian Motion with analytical derivatives.
// Returns float3(height, dheight/dx, dheight/dy).
// seed: passed to noised() to randomize gradient selection.
float3 FractalNoise(float2 p, float freq, int octaves, float lacunarity, float gain, float2 seed) {
    float3 n = float3(0.0, 0.0, 0.0);
    float nf = freq;
    float na = 1.0;
    for (int i = 0; i < octaves; i++) {
        n += noised(p * nf, seed) * na * float3(1.0, nf, nf);
        na *= gain;
        nf *= lacunarity;
    }
    return n;
}

#endif
