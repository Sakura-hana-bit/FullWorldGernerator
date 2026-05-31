#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using UnityEditor.EditorTools;

namespace ScreenSpaceBrush
{
    /// <summary>
    /// Global SceneView brush tool. Raycasts from the mouse, finds or auto-creates
    /// an IBrushTarget, and delegates painting to the selected engine (CPU or GPU).
    /// Activated from the SceneView toolbar.
    /// </summary>
    [EditorTool("Screen Space Brush")]
    public class SceneViewBrushTool : EditorTool
    {
        #region Nested Types

        public enum PaintEngine
        {
            CPU,
            GPU
        }

        #endregion

        #region Public Settings

        public BrushSettings brushSettings = new BrushSettings();

        [Tooltip("Painting engine: CPU (Texture2D) or GPU (RenderTexture + Compute Shader).")]
        public PaintEngine engine = PaintEngine.GPU;

        [Tooltip("Auto-add PaintableComponent to meshes without an IBrushTarget.")]
        public bool autoCreateTarget = true;

        #endregion

        #region Stroke State

        private Vector2 _lastUv;
        private bool _hasLastUv;
        private bool _isPainting;
        private IBrushTarget _currentTarget;
        private bool _usingGpu;

        #endregion

        #region Stroke Debounce

        // Delays SyncGpuToCpu + AutoSave until mouse inactivity,
        // so rapid clicks continue the same stroke without stutter.
        private bool _mouseIsDown;
        private bool _strokeEndPending;
        private double _strokeDebounceTime;
        private bool _strokeSessionActive;
        private const double StrokeEndDelaySeconds = 0.5;

        #endregion

        #region Time-Based Accumulation

        // When the mouse is held stationary, keeps painting at reduced opacity
        // to simulate paint buildup at the cursor position.
        private double _lastStampTime;
        private const double AccumulationInterval = 1.0 / 15.0; // ~15 stamps/sec
        private const float AccumulationOpacityScale = 0.3f;
        private BrushSettings _accumulationSettings;

        #endregion

        #region EditorTool Lifecycle

        public override GUIContent toolbarIcon =>
            EditorGUIUtility.TrIconContent("CircularBrush", "Screen Space Brush Tool");

        public override void OnActivated()
        {
            base.OnActivated();
            ResetState();
            if (engine == PaintEngine.GPU)
                GpuBrushEngine.EnsureInitialized();
            Debug.Log("[ScreenSpaceBrush] Tool activated. Click on any mesh to paint.");
        }

        public override void OnWillBeDeactivated()
        {
            base.OnWillBeDeactivated();
            EndStroke();
            _currentTarget = null;
            Debug.Log("[ScreenSpaceBrush] Tool deactivated");
        }

        #endregion

        #region Main Input Loop

        public override void OnToolGUI(EditorWindow window)
        {
            if (!(window is SceneView)) return;

            Event e = Event.current;
            int controlId = GUIUtility.GetControlID(FocusType.Passive);

            HandleAccumulationAndDebounce();

            // Raycast from mouse position
            Ray ray = HandleUtility.GUIPointToWorldRay(e.mousePosition);
            bool hasHit = Physics.Raycast(ray, out RaycastHit hitInfo, float.MaxValue);

            if (hasHit)
            {
                DrawBrushPreview(hitInfo);
                HandlePaintingInput(e, hitInfo, controlId);
            }
            else
            {
                HandleMouseUpOutsideHit(e);
            }

            if (e.GetTypeForControl(controlId) == EventType.Layout)
                HandleUtility.AddDefaultControl(controlId);

            // Force continuous repaints for accumulation and debounce timer
            if ((_isPainting && _mouseIsDown) || _strokeEndPending)
                SceneView.RepaintAll();

            DrawSettingsPanel();
        }

        #endregion

        #region Input Handlers

        private void HandleAccumulationAndDebounce()
        {
            if (Event.current.type != EventType.Repaint) return;

            double now = EditorApplication.timeSinceStartup;

            // Accumulation: paint at current position when mouse is held still
            if (_isPainting && _mouseIsDown && _hasLastUv && _currentTarget != null)
            {
                if (now - _lastStampTime >= AccumulationInterval)
                {
                    EnsureAccumulationSettings();
                    ApplyPaintStamp(_lastUv, _accumulationSettings);
                    _lastStampTime = now;
                }
            }

            // Debounced stroke end
            if (_strokeEndPending && now - _strokeDebounceTime >= StrokeEndDelaySeconds)
                EndStroke();
        }

        private void HandlePaintingInput(Event e, RaycastHit hitInfo, int controlId)
        {
            switch (e.GetTypeForControl(controlId))
            {
                case EventType.MouseDown when e.button == 0 && !e.alt && !e.control:
                    HandleMouseDown(hitInfo);
                    e.Use();
                    break;

                case EventType.MouseDrag when e.button == 0 && !e.alt && !e.control:
                    HandleMouseDrag(hitInfo);
                    e.Use();
                    break;

                case EventType.MouseUp when e.button == 0:
                    HandleMouseUp();
                    break;
            }
        }

        private void HandleMouseUpOutsideHit(Event e)
        {
            if (e.type == EventType.MouseUp && e.button == 0)
                HandleMouseUp();
        }

        private void HandleMouseDown(RaycastHit hitInfo)
        {
            // Cancel any pending stroke end (rapid click continuation)
            _strokeEndPending = false;
            _mouseIsDown = true;

            IBrushTarget target = null;// t = GetOrCreateBrushTarget(hitInfo.collider.gameObject);
            if (target == null) return;

            // ── Session management ──
            bool shouldResolveEngine = false;

            if (!_strokeSessionActive)
            {
                // New stroke session
                _isPainting = true;
                _currentTarget = target;
                _hasLastUv = false;
                target.OnStrokeBegin();
                _strokeSessionActive = true;
                shouldResolveEngine = true;
            }
            else if (_currentTarget != target)
            {
                // Target changed within the same stroke session
                if (_currentTarget != null)
                {
                    if (_usingGpu && _currentTarget is IGpuBrushTarget prevGpu)
                        prevGpu.SyncGpuToCpu();
                    _currentTarget.OnStrokeEnd();
                }
                _currentTarget = target;
                _hasLastUv = false;
                target.OnStrokeBegin();
                shouldResolveEngine = true;
            }

            // ── Resolve paint engine (CPU vs GPU) ──
            if (shouldResolveEngine)
            {
                _usingGpu = engine == PaintEngine.GPU
                            && _currentTarget is IGpuBrushTarget
                            && GpuBrushEngine.EnsureInitialized();

                // If GPU is selected but RenderTexture is unavailable, fall back to CPU
                if (_usingGpu)
                {
                    var rt = (_currentTarget as IGpuBrushTarget)?.GetPaintRenderTexture();
                    if (rt == null)
                        _usingGpu = false;
                }
            }

            // ── Apply first stamp ──
            ApplyPaintStamp(hitInfo.textureCoord, brushSettings);
        }

        private void HandleMouseDrag(RaycastHit hitInfo)
        {
            if (!_isPainting || _currentTarget == null) return;

            Vector2 uv = hitInfo.textureCoord;

            if (_hasLastUv)
                ApplyPaintLine(_lastUv, uv, brushSettings);
            else
                ApplyPaintStamp(uv, brushSettings);
        }

        private void HandleMouseUp()
        {
            _mouseIsDown = false;

            if (_isPainting)
            {
                // Debounce: don't end stroke immediately on mouse-up
                // so rapid clicks continue the same stroke
                _strokeEndPending = true;
                _strokeDebounceTime = EditorApplication.timeSinceStartup;
            }
        }

        #endregion

        #region Paint Application

        /// <summary>
        /// Apply a single paint stamp at the given UV. Handles GPU/CPU dispatch.
        /// </summary>
        private void ApplyPaintStamp(Vector2 uv, BrushSettings settings)
        {
            if (_usingGpu)
            {
                var rt = (_currentTarget as IGpuBrushTarget)?.GetPaintRenderTexture();
                if (rt != null)
                {
                    GpuBrushEngine.Paint(rt, uv, settings);
                }
                else
                {
                    // GPU RT unavailable — fall back to CPU for this and subsequent stamps
                    _usingGpu = false;
                    var tex = _currentTarget.GetPaintTexture();
                    if (tex == null) return;
                    BrushEngine.Paint(tex, uv, settings);
                }
            }
            else
            {
                var tex = _currentTarget.GetPaintTexture();
                if (tex == null) return;
                BrushEngine.Paint(tex, uv, settings);
            }

            _lastUv = uv;
            _hasLastUv = true;
            _lastStampTime = EditorApplication.timeSinceStartup;
            _currentTarget.OnPaintApplied();
        }

        /// <summary>
        /// Apply a line of paint between two UVs. Handles GPU/CPU dispatch.
        /// </summary>
        private void ApplyPaintLine(Vector2 fromUv, Vector2 toUv, BrushSettings settings)
        {
            if (_usingGpu)
            {
                var rt = (_currentTarget as IGpuBrushTarget)?.GetPaintRenderTexture();
                if (rt == null) return;
                GpuBrushEngine.PaintLine(rt, fromUv, toUv, settings);
            }
            else
            {
                var tex = _currentTarget.GetPaintTexture();
                if (tex == null) return;
                BrushEngine.PaintLine(tex, fromUv, toUv, settings);
            }

            _lastUv = toUv;
            _hasLastUv = true;
            _lastStampTime = EditorApplication.timeSinceStartup;
            _currentTarget.OnPaintApplied();
        }

        #endregion

        #region Stroke Lifecycle

        private void ResetState()
        {
            _hasLastUv = false;
            _isPainting = false;
            _mouseIsDown = false;
            _strokeEndPending = false;
            _strokeSessionActive = false;
            _currentTarget = null;
            _usingGpu = false;
        }

        private void EndStroke()
        {
            if (_currentTarget != null)
            {
                // Sync GPU → CPU before stroke end (needed for saving)
                if (_usingGpu && _currentTarget is IGpuBrushTarget gpuTarget)
                    gpuTarget.SyncGpuToCpu();

                _currentTarget.OnStrokeEnd();
            }

            _isPainting = false;
            _hasLastUv = false;
            _currentTarget = null;
            _usingGpu = false;
            _strokeEndPending = false;
            _strokeSessionActive = false;
            _mouseIsDown = false;
        }

        /// <summary>
        /// Create a copy of brushSettings with scaled opacity for accumulation stamps.
        /// Re-created each time to stay in sync with all brushSettings fields.
        /// </summary>
        private void EnsureAccumulationSettings()
        {
            _accumulationSettings = new BrushSettings
            {
                radius = brushSettings.radius,
                hardness = brushSettings.hardness,
                opacity = brushSettings.opacity * AccumulationOpacityScale,
                mode = brushSettings.mode,
                color = brushSettings.color
            };
        }

        #endregion

        #region Brush Target Management

        /// <summary>
        /// Find an IBrushTarget on the hit GameObject, or auto-create a PaintableComponent if enabled.
        /// </summary>
        //private IBrushTarget GetOrCreateBrushTarget(GameObject go)
        //{
        //    var target = go.GetComponent<IBrushTarget>();
        //    if (target != null) return target;

        //    if (!autoCreateTarget) return null;
        //    if (go.GetComponent<MeshRenderer>() == null || go.GetComponent<MeshFilter>() == null) return null;

        //    Undo.AddComponent<PaintableComponent>(go);
        //    target = go.GetComponent<IBrushTarget>();

        //    var paintable = go.GetComponent<PaintableComponent>();
        //    if (paintable != null)
        //        paintable.EnsureMeshCollider();

        //    return target;
        //}

        #endregion

        #region Brush Preview

        private void DrawBrushPreview(RaycastHit hit)
        {
            if (_isPainting) return;

            Vector3 normal = hit.normal;
            float worldRadius = EstimateWorldRadius(hit, brushSettings.radius);

            // Outer ring (full radius)
            Handles.color = Color.cyan;
            Handles.DrawWireDisc(hit.point, normal, worldRadius);

            // Inner ring (hardness boundary)
            if (brushSettings.hardness < 1f)
            {
                float innerRadius = worldRadius * brushSettings.hardness;
                Handles.color = new Color(0f, 1f, 1f, 0.4f);
                Handles.DrawWireDisc(hit.point, normal, innerRadius);
            }

            // Normal indicator
            Handles.color = Color.yellow;
            Handles.DrawLine(hit.point, hit.point + normal * worldRadius * 0.3f);
        }

        /// <summary>
        /// Estimate the brush radius in world space by analyzing the mesh's UV-to-world ratio.
        /// </summary>
        private float EstimateWorldRadius(RaycastHit hit, float uvRadius)
        {
            var meshFilter = hit.collider.GetComponent<MeshFilter>();
            if (meshFilter == null || meshFilter.sharedMesh == null)
                return uvRadius;

            Mesh mesh = meshFilter.sharedMesh;
            Transform transform = hit.collider.transform;

            Vector2[] uvs = mesh.uv;
            if (uvs == null || uvs.Length == 0)
                return uvRadius;

            Bounds meshBounds = mesh.bounds;
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

            float worldSpan = meshBounds.size.magnitude;
            float scale = transform.lossyScale.magnitude;
            return (uvRadius / uvSpan) * worldSpan * scale;
        }

        #endregion

        #region Settings Panel

        private static GUIStyle _panelStyle;
        private static GUIStyle _panelTitleStyle;

        private void DrawSettingsPanel()
        {
            Handles.BeginGUI();

            var sv = SceneView.currentDrawingSceneView;
            float viewWidth = sv != null ? sv.position.width : 800f;
            float viewHeight = sv != null ? sv.position.height : 600f;

            float panelWidth = 230f;
            float panelHeight = brushSettings.mode == BrushSettings.PaintMode.Add ? 250f : 230f;
            float margin = 10f;
            float x = viewWidth - panelWidth - margin;
            float y = viewHeight - panelHeight - margin;
            Rect panelRect = new Rect(x, y, panelWidth, panelHeight);

            if (_panelStyle == null)
            {
                _panelStyle = new GUIStyle(EditorStyles.helpBox);
                Color bg = EditorGUIUtility.isProSkin
                    ? new Color(0.22f, 0.22f, 0.22f, 1f)
                    : new Color(0.76f, 0.76f, 0.76f, 1f);
                _panelStyle.normal.background = MakeTex(bg);
                _panelStyle.onNormal.background = _panelStyle.normal.background;
            }

            if (_panelTitleStyle == null)
            {
                _panelTitleStyle = new GUIStyle(EditorStyles.boldLabel);
                _panelTitleStyle.alignment = TextAnchor.MiddleCenter;
            }

            GUILayout.BeginArea(panelRect, _panelStyle);

            GUILayout.Label("Brush Settings", _panelTitleStyle);
            EditorGUILayout.Space(2);

            engine = (PaintEngine)EditorGUILayout.EnumPopup("Engine", engine);
            brushSettings.mode = (BrushSettings.PaintMode)EditorGUILayout.EnumPopup("Mode", brushSettings.mode);
            brushSettings.radius = EditorGUILayout.Slider("Radius", brushSettings.radius, 0.005f, 0.5f);
            brushSettings.hardness = EditorGUILayout.Slider("Hardness", brushSettings.hardness, 0f, 1f);
            brushSettings.opacity = EditorGUILayout.Slider("Opacity", brushSettings.opacity, 0.01f, 1f);

            if (brushSettings.mode == BrushSettings.PaintMode.Add)
                brushSettings.color = EditorGUILayout.ColorField(new GUIContent("Color"), brushSettings.color, true, true, false);

            EditorGUILayout.Space(4);
            autoCreateTarget = EditorGUILayout.Toggle("Auto Create Target", autoCreateTarget);

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("LMB: Paint | Alt: Orbit", EditorStyles.miniLabel);
            EditorGUILayout.LabelField("Hold: Accumulate", EditorStyles.miniLabel);

            GUILayout.EndArea();

            Handles.EndGUI();
        }

        private static Texture2D MakeTex(Color color)
        {
            var tex = new Texture2D(1, 1);
            tex.SetPixel(0, 0, color);
            tex.Apply();
            return tex;
        }

        #endregion
    }
}
#endif
