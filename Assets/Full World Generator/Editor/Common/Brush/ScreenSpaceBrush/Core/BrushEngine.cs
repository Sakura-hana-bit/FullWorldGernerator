using UnityEngine;

namespace ScreenSpaceBrush
{
    /// <summary>
    /// Core brush painting engine. Stateless, pure functions for painting on a Texture2D.
    /// No Unity Editor dependencies — can be used in runtime or editor contexts.
    /// </summary>
    public static class BrushEngine
    {
        /// <summary>
        /// Paint a single circular stamp at the given UV coordinate.
        /// </summary>
        public static void Paint(Texture2D texture, Vector2 uv, BrushSettings settings)
        {
            if (texture == null) return;

            int width = texture.width;
            int height = texture.height;

            float cx = uv.x * width;
            float cy = uv.y * height;

            float radiusPx = settings.radius * Mathf.Max(width, height);
            int radiusCeil = Mathf.CeilToInt(radiusPx);

            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - radiusCeil));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - radiusCeil));
            int x1 = Mathf.Min(width - 1, Mathf.CeilToInt(cx + radiusCeil));
            int y1 = Mathf.Min(height - 1, Mathf.CeilToInt(cy + radiusCeil));

            if (x0 > x1 || y0 > y1) return;

            Color[] pixels = texture.GetPixels(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
            int regionW = x1 - x0 + 1;
            int regionH = y1 - y0 + 1;

            Color[] originalPixels = null;
            if (settings.mode == BrushSettings.PaintMode.Smooth)
            {
                originalPixels = new Color[pixels.Length];
                System.Array.Copy(pixels, originalPixels, pixels.Length);
            }

            for (int py = y0; py <= y1; py++)
            {
                for (int px = x0; px <= x1; px++)
                {
                    float dx = px - cx;
                    float dy = py - cy;
                    float dist = Mathf.Sqrt(dx * dx + dy * dy);

                    float normalizedDist = dist / radiusPx;
                    float weight = settings.WeightAtNormalizedDistance(normalizedDist);
                    if (weight <= 0f) continue;

                    int idx = (py - y0) * regionW + (px - x0);
                    Color existing = pixels[idx];

                    switch (settings.mode)
                    {
                        case BrushSettings.PaintMode.Modify:
                        {
                            float blend = weight * settings.opacity * settings.color.a;
                            pixels[idx] = new Color(
                                Mathf.Lerp(existing.r, settings.color.r, blend),
                                Mathf.Lerp(existing.g, settings.color.g, blend),
                                Mathf.Lerp(existing.b, settings.color.b, blend),
                                existing.a
                            );
                            break;
                        }
                        case BrushSettings.PaintMode.Erase:
                        {
                            float blend = weight * settings.opacity;
                            Color target = new Color(0f, 0f, 0f, existing.a);
                            pixels[idx] = new Color(
                                Mathf.Lerp(existing.r, target.r, blend),
                                Mathf.Lerp(existing.g, target.g, blend),
                                Mathf.Lerp(existing.b, target.b, blend),
                                existing.a
                            );
                            break;
                        }
                        case BrushSettings.PaintMode.Smooth:
                        {
                            Color blurred = SampleBlurred(originalPixels, px - x0, py - y0, regionW, regionW, regionH);
                            float blend = weight * settings.opacity;
                            pixels[idx] = new Color(
                                Mathf.Lerp(existing.r, blurred.r, blend),
                                Mathf.Lerp(existing.g, blurred.g, blend),
                                Mathf.Lerp(existing.b, blurred.b, blend),
                                existing.a
                            );
                            break;
                        }
                    }
                }
            }

            texture.SetPixels(x0, y0, regionW, regionH, pixels);
            texture.Apply(false);
        }

        /// <summary>
        /// Paint a line of stamps between two UV coordinates for smooth strokes.
        /// Spacing is ~20% of the brush radius.
        /// </summary>
        public static void PaintLine(Texture2D texture, Vector2 fromUv, Vector2 toUv, BrushSettings settings)
        {
            float dist = Vector2.Distance(fromUv, toUv);
            float spacing = Mathf.Max(0.002f, settings.radius * 0.2f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / spacing));

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 uv = Vector2.Lerp(fromUv, toUv, t);
                Paint(texture, uv, settings);
            }
        }

        private static Color SampleBlurred(Color[] pixels, int lx, int ly, int regionW, int totalW, int totalH)
        {
            Color sum = Color.clear;
            float count = 0f;

            for (int ky = -1; ky <= 1; ky++)
            {
                for (int kx = -1; kx <= 1; kx++)
                {
                    int sx = lx + kx;
                    int sy = ly + ky;
                    if (sx < 0 || sx >= totalW || sy < 0 || sy >= totalH) continue;

                    sum += pixels[sy * regionW + sx];
                    count += 1f;
                }
            }

            return count > 0 ? sum / count : Color.clear;
        }
    }
}
