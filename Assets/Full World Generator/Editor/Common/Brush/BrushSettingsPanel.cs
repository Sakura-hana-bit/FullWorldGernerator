using ScreenSpaceBrush;
using UnityEditor;
using UnityEngine;

namespace FullWorldEditor
{
    /// <summary>
    /// SceneView 右下角浮动笔刷设置面板。
    /// 自包含样式，外部只需调用 Draw()。
    /// </summary>
    public static class BrushSettingsPanel
    {
        // ── 样式缓存 ──────────────────────────────────────────────

        private static GUIStyle s_PanelStyle;
        private static GUIStyle s_PanelTitleStyle;
        private static Texture2D s_PanelBg;

        // ── 可选分辨率 ────────────────────────────────────────────

        private static readonly int[] s_ResolutionOptions = { 32, 64, 128, 256, 512, 1024, 2048 };
        private static readonly string[] s_ResolutionLabels = { "32", "64", "128", "256", "512", "1024", "2048" };

        // ── 滑条标签宽度 ──────────────────────────────────────────

        private const float k_LabelWidth = 60f;
        private const float k_FieldWidth = 40f;

        // ── 面板矩形（供外部检测鼠标是否在面板上）──────────────

        private static Rect s_LastPanelRect;

        /// <summary>上一帧面板的屏幕矩形，鼠标在此区域内时应屏蔽笔刷输入。</summary>
        public static Rect PanelRect => s_LastPanelRect;

        // ── 公开 API ──────────────────────────────────────────────

        /// <summary>
        /// 在 SceneView 中绘制浮动设置面板。
        /// </summary>
        public static void Draw(SceneView sceneView, BrushSettings settings, int currentResolution,
            RenderTexture previewRT, System.Action onDone, System.Action<int> onResolutionChanged)
        {
            Draw(sceneView, settings, currentResolution, previewRT, onDone, onResolutionChanged, null);
        }

        /// <summary>
        /// 在 SceneView 中绘制浮动设置面板（带 Undo 按钮）。
        /// </summary>
        public static void Draw(SceneView sceneView, BrushSettings settings, int currentResolution,
            RenderTexture previewRT, System.Action onDone, System.Action<int> onResolutionChanged,
            System.Action onUndo)
        {
            if (sceneView == null) return;

            Handles.BeginGUI();

            float panelWidth = 230f;
            float panelHeight = 330f;
            float margin = 10f;

            float x = sceneView.position.width - panelWidth - margin;
            float y = sceneView.position.height - panelHeight - margin;
            var panelRect = new Rect(x, y, panelWidth, panelHeight);
            s_LastPanelRect = panelRect;

            EnsureStyles();
            GUILayout.BeginArea(panelRect, s_PanelStyle);

            // ── 标题 + Undo + Done ──
            EditorGUILayout.BeginHorizontal();
            GUILayout.Label("Brush Settings", s_PanelTitleStyle);
            if (GUILayout.Button("Undo", GUILayout.Width(42), GUILayout.Height(18)))
            {
                onUndo?.Invoke();
                Handles.EndGUI();
                GUILayout.EndArea();
                return;
            }
            if (GUILayout.Button("Done", GUILayout.Width(52), GUILayout.Height(18)))
            {
                onDone?.Invoke();
                Handles.EndGUI();
                GUILayout.EndArea();
                return;
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(2);

            // ── 分辨率 ──
            EditorGUI.BeginChangeCheck();
            currentResolution = EditorGUILayout.IntPopup("Resolution", currentResolution, s_ResolutionLabels, s_ResolutionOptions);
            if (EditorGUI.EndChangeCheck())
            {
                onResolutionChanged?.Invoke(currentResolution);
                SceneView.RepaintAll();
            }

            // ── 笔刷参数 ──
            EditorGUI.BeginChangeCheck();
            settings.mode = (BrushSettings.PaintMode)EditorGUILayout.EnumPopup("Mode", settings.mode);

            settings.radius = DrawSlider("Radius", settings.radius, 0.005f, 0.5f);
            settings.hardness = DrawSlider("Hardness", settings.hardness, 0f, 1f);
            settings.opacity = DrawSlider("Opacity", settings.opacity, 0.01f, 0.2f);
            MaskBrushTool.TargetIntensity = DrawSlider("Intensity", MaskBrushTool.TargetIntensity, 0f, 1f);
            if (EditorGUI.EndChangeCheck())
                SceneView.RepaintAll();

            EditorGUILayout.Space(4);

            // ── Mask 预览 ──
            if (previewRT != null)
            {
                float previewSize = 64f;
                var previewRect = GUILayoutUtility.GetRect(previewSize, previewSize, GUILayout.ExpandWidth(false));
                EditorGUI.DrawPreviewTexture(previewRect, previewRT);
            }

            EditorGUILayout.Space(2);

            // ── 操作提示 ──
            EditorGUILayout.Space(2);
            var helpStyle = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleLeft };
            EditorGUILayout.LabelField("LMB: Paint  |  Alt+LMB: Orbit", helpStyle);
            EditorGUILayout.LabelField("B: Modify  |  E: Erase  |  S: Smooth", helpStyle);
            EditorGUILayout.LabelField("Shift+Scroll: Radius", helpStyle);
            EditorGUILayout.LabelField("Shift+Ctrl+Scroll: Hardness", helpStyle);
            EditorGUILayout.LabelField("Ctrl+Z: Undo  |  ESC / Done: Exit", helpStyle);

            GUILayout.EndArea();
            Handles.EndGUI();
        }

        // ── 自定义滑条（带数值显示）────────────────────────────

        private static float DrawSlider(string label, float value, float min, float max)
        {
            var rect = EditorGUILayout.GetControlRect(false, 18f);
            var labelRect = new Rect(rect.x, rect.y, k_LabelWidth, rect.height);
            var fieldRect = new Rect(rect.x + rect.width - k_FieldWidth, rect.y, k_FieldWidth, rect.height);
            var sliderRect = new Rect(labelRect.xMax + 2f, rect.y, fieldRect.x - labelRect.xMax - 6f, rect.height);

            GUI.Label(labelRect, label);

            EditorGUI.BeginChangeCheck();
            float newVal = GUI.HorizontalSlider(sliderRect, value, min, max);
            if (EditorGUI.EndChangeCheck())
                newVal = Mathf.Clamp(newVal, min, max);

            newVal = EditorGUI.FloatField(fieldRect, newVal);
            newVal = Mathf.Clamp(newVal, min, max);

            return newVal;
        }

        // ── 内部 ──────────────────────────────────────────────────

        private static void EnsureStyles()
        {
            if (s_PanelStyle != null) return;

            s_PanelStyle = new GUIStyle(EditorStyles.helpBox);

            Color bg = EditorGUIUtility.isProSkin
                ? new Color(0.22f, 0.22f, 0.22f, 0.95f)
                : new Color(0.76f, 0.76f, 0.76f, 0.95f);

            s_PanelBg = new Texture2D(1, 1);
            s_PanelBg.SetPixel(0, 0, bg);
            s_PanelBg.Apply();
            s_PanelStyle.normal.background = s_PanelBg;
            s_PanelStyle.onNormal.background = s_PanelBg;
            s_PanelStyle.padding = new RectOffset(8, 8, 6, 6);

            s_PanelTitleStyle = new GUIStyle(EditorStyles.boldLabel)
            {
                alignment = TextAnchor.MiddleLeft
            };
        }
    }
}
