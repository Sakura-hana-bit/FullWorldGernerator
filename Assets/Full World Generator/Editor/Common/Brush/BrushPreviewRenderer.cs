using UnityEngine;
using UnityEditor;

namespace FullWorldEditor
{
    /// <summary>
    /// 笔刷预览渲染器。在 SceneView 中绘制笔刷范围圆圈和法线指示器。
    /// 缓存 Mesh 的 UV→World 映射比例，避免每帧扫描 UV 数组。
    /// 颜色透明度随 Opacity 变化。
    /// </summary>
    public static class BrushPreviewRenderer
    {
        // ── 缓存 ──────────────────────────────────────────────────

        private static Mesh s_CachedMesh;
        private static float s_CachedScale;
        private static Vector3 s_CachedLossyScale;

        /// <summary>清除缓存，在编辑会话切换时调用。</summary>
        public static void InvalidateCache()
        {
            s_CachedMesh = null;
        }

        // ── 公开 API ──────────────────────────────────────────────

        /// <summary>
        /// 绘制笔刷预览：外圈（全半径）+ 内圈（硬度边界）+ 法线指示线。
        /// 颜色透明度随 opacity 变化。
        /// </summary>
        public static void Draw(RaycastHit hit, float uvRadius, float hardness, float opacity)
        {
            float worldRadius = EstimateWorldRadius(hit, uvRadius);

            // 透明度：opacity 0→0.5, 1→1.0，最低不会看不见
            float alpha = Mathf.Lerp(0.5f, 1f, opacity);

            // 外圈
            Handles.color = new Color(0f, 1f, 1f, alpha);
            Handles.DrawWireDisc(hit.point, hit.normal, worldRadius);

            // 内圈（硬度边界）
            if (hardness < 1f)
            {
                float innerRadius = worldRadius * hardness;
                Handles.color = new Color(0f, 1f, 1f, alpha * 0.6f);
                Handles.DrawWireDisc(hit.point, hit.normal, innerRadius);
            }

            // 法线指示线
            Handles.color = new Color(1f, 1f, 0f, alpha);
            Handles.DrawLine(hit.point, hit.point + hit.normal * worldRadius * 0.3f);
        }

        // ── 内部 ──────────────────────────────────────────────────

        /// <summary>
        /// 根据 Mesh 的 UV→World 映射估算笔刷在世界空间中的半径。
        /// 结果按 Mesh + lossyScale 缓存，避免每帧遍历 UV 数组。
        /// </summary>
        private static float EstimateWorldRadius(RaycastHit hit, float uvRadius)
        {
            var meshFilter = hit.collider.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return uvRadius * 100f;

            Mesh mesh = meshFilter.sharedMesh;
            Vector3 lossyScale = hit.collider.transform.lossyScale;

            // 命中缓存：同一 Mesh 且 lossyScale 未变时复用
            if (s_CachedMesh == mesh && s_CachedLossyScale == lossyScale)
                return uvRadius * s_CachedScale * lossyScale.magnitude;

            // 首次或 Mesh 变更：扫描 UV 计算映射比例
            Vector2[] uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0)
            {
                s_CachedScale = 100f;
                s_CachedMesh = mesh;
                s_CachedLossyScale = lossyScale;
                return uvRadius * s_CachedScale * lossyScale.magnitude;
            }

            Vector2 uvMin = new Vector2(float.MaxValue, float.MaxValue);
            Vector2 uvMax = new Vector2(float.MinValue, float.MinValue);

            int sampleCount = Mathf.Min(uvs.Length, 1000);
            for (int i = 0; i < sampleCount; i++)
            {
                uvMin = Vector2.Min(uvMin, uvs[i]);
                uvMax = Vector2.Max(uvMax, uvs[i]);
            }

            float uvSpan = Mathf.Max(uvMax.x - uvMin.x, uvMax.y - uvMin.y);
            if (uvSpan < 0.001f) uvSpan = 1f;

            Bounds meshBounds = mesh.bounds;
            float worldSpan = meshBounds.size.magnitude;

            s_CachedScale = worldSpan / uvSpan;
            s_CachedMesh = mesh;
            s_CachedLossyScale = lossyScale;

            return uvRadius * s_CachedScale * lossyScale.magnitude;
        }
    }
}
