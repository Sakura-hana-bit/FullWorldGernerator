using System.Collections.Generic;
using UnityEngine;

namespace FullWorld
{
    public static class VegetationScatterer
    {
        // ================================================================
        //  Public API
        // ================================================================

        /// <summary>
        /// Generates vegetation instances using clustered distribution.
        /// Cluster centers are placed via Poisson-disk-like sampling;
        /// individuals are scattered around each center with Gaussian offsets.
        /// Vegetation probability is driven by biome zone weight (from GPU
        /// biome map) × slope weight (flat = high, steep = low).
        /// </summary>
        public static List<VegetationInstance> Scatter(
            RenderTexture heightSlopeMap,
            RenderTexture biomeMap,
            in VegetationParams param,
            in FullWorldTerrain.BiomeDistribution biome,
            float heightScale,
            float meshExtentX,
            float meshExtentZ,
            int resolution,
            float[] layerMask = null)
        {
            var instances = new List<VegetationInstance>();

            Color[] heightPixels = ReadbackRT(heightSlopeMap, resolution);
            Color[] biomePixels = ReadbackRT(biomeMap, resolution);
            if (heightPixels == null || biomePixels == null)
            {
                Debug.LogWarning("[VegetationScatterer] Failed to read back heightmap or biome map.");
                return instances;
            }

            // Build per-pixel vegetation probability (0..1) from zone + slope
            var vegWeight = BuildVegetationWeight(heightPixels, biomePixels, biome, param, resolution, layerMask, out int validPixelCount);

            if (validPixelCount == 0)
            {
                Debug.Log("[VegetationScatterer] No vegetation zone pixels found.");
                return instances;
            }

            // Derive target count from density and weighted area
            float weightedArea = 0f;
            for (int i = 0; i < vegWeight.Length; i++)
                weightedArea += vegWeight[i];
            float worldArea = weightedArea / (resolution * resolution) * meshExtentX * meshExtentZ;
            int totalTarget = Mathf.Max(1, Mathf.RoundToInt(param.density * worldArea / 1000f));

            // Cluster parameters
            float clusterArea = Mathf.PI * param.clusterRadius * param.clusterRadius;
            int clusterCount = Mathf.Clamp(Mathf.Max(1, Mathf.RoundToInt(worldArea / clusterArea)), 1, totalTarget);
            int treesPerCluster = Mathf.Max(1, Mathf.CeilToInt((float)totalTarget / clusterCount));

            // Weighted sampling: build CDF over valid (weight > threshold) pixels
            var validPixels = new List<int>(validPixelCount);
            for (int i = 0; i < resolution * resolution; i++)
                if (vegWeight[i] > 0.05f)
                    validPixels.Add(i);

            // Generate cluster centers
            var masterRng = new System.Random(param.vegetationSeed + 1);
            var clusterCenters = GenerateClusterCenters(
                validPixels, vegWeight, clusterCount, param.clusterRadius,
                resolution, meshExtentX, meshExtentZ, masterRng);

            // Scatter individuals around each cluster center
            float halfExtentX = meshExtentX * 0.5f;
            float halfExtentZ = meshExtentZ * 0.5f;
            float clusterRadiusUV_X = param.clusterRadius / meshExtentX;
            float clusterRadiusUV_Z = param.clusterRadius / meshExtentZ;

            for (int c = 0; c < clusterCenters.Count; c++)
            {
                var clusterRng = new System.Random(param.vegetationSeed * 31 + c * 7919 + 1);
                Vector2 center = clusterCenters[c];

                for (int t = 0; t < treesPerCluster; t++)
                {
                    var instance = TryScatterIndividual(
                        center, clusterRng, vegWeight, heightPixels,
                        param, biome, heightScale,
                        halfExtentX, halfExtentZ,
                        clusterRadiusUV_X, clusterRadiusUV_Z,
                        meshExtentX, meshExtentZ, resolution,
                        instances);

                    if (instance.HasValue)
                        instances.Add(instance.Value);
                }
            }

            Debug.Log($"[VegetationScatterer] Generated {instances.Count} instances in {clusterCenters.Count} clusters (target {totalTarget}).");
            return instances;
        }

        // ================================================================
        //  Vegetation Weight (probability per pixel)
        // ================================================================

        /// <summary>
        /// Builds a per-pixel vegetation probability [0..1] by combining:
        ///   - Zone weight: from GPU biome map (green channel dominance),
        ///     drops at biome zone boundaries.
        ///   - Slope weight: 1 on flat ground, falling to 0 at maxSlope.
        /// Final weight = zoneWeight × slopeWeight, further masked by layerMask.
        /// </summary>
        private static float[] BuildVegetationWeight(
            Color[] heightPixels,
            Color[] biomePixels,
            in FullWorldTerrain.BiomeDistribution biome,
            in VegetationParams param,
            int resolution,
            float[] layerMask,
            out int validPixelCount)
        {
            int total = resolution * resolution;
            var weight = new float[total];
            validPixelCount = 0;

            // Scan max slope for normalization (matches GPU BiomeMapCS which clamps slope to [0,1])
            float maxObservedSlope = 0.001f;
            for (int i = 0; i < total; i++)
            {
                float sx = heightPixels[i].g, sy = heightPixels[i].b;
                float s = Mathf.Sqrt(sx * sx + sy * sy);
                if (s > maxObservedSlope) maxObservedSlope = s;
            }

            for (int i = 0; i < total; i++)
            {
                float slopeX = heightPixels[i].g;
                float slopeY = heightPixels[i].b;
                float rawSlope = Mathf.Sqrt(slopeX * slopeX + slopeY * slopeY);

                // Normalize slope to [0,1] range matching GPU biome shader behavior
                float slope = Mathf.Clamp01(rawSlope / maxObservedSlope);

                // --- Zone weight (gating): determines whether vegetation CAN be placed ---
                // Uses biome map green dominance. 0 = outside vegetation zone.
                float zoneWeight;

                if (param.restrictToBiomeZone && biomePixels != null)
                {
                    var bp = biomePixels[i];
                    float greenOverR = bp.g - bp.r;
                    float greenOverB = bp.g - bp.b;
                    zoneWeight = Mathf.Clamp01(Mathf.Min(greenOverR, greenOverB) / 0.15f);
                }
                else
                {
                    zoneWeight = 1f;
                }

                // Pixels outside the zone get zero weight regardless of slope
                if (zoneWeight < 0.01f)
                {
                    weight[i] = 0f;
                    continue;
                }

                // --- Slope density: modifies placement probability within the zone ---
                // Flat ground (slope≈0): full density (1.0)
                // At maxSlope: density drops to a minimum floor (0.2)
                // Above maxSlope: hard cutoff to 0
                // This avoids the multiplicative collapse where zone×slope both < 1 → near-zero
                float slopeDensity;
                if (param.maxSlope > 0.001f)
                {
                    if (slope < param.maxSlope)
                        slopeDensity = Mathf.Lerp(1f, 0.2f, slope / param.maxSlope);
                    else
                        slopeDensity = 0f;
                }
                else
                {
                    slopeDensity = slope < 0.001f ? 1f : 0f;
                }

                float w = zoneWeight * slopeDensity;

                if (layerMask != null)
                    w *= layerMask[i];

                weight[i] = w;
                if (w > 0.05f) validPixelCount++;
            }

            return weight;
        }

        // ================================================================
        //  Cluster Center Generation (weighted Poisson-disk)
        // ================================================================

        private static List<Vector2> GenerateClusterCenters(
            List<int> validPixels,
            float[] vegWeight,
            int clusterCount,
            float clusterRadius,
            int resolution,
            float meshExtentX,
            float meshExtentZ,
            System.Random rng)
        {
            var centers = new List<Vector2>();
            float minDist = clusterRadius * 1.2f;
            float maxExtent = Mathf.Max(meshExtentX, meshExtentZ);

            // Build CDF for weighted sampling of cluster centers
            var cdf = new float[validPixels.Count];
            float sum = 0f;
            for (int i = 0; i < validPixels.Count; i++)
            {
                sum += vegWeight[validPixels[i]];
                cdf[i] = sum;
            }

            if (sum < 1e-6f) return centers;

            int maxAttempts = clusterCount * 30;
            int attempts = 0;

            while (centers.Count < clusterCount && attempts < maxAttempts)
            {
                attempts++;

                // Weighted random pick from valid pixels
                int pixelIdx = SampleCDF(cdf, (float)rng.NextDouble() * sum, validPixels);
                int px = pixelIdx % resolution;
                int py = pixelIdx / resolution;

                var candidate = new Vector2(
                    (px + (float)rng.NextDouble()) / resolution,
                    (py + (float)rng.NextDouble()) / resolution);

                if (!IsTooCloseToCenters(candidate, centers, minDist, maxExtent))
                    centers.Add(candidate);
            }

            // Fallback: relax distance and fill remaining
            if (centers.Count < clusterCount)
            {
                float relaxedDist = clusterRadius * 0.4f;
                attempts = 0;
                maxAttempts = (clusterCount - centers.Count) * 40;

                while (centers.Count < clusterCount && attempts < maxAttempts)
                {
                    attempts++;
                    int pixelIdx = SampleCDF(cdf, (float)rng.NextDouble() * sum, validPixels);
                    int px = pixelIdx % resolution;
                    int py = pixelIdx / resolution;

                    var candidate = new Vector2(
                        (px + (float)rng.NextDouble()) / resolution,
                        (py + (float)rng.NextDouble()) / resolution);

                    if (!IsTooCloseToCenters(candidate, centers, relaxedDist, maxExtent))
                        centers.Add(candidate);
                }
            }

            // Last resort: random valid pixels without distance check
            if (centers.Count < clusterCount)
            {
                while (centers.Count < clusterCount)
                {
                    int pixelIdx = SampleCDF(cdf, (float)rng.NextDouble() * sum, validPixels);
                    int px = pixelIdx % resolution;
                    int py = pixelIdx / resolution;

                    centers.Add(new Vector2(
                        (px + (float)rng.NextDouble()) / resolution,
                        (py + (float)rng.NextDouble()) / resolution));
                }
            }

            return centers;
        }

        /// <summary>
        /// Binary-search a CDF array and return the validPixels entry at that index.
        /// </summary>
        private static int SampleCDF(float[] cdf, float target, List<int> validPixels)
        {
            int lo = 0, hi = cdf.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi) / 2;
                if (cdf[mid] < target) lo = mid + 1; else hi = mid;
            }
            return validPixels[lo];
        }

        private static bool IsTooCloseToCenters(Vector2 candidate, List<Vector2> centers, float minDist, float maxExtent)
        {
            float minDistSq = (minDist / maxExtent) * (minDist / maxExtent);
            for (int c = 0; c < centers.Count; c++)
            {
                float dx = candidate.x - centers[c].x;
                float dy = candidate.y - centers[c].y;
                if (dx * dx + dy * dy < minDistSq)
                    return true;
            }
            return false;
        }

        // ================================================================
        //  Individual Instance Scattering
        // ================================================================

        private static VegetationInstance? TryScatterIndividual(
            Vector2 center,
            System.Random rng,
            float[] vegWeight,
            Color[] heightPixels,
            in VegetationParams param,
            in FullWorldTerrain.BiomeDistribution biome,
            float heightScale,
            float halfExtentX,
            float halfExtentZ,
            float clusterRadiusUV_X,
            float clusterRadiusUV_Z,
            float meshExtentX,
            float meshExtentZ,
            int resolution,
            List<VegetationInstance> existingInstances)
        {
            // Gaussian offset from cluster center (Box-Muller)
            float u1 = Mathf.Max((float)rng.NextDouble(), 1e-6f);
            float u2 = (float)rng.NextDouble();
            float mag = Mathf.Sqrt(-2f * Mathf.Log(u1));
            float gaussX = mag * Mathf.Cos(2f * Mathf.PI * u2);
            float gaussZ = mag * Mathf.Sin(2f * Mathf.PI * u2);

            float uvX = Mathf.Clamp01(center.x + gaussX * clusterRadiusUV_X);
            float uvZ = Mathf.Clamp01(center.y + gaussZ * clusterRadiusUV_Z);

            int sampleX = Mathf.Clamp((int)(uvX * (resolution - 1)), 0, resolution - 1);
            int sampleZ = Mathf.Clamp((int)(uvZ * (resolution - 1)), 0, resolution - 1);
            int sampleIdx = sampleZ * resolution + sampleX;

            // Weight acts as zone+slope gate, not as rejection probability.
            // Cluster centers are already weighted-sampled; here we only need
            // to confirm the pixel is inside the vegetation zone.
            float w = vegWeight[sampleIdx];
            if (w < 0.01f)
            {
                // Outside zone — try snapping to nearest valid pixel
                sampleIdx = FindNearestValidPixel(vegWeight, sampleX, sampleZ, resolution, 3);
                if (sampleIdx < 0)
                    return null;

                uvX = (float)(sampleIdx % resolution) / resolution;
                uvZ = (float)(sampleIdx / resolution) / resolution;
            }

            float height = heightPixels[sampleIdx].r;

            var worldPos = new Vector3(
                -halfExtentX + uvX * meshExtentX,
                height * heightScale,
                -halfExtentZ + uvZ * meshExtentZ);

            // Min distance check
            if (IsTooCloseToExisting(worldPos, existingInstances, param.minDistance))
                return null;

            // Compute slope tilt
            float slopeX = heightPixels[sampleIdx].g;
            float slopeY = heightPixels[sampleIdx].b;
            float tiltStrength = param.slopeTiltStrength;
            float randomness = 1f - param.slopeTiltRandomness * (float)rng.NextDouble();
            float tiltX = Mathf.Atan(slopeX) * Mathf.Rad2Deg * tiltStrength * randomness;
            float tiltZ = Mathf.Atan(slopeY) * Mathf.Rad2Deg * tiltStrength * randomness;

            // Determine type & dimensions
            bool isBush = (float)rng.NextDouble() < param.bushRatio;
            VegetationType type = isBush ? VegetationType.Bush : VegetationType.Tree;

            float vegHeight, vegRadius;
            if (isBush)
            {
                vegHeight = Mathf.Lerp(param.bushHeightRange.x, param.bushHeightRange.y, (float)rng.NextDouble());
                vegRadius = Mathf.Lerp(param.bushRadiusRange.x, param.bushRadiusRange.y, (float)rng.NextDouble());
            }
            else
            {
                vegHeight = Mathf.Lerp(param.treeHeightRange.x, param.treeHeightRange.y, (float)rng.NextDouble());
                vegRadius = Mathf.Lerp(param.treeRadiusRange.x, param.treeRadiusRange.y, (float)rng.NextDouble());
            }

            float rotation = (float)rng.NextDouble() * 360f;

            return new VegetationInstance(worldPos, type, vegHeight, vegRadius, rotation, tiltX, tiltZ);
        }

        /// <summary>
        /// Searches for the nearest pixel with vegetation weight > threshold
        /// within a square radius. Returns -1 if none found.
        /// </summary>
        private static int FindNearestValidPixel(float[] vegWeight, int cx, int cz, int resolution, int searchRadius)
        {
            int bestDist = int.MaxValue;
            int bestIdx = -1;

            for (int dz = -searchRadius; dz <= searchRadius; dz++)
            {
                for (int dx = -searchRadius; dx <= searchRadius; dx++)
                {
                    int x = cx + dx, z = cz + dz;
                    if (x < 0 || x >= resolution || z < 0 || z >= resolution) continue;

                    int idx = z * resolution + x;
                    if (vegWeight[idx] < 0.05f) continue;

                    int dist = dx * dx + dz * dz;
                    if (dist < bestDist)
                    {
                        bestDist = dist;
                        bestIdx = idx;
                    }
                }
            }

            return bestIdx;
        }

        // ================================================================
        //  Proximity Check
        // ================================================================

        private static bool IsTooCloseToExisting(Vector3 pos, List<VegetationInstance> instances, float minDist)
        {
            float minDistSq = minDist * minDist;
            int start = Mathf.Max(0, instances.Count - 128);
            for (int i = instances.Count - 1; i >= start; i--)
            {
                float dx = instances[i].position.x - pos.x;
                float dz = instances[i].position.z - pos.z;
                if (dx * dx + dz * dz < minDistSq)
                    return true;
            }
            return false;
        }

        // ================================================================
        //  Utilities
        // ================================================================

        private static int RandRange(System.Random rng, int max)
        {
            return (int)(rng.NextDouble() * max);
        }

        private static Color[] ReadbackRT(RenderTexture rt, int resolution)
        {
            if (rt == null) return null;

            int rtWidth = rt.width;
            int rtHeight = rt.height;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var tex = new Texture2D(rtWidth, rtHeight, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, rtWidth, rtHeight), 0, 0);
            tex.Apply();
            var pixels = tex.GetPixels();

            RenderTexture.active = prev;
            Object.DestroyImmediate(tex);

            if (rtWidth != resolution || rtHeight != resolution)
            {
                var resampled = new Color[resolution * resolution];
                for (int y = 0; y < resolution; y++)
                {
                    for (int x = 0; x < resolution; x++)
                    {
                        int srcX = Mathf.Clamp(x * rtWidth / resolution, 0, rtWidth - 1);
                        int srcY = Mathf.Clamp(y * rtHeight / resolution, 0, rtHeight - 1);
                        resampled[y * resolution + x] = pixels[srcY * rtWidth + srcX];
                    }
                }
                return resampled;
            }

            return pixels;
        }
    }
}
