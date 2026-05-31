using System;
using UnityEngine;

namespace FullWorld
{
    public class BaseMaskData : ScriptableObject
    {
        [SerializeField, HideInInspector] public float[] m_Mask;

        // ================================================================
        //  Preview RT Cache (non-serialized)
        // ================================================================

        [NonSerialized] private RenderTexture m_CachedRT;
        [NonSerialized] private bool m_IsDirty = true;

        // Bicubic resample compute shader (lazy loaded)
        private static ComputeShader s_BicubicCS;
        private static int s_BicubicKernel;

        /// <summary>
        /// Marks the preview RT as dirty, forcing regeneration on next access.
        /// Call this after modifying <see cref="m_Mask"/> (CPU side).
        /// </summary>
        public void MarkDirty() => m_IsDirty = true;

        /// <summary>
        /// Clears the dirty flag without regenerating. Use after directly modifying
        /// m_CachedRT (GPU side, e.g. Undo restore) to prevent PreviewRT from
        /// overwriting the restored GPU data with stale CPU data on next access.
        /// </summary>
        public void ClearDirty() => m_IsDirty = false;

        /// <summary>
        /// Lazily generated preview RenderTexture from <see cref="m_Mask"/>.
        /// Returns null when no mask data is available.
        /// Automatically regenerates when dirty or when the cached RT is destroyed.
        /// Format: ARGBFloat for GPU compute-shader compatibility (RWTexture2D&lt;float4&gt;).
        /// </summary>
        public RenderTexture PreviewRT
        {
            get
            {
                if (m_Mask == null || m_Mask.Length == 0)
                {
                    ReleaseRT();
                    return null;
                }

                if (!m_IsDirty && m_CachedRT != null)
                    return m_CachedRT;

                GenerateRT();
                m_IsDirty = false;
                return m_CachedRT;
            }
        }

        // ================================================================
        //  Properties
        // ================================================================

        /// <summary>m_Mask 序列化数据的分辨率（SyncToCpu 后与 EffectiveResolution 一致）。</summary>
        public int Resolution => m_Mask != null ? (int)Math.Sqrt(m_Mask.Length) : 0;

        /// <summary>
        /// 当前实际生效的分辨率。编辑期间取 RT 宽度（可能已被 Resize 改变），
        /// 无 RT 时回退到 m_Mask 的分辨率。
        /// </summary>
        public int EffectiveResolution => m_CachedRT != null ? m_CachedRT.width : Resolution;

        // ================================================================
        //  GPU Editing Path
        // ================================================================

        /// <summary>
        /// 确保 m_CachedRT 已就绪且可用于 GpuBrushEngine 直接绘制。
        /// 首次调用时会从 m_Mask 生成 RT；后续调用若 RT 仍有效则直接返回。
        /// 绘制期间应使用此 RT，避免中间 Blit。
        /// </summary>
        public RenderTexture EnsureEditableRT()
        {
            // PreviewRT 会处理 dirty check 和 RT 分配
            return PreviewRT;
        }

        /// <summary>
        /// 将 m_CachedRT 缩放到新分辨率。使用 GPU Bicubic（Catmull-Rom）重采样。
        /// 缩放后 m_CachedRT 即为新分辨率，m_IsDirty = false，
        /// 下次 SyncToCpu 时 m_Mask 会以新分辨率覆盖。
        /// </summary>
        public void Resize(int newResolution)
        {
            if (newResolution <= 0) return;

            // 若当前无 RT，从 m_Mask 以新分辨率生成空白 RT
            if (m_CachedRT == null)
            {
                m_CachedRT = new RenderTexture(newResolution, newResolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                    filterMode = FilterMode.Bilinear,
                };
                m_CachedRT.Create();

                var prev = RenderTexture.active;
                RenderTexture.active = m_CachedRT;
                GL.Clear(true, true, Color.white);
                RenderTexture.active = prev;

                m_IsDirty = false;
                return;
            }

            // 同分辨率无需操作
            if (m_CachedRT.width == newResolution)
                return;

            // GPU Bicubic 重采样
            EnsureBicubicCS();
            var newRT = new RenderTexture(newResolution, newResolution, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            newRT.Create();

            if (s_BicubicCS != null)
            {
                s_BicubicCS.SetTexture(s_BicubicKernel, "_Source", m_CachedRT);
                s_BicubicCS.SetTexture(s_BicubicKernel, "_Output", newRT);
                s_BicubicCS.SetVector("_SrcSize", new Vector4(m_CachedRT.width, m_CachedRT.height, 0, 0));
                s_BicubicCS.SetVector("_DstSize", new Vector4(newResolution, newResolution, 0, 0));
                int groups = Mathf.CeilToInt(newResolution / 8f);
                s_BicubicCS.Dispatch(s_BicubicKernel, groups, groups, 1);
            }
            else
            {
                // Fallback: bilinear Blit
                var prevActive = RenderTexture.active;
                RenderTexture.active = newRT;
                Graphics.Blit(m_CachedRT, newRT);
                RenderTexture.active = prevActive;
            }

            // 替换
            m_CachedRT.Release();
            DestroyImmediate(m_CachedRT);
            m_CachedRT = newRT;

            m_IsDirty = false; // 阻止 PreviewRT 从旧 m_Mask 重新生成
        }

        /// <summary>
        /// 将 m_CachedRT 回读到 m_Mask（CPU 序列化路径）。
        /// 仅在需要持久化时调用（Deactivate / Save）。
        /// </summary>
        public void SyncToCpu()
        {
            if (m_CachedRT == null) return;

            int res = m_CachedRT.width;

            var prev = RenderTexture.active;
            RenderTexture.active = m_CachedRT;
            var tex = new Texture2D(res, res, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, res, res), 0, 0);
            var pixels = tex.GetPixels();
            DestroyImmediate(tex);
            RenderTexture.active = prev;

            if (m_Mask == null || m_Mask.Length != pixels.Length)
                m_Mask = new float[pixels.Length];

            for (int i = 0; i < pixels.Length; i++)
                m_Mask[i] = pixels[i].r;
        }

        // ================================================================
        //  RT Generation (CPU → GPU)
        // ================================================================

        private void GenerateRT()
        {
            ReleaseRT();

            int res = Resolution;
            if (res <= 0) return;

            // float[] → Texture2D → Blit → RenderTexture
            var tex = new Texture2D(res, res, TextureFormat.RGBAFloat, false, true);
            var pixels = new Color[m_Mask.Length];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(m_Mask[i], m_Mask[i], m_Mask[i], 1f);
            tex.SetPixels(pixels);
            tex.Apply(false, true);

            m_CachedRT = new RenderTexture(res, res, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear,
            };
            m_CachedRT.Create();

            var prev = RenderTexture.active;
            RenderTexture.active = m_CachedRT;
            Graphics.Blit(tex, m_CachedRT);
            RenderTexture.active = prev;

            DestroyImmediate(tex);
        }

        public void ReleaseRT()
        {
            if (m_CachedRT != null)
            {
                m_CachedRT.Release();
                DestroyImmediate(m_CachedRT);
                m_CachedRT = null;
            }
        }

        private static void EnsureBicubicCS()
        {
            if (s_BicubicCS != null) return;
            s_BicubicCS = Resources.Load<ComputeShader>("Shader/BicubicResampleCS");
            if (s_BicubicCS != null)
                s_BicubicKernel = s_BicubicCS.FindKernel("CSBicubicResample");
        }

        private void OnDisable() => ReleaseRT();

        private void OnDestroy() => ReleaseRT();

    }
}
