#ifndef TERRAIN_EROSION_HLSL
#define TERRAIN_EROSION_HLSL

#include "TerrainNoise.hlsl"

#define TAU 6.28318530717959
#define TREES
// Pack 4 [0,1] values into a single float (8-bit precision per channel).
float Pack4(float4 v) {
    float4 s = floor(saturate(v) * 255.0) / 255.0;
    return s.x + s.y / 256.0 + s.z / 65536.0 + s.w / 16777216.0;
}

// -----------------------------------------------------------------------------
// UTILITY FUNCTIONS
// -----------------------------------------------------------------------------

float PowInv(float t, float power) {
    return 1.0 - pow(1.0 - saturate(t), power);
}

float EaseOut(float t) {
    float v = 1.0 - saturate(t);
    return 1.0 - v * v;
}

float SmoothStart(float t, float smoothing) {
    if (t >= smoothing)
        return t - 0.5 * smoothing;
    return 0.5 * t * t / smoothing;
}

float2 SafeNormalize(float2 n) {
    float l = length(n);
    return (abs(l) > 1e-10) ? (n / l) : n;
}

// -----------------------------------------------------------------------------
// PHACELLE NOISE FUNCTION
// -----------------------------------------------------------------------------

// Phacelle Noise function copyright (c) 2025 Rune Skovbo Johansen
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
float4 PhacelleNoise(in float2 p, float2 normDir, float freq, float offset, float normalization, float2 seed) {
    float2 sideDir = normDir.yx * float2(-1.0, 1.0) * freq * TAU;
    offset *= TAU;

    float2 pInt = floor(p);
    float2 pFrac = frac(p);
    float2 phaseDir = float2(0.0, 0.0);
    float weightSum = 0.0;

    for (int i = -1; i <= 2; i++) {
        for (int j = -1; j <= 2; j++) {
            float2 gridOffset = float2(i, j);
            float2 gridPoint = pInt + gridOffset;
            float2 randomOffset = hash(gridPoint + seed) * 0.5;
            float2 vectorFromCellPoint = pFrac - gridOffset - randomOffset;

            float sqrDist = dot(vectorFromCellPoint, vectorFromCellPoint);
            float weight = exp(-sqrDist * 2.0);
            weight = max(0.0, weight - 0.01111);

            weightSum += weight;

            float waveInput = dot(vectorFromCellPoint, sideDir) + offset;
            phaseDir += float2(cos(waveInput), sin(waveInput)) * weight;
        }
    }

    float2 interpolated = phaseDir / weightSum;// 权重加成（d+[d0*w0+d1*w1...]/w）
    float mag = sqrt(dot(interpolated, interpolated));
    mag = max(1.0 - normalization, mag);
    return float4(interpolated / mag, sideDir);
}

// -----------------------------------------------------------------------------
// EROSION FILTER
// -----------------------------------------------------------------------------

// Advanced Terrain Erosion Filter copyright (c) 2025 Rune Skovbo Johansen
// This Source Code Form is subject to the terms of the Mozilla Public
// License, v. 2.0. If a copy of the MPL was not distributed with this
// file, You can obtain one at https://mozilla.org/MPL/2.0/.
float4 ErosionFilter(
    float2 p,               //固定连续
    float3 heightAndSlope,  //固定连续
    float fadeTarget,       
    float strength,         //连续可变
    float gullyWeight,      //连续可变
    float detail,           //连续可变
    float4 rounding, 
    float4 onset, 
    float2 assumedSlope,
    float scale, 
    int octaves, 
    float lacunarity,
    float gain,
    float cellScale, 
    float normalization,
    float2 seed,
    out float ridgeMap, 
    out float debug
) {
    fadeTarget = clamp(fadeTarget, -1.0, 1.0);

    float3 inputHeightAndSlope = heightAndSlope;
    float freq = 1.0 / (scale * cellScale);
    float slopeLength = max(length(heightAndSlope.yz), 1e-10);
    float magnitude = 0.0;
    float roundingMult = 1.0;

    float roundingForInput = lerp(rounding.y, rounding.x, saturate(fadeTarget + 0.5)) * rounding.z;
    float combiMask = EaseOut(SmoothStart(slopeLength * onset.x, roundingForInput * onset.x));

    float ridgeMapCombiMask = EaseOut(slopeLength * onset.z);
    float ridgeMapFadeTarget = fadeTarget;

    float2 gullySlope = lerp(heightAndSlope.yz, heightAndSlope.yz / slopeLength * assumedSlope.x, assumedSlope.y);

    for (int i = 0; i < octaves; i++) {
        float4 phacelle = PhacelleNoise(p * freq, SafeNormalize(gullySlope), cellScale, 0.25, normalization, seed);
        // phacelle (dir.xy,sideDir.xy)
        phacelle.zw *= -freq;
        float sloping = abs(phacelle.y);

        gullySlope += sign(phacelle.y) * phacelle.zw * strength * gullyWeight;

        // Phacelle会形成波浪状的侵蚀，类似于cos函数的复合
        // cos的导数就是sin 变换是cos的同时此时梯度的方向就是sin，故是float3(phacelle.x, phacelle.y * phacelle.zw);
        float3 gullies = float3(phacelle.x, phacelle.y * phacelle.zw);
        float3 fadedGullies = lerp(float3(fadeTarget, 0.0, 0.0), gullies * gullyWeight, combiMask);
        heightAndSlope += fadedGullies * strength;
        magnitude += strength;

        fadeTarget = fadedGullies.x;

        float roundingForOctave = lerp(rounding.y, rounding.x, saturate(phacelle.x + 0.5)) * roundingMult;
        float newMask = EaseOut(SmoothStart(sloping * onset.y, roundingForOctave * onset.y));
        combiMask = PowInv(combiMask, detail) * newMask;

        ridgeMapFadeTarget = lerp(ridgeMapFadeTarget, gullies.x, ridgeMapCombiMask);
        float newRidgeMapMask = EaseOut(sloping * onset.w);
        ridgeMapCombiMask = ridgeMapCombiMask * newRidgeMapMask;

        strength *= gain;
        freq *= lacunarity;
        roundingMult *= rounding.w;
    }

    ridgeMap = ridgeMapFadeTarget * (1.0 - ridgeMapCombiMask);
    debug = fadeTarget;

    float3 heightAndSlopeDelta = heightAndSlope - inputHeightAndSlope;
    return float4(heightAndSlopeDelta, magnitude);
}

// -----------------------------------------------------------------------------
// HEIGHTMAP
// -----------------------------------------------------------------------------

// Terrain constants — set these via material properties or shader globals.
#ifndef DEFAULT_HEIGHT
#define DEFAULT_HEIGHT 0.0
#endif
#ifndef GRASS_HEIGHT
#define GRASS_HEIGHT 0.0
#endif
#ifndef WATER_HEIGHT
#define WATER_HEIGHT 0.0
#endif

float GetTreesAmount(float height, float normalY, float occlusion, float ridgeMap) {
    return ((
        smoothstep(
            GRASS_HEIGHT + 0.05,
            GRASS_HEIGHT + 0.01,
            height + 0.01 + (occlusion - 0.8) * 0.05
        )
        * smoothstep(0.0, 0.4, occlusion)
        * smoothstep(0.95, 1.0, normalY)
        * smoothstep(-1.4, 0.0, ridgeMap)
        #if defined(WATER)
            * smoothstep(WATER_HEIGHT + 0.000, WATER_HEIGHT + 0.007, height)
        #endif
    ) - 0.5) / 0.6;
}

float4 TerrainHeightmap(
    float2 p,
    float3 heightSlopeMap,
    float erosionScale,
    float erosionStrength,
    float erosionGullyWeight,
    float erosionDetail,
    float4 erosionRounding,     // x: ridge, y: crease, z: initial mult, w: octave mult
    float4 erosionOnset,        // x: initial, y: octave, z: ridgeMap initial, w: ridgeMap octave
    float2 erosionAssumedSlope, // x: assumed slope value, y: override amount
    float erosionCellScale,
    float erosionNormalization,
    int erosionOctaves,
    float erosionLacunarity,
    float erosionGain,
    float2 terrainHeightOffset, // x: offset, y: fade target blend
    float erosionEnabled,
    float fadeTargetDivisor,
    float2 seed
) {
    float3 n = heightSlopeMap;

    float fadeTarget = clamp((n.x - DEFAULT_HEIGHT) / fadeTargetDivisor, -1.0, 1.0);

    float ridgeMap, debug;
    float4 h = ErosionFilter(
        p, n, fadeTarget,
        erosionStrength, erosionGullyWeight, erosionDetail,
        erosionRounding, erosionOnset, erosionAssumedSlope,
        erosionScale, erosionOctaves, erosionLacunarity,
        erosionGain, erosionCellScale, erosionNormalization,
        seed, ridgeMap, debug);

    bool erosion = erosionEnabled > 0.0;

    #ifdef COMPARISON_SLIDER
        if (1.0 - p.y > 0.5 - cos(_Time.y))
            erosion = false;
    #endif

    if (!erosion) {
        h = float4(0.0, 0.0, 0.0, 1.0);  // w=1 avoids 0/0 in erosion delta
        ridgeMap = 1.0;
    }

    float offset = lerp(terrainHeightOffset.x, -fadeTarget, terrainHeightOffset.y) * h.w;
    float eroded = n.x + h.x + offset;
    float2 erodedSlope = n.yz + h.yz ;

    float trees = -1.0;
    #if defined(TREES)
        float2 deriv = n.yz + h.yz;
        float normalY = 1.0 / sqrt(1.0 + dot(deriv, deriv));
        float occlusion = h.w > 1e-6 ? h.x / h.w + 0.5 : 0.5;  // guard 0/0 when erosion disabled
        float treesAmount = GetTreesAmount(eroded, normalY, occlusion, ridgeMap);
        trees = (1.0 - pow(noised((p + 0.5) * 200.0, seed).x * 0.5 + 0.5, 2.0) - 1.0 + 1.0 * treesAmount) * 1.5;
        if (trees > 0.0) {
            eroded += trees / 300.0;
        }
    #endif

    float erosionDeltaNorm = h.w > 1e-6 ? h.x / h.w : 0.0;  // 0 when no erosion
    float packed = Pack4(float4(
        saturate(erosionDeltaNorm * 0.5 + 0.5),
        saturate(ridgeMap * 0.5 + 0.5),
        saturate(trees * 0.5 + 0.5),
        saturate(debug * 0.5 + 0.5)
    ));

    return float4(eroded, erodedSlope.x, erodedSlope.y, packed);
}

#endif
