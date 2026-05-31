#if UNITY_EDITOR
using FullWorld;
using FullWorldEditor;
using ScreenSpaceBrush;
using UnityEditor;
using UnityEngine;

namespace FullWorldEditor.Demo
{
    /// <summary>
    /// MaskBrushTool 测试窗口。
    /// 菜单 Tools → Mask Brush Demo 打开。
    /// 创建伪地形 Mesh + 伪 Mask 数据，激活 MaskEditSession 后即可在 SceneView 中用笔刷绘制。
    /// </summary>
    public class MaskBrushDemoWindow : EditorWindow
    {
        #region Constants

        private const string kTerrainMeshName = "TerrainHeightMesh";
        private const int kDefaultResolution = 64;
        private const float kPlaneSize = 10f;
        private const string kMaskAssetPath = "Assets/Temp_MaskBrushDemo_Mask.asset";

        #endregion

        #region State

        private GameObject m_MeshObject;
        private MeshCollider m_Collider;
        private BaseMaskData m_Mask;
        private Material m_PreviewMaterial;
        private bool m_IsSetup;

        // Preview
        private Texture2D m_PreviewTex;
        private double m_LastRepaintTime;

        #endregion

        #region Menu

        [MenuItem("Tools/Mask Brush Demo")]
        private static void Open() => GetWindow<MaskBrushDemoWindow>("Mask Brush Demo");

        #endregion

        #region GUI

        private void OnGUI()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "1. 点击 Setup Demo Scene 创建伪数据\n" +
                "2. 点击 Activate Mask 激活笔刷\n" +
                "3. 在 SceneView 中 LMB 绘制，ESC 退出\n" +
                "4. 测试完毕点击 Cleanup Demo 清理",
                MessageType.Info);

            EditorGUILayout.Space(4);

            // ── Setup / Cleanup ──
            EditorGUI.BeginDisabledGroup(m_IsSetup);
            if (GUILayout.Button("Setup Demo Scene", GUILayout.Height(28)))
                SetupDemo();
            EditorGUI.EndDisabledGroup();

            EditorGUI.BeginDisabledGroup(!m_IsSetup);
            if (GUILayout.Button("Cleanup Demo", GUILayout.Height(28)))
                CleanupDemo();
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(6);

            // ── Activate / Deactivate ──
            EditorGUI.BeginDisabledGroup(!m_IsSetup);
            bool isActive = MaskEditSession.Instance.Current != null;

            if (!isActive)
            {
                if (GUILayout.Button("Activate Mask", GUILayout.Height(24)))
                    MaskEditSession.Instance.Activate(m_Mask);
            }
            else
            {
                GUI.backgroundColor = Color.red;
                if (GUILayout.Button("Deactivate", GUILayout.Height(24)))
                    MaskEditSession.Instance.Deactivate();
                GUI.backgroundColor = Color.white;
            }
            EditorGUI.EndDisabledGroup();

            EditorGUILayout.Space(6);

            // ── Status ──
            var session = MaskEditSession.Instance;
            EditorGUILayout.LabelField("Session Active", session.Current != null ? "✔ Yes" : "✘ No");
            EditorGUILayout.LabelField("Tool Active", MaskBrushTool.IsActive ? "✔ Yes" : "✘ No");
            if (m_Mask != null)
            {
                EditorGUILayout.LabelField("Mask Resolution", m_Mask.Resolution.ToString());
                EditorGUILayout.LabelField("Mask Length", m_Mask.m_Mask?.Length.ToString() ?? "null");
            }

            EditorGUILayout.Space(6);

            // ── Brush Settings ──
            DrawBrushSettings();

            EditorGUILayout.Space(6);

            // ── Mask Preview ──
            DrawMaskPreview();

            // Continuous repaint for live preview
            if (MaskBrushTool.IsActive)
            {
                double now = EditorApplication.timeSinceStartup;
                if (now - m_LastRepaintTime > 0.1)
                {
                    Repaint();
                    m_LastRepaintTime = now;
                }
            }
        }

        #endregion

        #region Brush Settings

        private void DrawBrushSettings()
        {
            EditorGUILayout.LabelField("Brush Settings", EditorStyles.boldLabel);

            var settings = MaskBrushTool.Settings;
            if (settings == null) return;

            EditorGUI.BeginChangeCheck();
            settings.mode = (BrushSettings.PaintMode)EditorGUILayout.EnumPopup("Mode", settings.mode);
            settings.radius = EditorGUILayout.Slider("Radius", settings.radius, 0.005f, 0.5f);
            settings.hardness = EditorGUILayout.Slider("Hardness", settings.hardness, 0f, 1f);
            settings.opacity = EditorGUILayout.Slider("Opacity", settings.opacity, 0.01f, 1f);
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();
        }

        #endregion

        #region Mask Preview

        private void DrawMaskPreview()
        {
            EditorGUILayout.LabelField("Mask Preview", EditorStyles.boldLabel);

            if (m_Mask == null || m_Mask.m_Mask == null)
            {
                EditorGUILayout.HelpBox("No mask data.", MessageType.Warning);
                return;
            }

            int res = m_Mask.Resolution;
            if (res <= 0) return;

            // Rebuild preview texture from mask float[]
            if (m_PreviewTex == null || m_PreviewTex.width != res)
            {
                if (m_PreviewTex != null) DestroyImmediate(m_PreviewTex);
                m_PreviewTex = new Texture2D(res, res, TextureFormat.RFloat, false, true);
            }

            var pixels = new Color[m_Mask.m_Mask.Length];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = new Color(m_Mask.m_Mask[i], 0f, 0f, 1f);
            m_PreviewTex.SetPixels(pixels);
            m_PreviewTex.Apply(false);

            // Display preview (scaled up for visibility)
            float previewSize = Mathf.Min(position.width - 20, 256);
            Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(previewRect, m_PreviewTex);

            // Stats
            float min = float.MaxValue, max = float.MinValue, sum = 0f;
            for (int i = 0; i < m_Mask.m_Mask.Length; i++)
            {
                float v = m_Mask.m_Mask[i];
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
            }
            EditorGUILayout.LabelField("Value Range", $"{min:F3} ~ {max:F3}  avg={sum / m_Mask.m_Mask.Length:F3}");
        }

        #endregion

        #region Setup / Cleanup

        private void SetupDemo()
        {
            // ── 1. 创建地形平面 Mesh ──
            m_MeshObject = GameObject.Find(kTerrainMeshName);
            if (m_MeshObject == null)
            {
                m_MeshObject = new GameObject(kTerrainMeshName);
                m_MeshObject.hideFlags = HideFlags.DontSave;

                var meshFilter = m_MeshObject.AddComponent<MeshFilter>();
                var meshRenderer = m_MeshObject.AddComponent<MeshRenderer>();

                // 生成带 UV 的平面网格
                meshFilter.sharedMesh = CreatePlaneMesh(kPlaneSize, kDefaultResolution);

                // 确保有 MeshCollider（射线检测必需）
                m_Collider = m_MeshObject.AddComponent<MeshCollider>();
                m_Collider.hideFlags = HideFlags.DontSave;

                // 预览材质：将 Mask 的 R 通道可视化
                m_PreviewMaterial = new Material(Shader.Find("Hidden/MaskBrushDemo_Preview"));
                m_PreviewMaterial.hideFlags = HideFlags.DontSave;
                meshRenderer.sharedMaterial = m_PreviewMaterial;
            }

            // ── 2. 创建伪 Mask 数据 ──
            m_Mask = AssetDatabase.LoadAssetAtPath<BaseMaskData>(kMaskAssetPath);
            if (m_Mask == null)
            {
                m_Mask = CreateInstance<BaseMaskData>();
                AssetDatabase.CreateAsset(m_Mask, kMaskAssetPath);
            }

            // 初始化 mask：左下角渐变 + 中心十字标记
            int res = kDefaultResolution;
            var maskData = new float[res * res];
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int idx = y * res + x;

                    // 背景渐变
                    float gradient = (float)y / (res - 1) * 0.3f;

                    // 中心十字
                    float cx = Mathf.Abs(x - res * 0.5f);
                    float cy = Mathf.Abs(y - res * 0.5f);
                    float cross = (cx < 2 || cy < 2) && cx < res * 0.3f && cy < res * 0.3f ? 0.8f : 0f;

                    maskData[idx] = Mathf.Clamp01(gradient + cross);
                }
            }
            m_Mask.m_Mask = maskData;
            m_Mask.MarkDirty();
            EditorUtility.SetDirty(m_Mask);
            AssetDatabase.SaveAssets();

            // 绑定 Mask PreviewRT 到材质
            UpdateMaterialTexture();

            m_IsSetup = true;

            // 聚焦 SceneView
            SceneView.FrameLastActiveSceneView();
            Debug.Log("[MaskBrushDemo] Demo scene set up. Click 'Activate Mask' to start painting.");
        }

        private void CleanupDemo()
        {
            MaskEditSession.Instance.Deactivate();

            if (m_MeshObject != null)
            {
                DestroyImmediate(m_MeshObject);
                m_MeshObject = null;
            }

            if (m_PreviewMaterial != null)
            {
                DestroyImmediate(m_PreviewMaterial);
                m_PreviewMaterial = null;
            }

            if (m_PreviewTex != null)
            {
                DestroyImmediate(m_PreviewTex);
                m_PreviewTex = null;
            }

            if (m_Mask != null)
            {
                m_Mask.ReleaseRT();
            }

            AssetDatabase.DeleteAsset(kMaskAssetPath);
            m_Mask = null;
            m_Collider = null;
            m_IsSetup = false;

            Debug.Log("[MaskBrushDemo] Demo cleaned up.");
        }

        #endregion

        #region Mesh Generation

        /// <summary>
        /// 生成一个 XZ 平面上的网格，带完整 UV 映射。
        /// </summary>
        private static Mesh CreatePlaneMesh(float size, int segments)
        {
            var mesh = new Mesh { name = "DemoPlane" };

            int vertCount = (segments + 1) * (segments + 1);
            var vertices = new Vector3[vertCount];
            var uvs = new Vector2[vertCount];
            var normals = new Vector3[vertCount];

            float half = size * 0.5f;
            float step = size / segments;

            for (int z = 0; z <= segments; z++)
            {
                for (int x = 0; x <= segments; x++)
                {
                    int idx = z * (segments + 1) + x;
                    vertices[idx] = new Vector3(-half + x * step, 0f, -half + z * step);
                    uvs[idx] = new Vector2((float)x / segments, (float)z / segments);
                    normals[idx] = Vector3.up;
                }
            }

            var triangles = new int[segments * segments * 6];
            int tri = 0;
            for (int z = 0; z < segments; z++)
            {
                for (int x = 0; x < segments; x++)
                {
                    int v0 = z * (segments + 1) + x;
                    int v1 = v0 + 1;
                    int v2 = (z + 1) * (segments + 1) + x;
                    int v3 = v2 + 1;

                    triangles[tri++] = v0;
                    triangles[tri++] = v2;
                    triangles[tri++] = v1;
                    triangles[tri++] = v1;
                    triangles[tri++] = v2;
                    triangles[tri++] = v3;
                }
            }

            mesh.vertices = vertices;
            mesh.uv = uvs;
            mesh.normals = normals;
            mesh.triangles = triangles;
            return mesh;
        }

        #endregion

        #region Material

        private void UpdateMaterialTexture()
        {
            if (m_PreviewMaterial != null && m_Mask != null && m_Mask.PreviewRT != null)
                m_PreviewMaterial.SetTexture("_MainTex", m_Mask.PreviewRT);
        }

        #endregion

        #region Lifecycle

        private void OnDestroy()
        {
            if (m_IsSetup)
                CleanupDemo();
        }

        #endregion
    }
}
#endif
