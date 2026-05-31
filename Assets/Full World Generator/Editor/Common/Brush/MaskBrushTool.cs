using FullWorld;
using ScreenSpaceBrush;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FullWorldEditor
{
    /// <summary>
    /// SceneView 笔刷工具，通过 MaskEditSession 桥接。
    /// 纯 GPU 流程：GpuBrushEngine 直接绘制到 mask.EnsureEditableRT()（即 m_CachedRT），
    /// 无中间 Blit，实现零延迟实时预览。
    /// 仅在 Deactivate 时 SyncToCpu 回写 m_Mask 用于持久化。
    /// </summary>
    [InitializeOnLoad]
    public static class MaskBrushTool
    {
        #region Fields

        private static bool s_IsActive;

        private static readonly BrushSettings s_BrushSettings = new BrushSettings
        {
            radius   = 0.05f,
            hardness = 0.5f,
            opacity  = 0.01f,
            mode     = BrushSettings.PaintMode.Modify,
            color    = Color.white
        };

        private static float s_TargetIntensity = 1f;

        // Stroke state
        private static Vector2 s_LastUV;
        private static bool s_HasLastUV;
        private static bool s_IsPainting;

        #endregion

        #region Initialization

        static MaskBrushTool()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            MaskEditSession.Instance.OnChanged += OnSessionChanged;
        }

        #endregion

        #region Session Callbacks

        private static void OnSessionChanged(BaseMaskData newMask)
        {
            s_IsActive = newMask != null;

            if (s_IsActive)
            {
                newMask.EnsureEditableRT();
                GpuBrushEngine.EnsureInitialized();
                BrushPreviewRenderer.InvalidateCache();
            }
            else
            {
                // MaskEditSession 已在 Deactivate/Activate 切换时负责 ForceSaveCurrent
                BrushPreviewRenderer.InvalidateCache();
            }

            SceneView.RepaintAll();
        }

        #endregion

        #region SceneView GUI

        private static void OnSceneGUI(SceneView sceneView)
        {
            if (!s_IsActive || MaskEditSession.Instance.Current == null)
                return;

            var e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            // Esc 退出编辑
            if (e.type == EventType.KeyDown && e.keyCode == KeyCode.Escape)
            {
                MaskEditSession.Instance.Deactivate();
                e.Use();
                return;
            }

            // 拦截 Unity 全局 Undo/Redo 命令，改用自定义 GPU Undo
            // ValidateCommand 阶段就消费掉，阻止 Unity 内置序列化 Undo 执行
            if (e.type == EventType.ValidateCommand && e.commandName == "Undo")
            {
                e.Use();
                return;
            }
            if (e.type == EventType.ExecuteCommand && e.commandName == "Undo")
            {
                if (MaskEditSession.Instance.PerformUndo())
                    MaskEditSession.Instance.NotifyStrokePainted();
                e.Use();
                SceneView.RepaintAll();
                return;
            }

            // Ctrl+Z 键盘事件兜底
            if (e.type == EventType.KeyDown && e.control && e.keyCode == KeyCode.Z && !e.shift)
            {
                if (MaskEditSession.Instance.PerformUndo())
                    MaskEditSession.Instance.NotifyStrokePainted();
                e.Use();
                SceneView.RepaintAll();
                return;
            }

            // S/B/E 切换笔刷模式，消耗事件以免触发其他操作
            if (e.type == EventType.KeyDown && !e.alt && !e.control)
            {
                bool modeChanged = false;
                switch (e.keyCode)
                {
                    case KeyCode.S:
                        s_BrushSettings.mode = BrushSettings.PaintMode.Smooth;
                        modeChanged = true;
                        break;
                    case KeyCode.B:
                        s_BrushSettings.mode = BrushSettings.PaintMode.Modify;
                        modeChanged = true;
                        break;
                    case KeyCode.E:
                        s_BrushSettings.mode = BrushSettings.PaintMode.Erase;
                        modeChanged = true;
                        break;
                }
                if (modeChanged)
                {
                    e.Use();
                    SceneView.RepaintAll();
                    return;
                }
            }

            // 先绘制面板 — 让面板控件优先处理鼠标事件
            var mask = MaskEditSession.Instance.Current;
            BrushSettingsPanel.Draw(sceneView, s_BrushSettings, mask?.EffectiveResolution ?? 0,
                mask?.PreviewRT, OnDoneClicked, OnResolutionChanged, OnUndoClicked);

            // 面板消费了事件则跳过笔刷逻辑
            bool mouseOverPanel = BrushSettingsPanel.PanelRect.Contains(e.mousePosition);
            if (e.type == EventType.Used || mouseOverPanel)
            {
                if (e.GetTypeForControl(controlId) == EventType.Layout)
                    HandleUtility.AddDefaultControl(controlId);
                return;
            }

            // 射线检测 — Stage 场景的独立物理世界
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            bool hasHit = RaycastInActiveContext(ray, out RaycastHit hitInfo);

            if (hasHit)
            {
                if (!s_IsPainting)
                    BrushPreviewRenderer.Draw(hitInfo, s_BrushSettings.radius, s_BrushSettings.hardness, s_BrushSettings.opacity);
                HandlePaintingInput(e, hitInfo, controlId);
            }

            if (e.GetTypeForControl(controlId) == EventType.Layout)
                HandleUtility.AddDefaultControl(controlId);

            // 滚轮快捷键 — Shift 时 Unity 将 delta 搬到 X 轴，读 e.delta.x
            if (e.type == EventType.ScrollWheel && e.shift)
            {
                float delta = -e.delta.x;

                if (e.control)
                    s_BrushSettings.hardness = Mathf.Clamp01(s_BrushSettings.hardness + delta * 0.02f);
                else
                    s_BrushSettings.radius = Mathf.Clamp(s_BrushSettings.radius + delta * 0.005f, 0.005f, 0.5f);

                e.Use();
                SceneView.RepaintAll();
            }

            // 持续重绘
            if (hasHit || s_IsPainting)
                SceneView.RepaintAll();
        }

        private static void OnDoneClicked()
        {
            MaskEditSession.Instance.Deactivate();
        }

        private static void OnUndoClicked()
        {
            MaskEditSession.Instance.PerformUndo();
            MaskEditSession.Instance.NotifyStrokePainted();
            SceneView.RepaintAll();
        }

        private static void OnResolutionChanged(int newResolution)
        {
            var mask = MaskEditSession.Instance.Current;
            if (mask == null) return;
            mask.Resize(newResolution);
        }

        #endregion

        #region Input Handlers

        private static void HandlePaintingInput(Event e, RaycastHit hitInfo, int controlId)
        {
            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when e.button == 0 && !e.alt && !e.control:
                    BeginStroke(hitInfo);
                    e.Use();
                    break;

                case EventType.MouseDrag when e.button == 0 && !e.alt && !e.control:
                    ContinueStroke(hitInfo);
                    e.Use();
                    break;

                case EventType.MouseUp when e.button == 0:
                    EndStroke();
                    break;
            }
        }

        private static void BeginStroke(RaycastHit hitInfo)
        {
            var mask = MaskEditSession.Instance.Current;
            if (mask == null) return;

            // 笔划开始前捕获 Undo 快照（1 等分辨率 + 5 低分辨率）
            MaskEditSession.Instance.CaptureUndoSnapshot();

            s_IsPainting = true;
            s_HasLastUV = false;

            Vector2 uv = hitInfo.textureCoord;
            PaintStamp(mask, uv);

            s_LastUV = uv;
            s_HasLastUV = true;
        }

        private static void ContinueStroke(RaycastHit hitInfo)
        {
            if (!s_IsPainting) return;

            var mask = MaskEditSession.Instance.Current;
            if (mask == null) return;

            Vector2 uv = hitInfo.textureCoord;

            if (s_HasLastUV)
                PaintLine(mask, s_LastUV, uv);
            else
                PaintStamp(mask, uv);

            s_LastUV = uv;
            s_HasLastUV = true;
        }

        private static void EndStroke()
        {
            if (!s_IsPainting) return;
            s_IsPainting = false;
            s_HasLastUV = false;
        }

        #endregion

        #region Paint Dispatch

        private static void SyncColorToIntensity()
        {
            float v = s_TargetIntensity;
            s_BrushSettings.color = new Color(v, v, v, 1f);
        }

        private static void PaintStamp(BaseMaskData mask, Vector2 uv)
        {
            var rt = mask.EnsureEditableRT();
            if (rt == null) return;
            SyncColorToIntensity();
            // Smooth 模式固定 opacity=1，Modify/Erase 使用面板值
            float savedOpacity = s_BrushSettings.opacity;
            if (s_BrushSettings.mode == BrushSettings.PaintMode.Smooth)
                s_BrushSettings.opacity = 1f;
            GpuBrushEngine.Paint(rt, uv, s_BrushSettings);
            s_BrushSettings.opacity = savedOpacity;
            MaskEditSession.Instance.NotifyStrokePainted();
        }

        private static void PaintLine(BaseMaskData mask, Vector2 fromUv, Vector2 toUv)
        {
            var rt = mask.EnsureEditableRT();
            if (rt == null) return;
            SyncColorToIntensity();
            float savedOpacity = s_BrushSettings.opacity;
            if (s_BrushSettings.mode == BrushSettings.PaintMode.Smooth)
                s_BrushSettings.opacity = 1f;
            GpuBrushEngine.PaintLine(rt, fromUv, toUv, s_BrushSettings);
            s_BrushSettings.opacity = savedOpacity;
            MaskEditSession.Instance.NotifyStrokePainted();
        }

        #endregion

        #region Raycast

        private static bool RaycastInActiveContext(Ray ray, out RaycastHit hitInfo)
        {
            var stage = StageUtility.GetCurrentStage() as PreviewSceneStage;
            if (stage != null && stage.scene.IsValid() && stage.scene.isLoaded)
            {
                var physicsScene = stage.scene.GetPhysicsScene();
                if (physicsScene.IsValid() && physicsScene.Raycast(ray.origin, ray.direction, out hitInfo, float.MaxValue))
                    return true;
            }

            hitInfo = default;
            return false;
        }

        #endregion

        #region Public Accessors

        public static BrushSettings Settings => s_BrushSettings;
        public static bool IsActive => s_IsActive;
        public static float TargetIntensity
        {
            get => s_TargetIntensity;
            set => s_TargetIntensity = Mathf.Clamp01(value);
        }

        #endregion
    }
}
