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
    /// 纯 GPU 流程：绘制 → UpdateFromRT → PreviewRT 实时更新 → 材质同步。
    /// </summary>
    public class MaskBrushDemoWindow : EditorWindow
    {
        #region Constants

        private const string kDemoPlaneName = "MaskBrushDemo_Plane";
        private const int kDefaultResolution = 64;
        private const float kPlaneSize = 10f;
        private const string kMaskAssetPath = "Assets/Temp_MaskBrushDemo_Mask.asset";

        #endregion

        #region State

        private GameObject m_MeshObject;
        private BaseMaskData m_Mask;
        private Material m_PreviewMaterial;
        private bool m_IsSetup;

        // 材质同步：UpdateFromRT 后 m_CachedRT 内容变化，
        // 但如果 RT 对象被重建，材质引用需要重新绑定
        private RenderTexture m_LastBoundRT;

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
            EditorGUILayout.LabelField("Session Active", MaskEditSession.Instance.Current != null ? "✔ Yes" : "✘ No");
            EditorGUILayout.LabelField("Tool Active", MaskBrushTool.IsActive ? "✔ Yes" : "✘ No");
            if (m_Mask != null)
            {
                EditorGUILayout.LabelField("Mask Resolution", m_Mask.Resolution.ToString());
                EditorGUILayout.LabelField("Mask RT", m_Mask.PreviewRT != null ? $"{m_Mask.PreviewRT.width}x{m_Mask.PreviewRT.height}" : "null");
            }

            EditorGUILayout.Space(6);

            // ── Brush Settings ──
            DrawBrushSettings();

            EditorGUILayout.Space(6);

            // ── Mask Preview (GPU RT 直出) ──
            DrawMaskPreview();

            // 持续重绘以保持预览同步 + 材质绑定
            if (MaskBrushTool.IsActive)
            {
                UpdateMaterialTexture();
                Repaint();
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
            EditorGUILayout.LabelField("Mask Preview (GPU RT)", EditorStyles.boldLabel);

            if (m_Mask == null)
            {
                EditorGUILayout.HelpBox("No mask data.", MessageType.Warning);
                return;
            }

            var rt = m_Mask.PreviewRT;
            if (rt == null)
            {
                EditorGUILayout.HelpBox("Preview RT is null.", MessageType.Warning);
                return;
            }

            float previewSize = Mathf.Min(position.width - 20, 256);
            Rect previewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
            EditorGUI.DrawPreviewTexture(previewRect, rt);

            EditorGUILayout.LabelField("Format", $"{rt.format}  {rt.width}x{rt.height}", EditorStyles.miniLabel);
        }

        #endregion

        #region Setup / Cleanup

        private void SetupDemo()
        {
            // ── 1. 创建地形平面 Mesh ──
            m_MeshObject = GameObject.Find(kDemoPlaneName);
            if (m_MeshObject == null)
            {
                m_MeshObject = new GameObject(kDemoPlaneName);
                m_MeshObject.hideFlags = HideFlags.DontSave;

                var meshFilter = m_MeshObject.AddComponent<MeshFilter>();
                var meshRenderer = m_MeshObject.AddComponent<MeshRenderer>();

                meshFilter.sharedMesh = CreatePlaneMesh(kPlaneSize, kDefaultResolution);

                // MeshCollider（射线检测必需）
                var collider = m_MeshObject.AddComponent<MeshCollider>();
                collider.hideFlags = HideFlags.DontSave;

                // 预览材质
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

            int res = kDefaultResolution;
            var maskData = new float[res * res];
            for (int y = 0; y < res; y++)
            {
                for (int x = 0; x < res; x++)
                {
                    int idx = y * res + x;
                    float gradient = (float)y / (res - 1) * 0.3f;
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

            if (m_Mask != null)
            {
                m_Mask.SyncToCpu();
                m_Mask.ReleaseRT();
            }

            AssetDatabase.DeleteAsset(kMaskAssetPath);
            m_Mask = null;
            m_IsSetup = false;

            Debug.Log("[MaskBrushDemo] Demo cleaned up.");
        }

        #endregion

        #region Mesh Generation

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

        /// <summary>
        /// 确保 Mesh 材质始终绑定到 Mask 的最新 PreviewRT。
        /// UpdateFromRT 在同一个 m_CachedRT 上 Blit，通常不需要重新绑定；
        /// 但如果 RT 被重建（分辨率变化等），需要重新 SetTexture。
        /// </summary>
        private void UpdateMaterialTexture()
        {
            if (m_PreviewMaterial == null || m_Mask == null) return;

            var rt = m_Mask.PreviewRT;
            if (rt != null && rt != m_LastBoundRT)
            {
                m_PreviewMaterial.SetTexture("_MainTex", rt);
                m_LastBoundRT = rt;
            }
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
