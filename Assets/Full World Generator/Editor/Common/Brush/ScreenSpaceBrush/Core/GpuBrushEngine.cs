using UnityEngine;

namespace ScreenSpaceBrush
{
    /// <summary>
    /// GPU-accelerated brush painting engine using a Compute Shader.
    /// Stateless, pure functions — mirrors the CPU BrushEngine API but operates on RenderTextures.
    /// 
    /// Usage:
    ///   GpuBrushEngine.Initialize();  // Load default shader from Resources
    ///   // — or —
    ///   GpuBrushEngine.Initialize(myCustomComputeShader);
    ///   
    ///   GpuBrushEngine.Paint(renderTexture, uv, settings);
    ///   GpuBrushEngine.PaintLine(renderTexture, fromUv, toUv, settings);
    ///   
    ///   GpuBrushEngine.Cleanup();  // Release temp textures
    /// 
    /// The Compute Shader must provide three kernels: CSAdd, CSErase, CSSmooth.
    /// Kernel parameters: _Center, _Radius, _Hardness, _Opacity, _Color,
    ///                    _TextureSize, _BoundsOffset, _Result (RWTexture2D),
    ///                    _Source (Texture2D, Smooth mode only).
    /// </summary>
    public static class GpuBrushEngine
    {
        private static ComputeShader _shader;
        private static int _kernelAdd;
        private static int _kernelErase;
        private static int _kernelSmooth;
        private static RenderTexture _tempTexture;

        private const int ThreadGroupSize = 8;

        /// <summary>True after Initialize() has been called successfully.</summary>
        public static bool IsInitialized => _shader != null;

        /// <summary>
        /// Initialize the engine with a Compute Shader.
        /// If customShader is null, loads "ScreenSpaceBrush/BrushPaint" from Resources.
        /// </summary>
        public static void Initialize(ComputeShader customShader = null)
        {
            _shader = customShader ?? Resources.Load<ComputeShader>("ScreenSpaceBrush/BrushPaint");
            if (_shader == null)
            {
                Debug.LogError("[GpuBrushEngine] No compute shader provided and " +
                               "'ScreenSpaceBrush/BrushPaint' not found in Resources!");
                return;
            }

            _kernelAdd    = _shader.FindKernel("CSAdd");
            _kernelErase  = _shader.FindKernel("CSErase");
            _kernelSmooth = _shader.FindKernel("CSSmooth");
        }

        /// <summary>
        /// Ensure the engine is initialized (idempotent). Call before painting.
        /// Returns true if ready to paint.
        /// </summary>
        public static bool EnsureInitialized()
        {
            if (_shader != null) return true;
            Initialize();
            return _shader != null;
        }

        // ── Paint API (mirrors CPU BrushEngine) ──────────────────────────

        /// <summary>
        /// Paint a single circular stamp at the given UV coordinate on a RenderTexture.
        /// Only dispatches threads within the brush bounding box for efficiency.
        /// </summary>
        public static void Paint(RenderTexture rt, Vector2 uv, BrushSettings settings)
        {
            if (rt == null) return;
            if (!EnsureInitialized()) return;

            int width = rt.width;
            int height = rt.height;

            // Bounding box in pixel space (same logic as CPU BrushEngine)
            float cx = uv.x * width;
            float cy = uv.y * height;
            float radiusPx = settings.radius * Mathf.Max(width, height);
            int radiusCeil = Mathf.CeilToInt(radiusPx);

            int x0 = Mathf.Max(0, Mathf.FloorToInt(cx - radiusCeil));
            int y0 = Mathf.Max(0, Mathf.FloorToInt(cy - radiusCeil));
            int x1 = Mathf.Min(width - 1, Mathf.CeilToInt(cx + radiusCeil));
            int y1 = Mathf.Min(height - 1, Mathf.CeilToInt(cy + radiusCeil));

            if (x0 > x1 || y0 > y1) return;

            int boundsW = x1 - x0 + 1;
            int boundsH = y1 - y0 + 1;

            // Set common shader parameters
            _shader.SetVector("_Center", new Vector4(uv.x, uv.y, 0, 0));
            _shader.SetFloat("_Radius", settings.radius);
            _shader.SetFloat("_Hardness", settings.hardness);
            _shader.SetFloat("_Opacity", settings.opacity);
            _shader.SetVector("_Color", settings.color.linear); // Work in linear space on GPU
            _shader.SetVector("_TextureSize", new Vector4(width, height, 0, 0));
            _shader.SetVector("_BoundsOffset", new Vector4(x0, y0, 0, 0));

            int kernel;
            switch (settings.mode)
            {
                case BrushSettings.PaintMode.Modify:
                    kernel = _kernelAdd;
                    _shader.SetTexture(kernel, "_Result", rt);
                    break;

                case BrushSettings.PaintMode.Erase:
                    kernel = _kernelErase;
                    _shader.SetTexture(kernel, "_Result", rt);
                    break;

                case BrushSettings.PaintMode.Smooth:
                    kernel = _kernelSmooth;
                    // Copy current state to temp for blur source
                    EnsureTempTexture(width, height);
                    Graphics.Blit(rt, _tempTexture);
                    _shader.SetTexture(kernel, "_Result", rt);
                    _shader.SetTexture(kernel, "_Source", _tempTexture);
                    break;

                default:
                    return;
            }

            int groupsX = (boundsW + ThreadGroupSize - 1) / ThreadGroupSize;
            int groupsY = (boundsH + ThreadGroupSize - 1) / ThreadGroupSize;

            _shader.Dispatch(kernel, groupsX, groupsY, 1);
        }

        /// <summary>
        /// Paint a line of stamps between two UV coordinates for smooth strokes.
        /// Spacing is ~20% of the brush radius (same as CPU).
        /// </summary>
        public static void PaintLine(RenderTexture rt, Vector2 fromUv, Vector2 toUv, BrushSettings settings)
        {
            float dist = Vector2.Distance(fromUv, toUv);
            float spacing = Mathf.Max(0.002f, settings.radius * 0.2f);
            int steps = Mathf.Max(1, Mathf.CeilToInt(dist / spacing));

            for (int i = 0; i <= steps; i++)
            {
                float t = (float)i / steps;
                Vector2 uv = Vector2.Lerp(fromUv, toUv, t);
                Paint(rt, uv, settings);
            }
        }

        /// <summary>
        /// Release the internal temp RenderTexture. Call when the engine is no longer needed.
        /// </summary>
        public static void Cleanup()
        {
            if (_tempTexture != null)
            {
                _tempTexture.Release();
                _tempTexture = null;
            }
        }

        // ── Internal ─────────────────────────────────────────────────────

        private static void EnsureTempTexture(int width, int height)
        {
            if (_tempTexture != null &&
                (_tempTexture.width != width || _tempTexture.height != height))
            {
                _tempTexture.Release();
                _tempTexture = null;
            }

            if (_tempTexture == null)
            {
                _tempTexture = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBHalf)
                {
                    name = "GpuBrushEngine_Temp",
                    enableRandomWrite = true
                };
                _tempTexture.Create();
            }
        }
    }
}
