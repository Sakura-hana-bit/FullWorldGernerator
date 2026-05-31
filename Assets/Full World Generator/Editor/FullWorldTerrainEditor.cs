using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Rendering;
using FullWorld;

namespace FullWorldEditor
{
    /// <summary>
    /// Custom editor for FullWorldTerrain. Provides terrain generation controls,
    /// biome-colored preview mesh, and blueprint editing mode with tool support.
    /// </summary>
    [CustomEditor(typeof(FullWorldTerrain), true)]
    public class FullWorldTerrainEditor : Editor
    {
        #region Tools & Render Modes

        public List<BaseFullWorldEditorTool> tools = new List<BaseFullWorldEditorTool>();
        public int currentToolIndex = 0;
        public List<TerrianMeshRender> renderModes = new List<TerrianMeshRender>();
        public int renderModeFlags = 0;

        public BaseFullWorldEditorTool GetTool(int index) =>
            (tools.Count > index && index >= 0) ? tools[index] : null;

        public BaseFullWorldEditorTool currentTool => GetTool(currentToolIndex);

        #endregion

        #region State

        FullWorldTerrainEditorStage stage;
        FullWorldTerrain m_target;
        public bool editMode = false;
        public bool isEditing = false;
        public bool[] visible = new bool[0];
        internal Bounds bounds => m_target.Bounds;

#if UNITY_2019_1_OR_NEWER
        Action<ScriptableRenderContext, Camera> renderCallback;
#endif

        #endregion

        #region Preview Mesh

        internal Mesh m_PreviewMesh;
        private GameObject m_HeightMeshObject;
        private const string kHeightMeshObjectName = "TerrainHeightMesh";
        private float m_HeightScaleMin = 0f;
        private float m_HeightScaleMax = 50f;
        private float m_MeshSizeX = 512f;
        private float m_MeshSizeZ = 512f;

        // Height Range — world-space height bounds (meters)
        private float m_RangeMin = 0f;
        private float m_RangeMax = 50f;

        // Cached heightmap normalized range [0,1]
        private float m_HeightmapMin = 0f;
        private float m_HeightmapMax = 0f;

        // Reserved for derived editors
        protected Mesh visualizationMesh;
        protected Mesh visualizationWireMesh;

        #endregion

        #region Vegetation Rendering

        private Mesh m_TreeMesh;
        private Mesh m_BushMesh;
        private Material m_VegetationMaterial;
        private List<Matrix4x4> m_TreeMatrices = new List<Matrix4x4>();
        private List<Matrix4x4> m_BushMatrices = new List<Matrix4x4>();
        private static readonly Color k_TreeColor = new Color(0.15f, 0.45f, 0.08f, 1f);
        private static readonly Color k_BushColor = new Color(0.35f, 0.65f, 0.15f, 1f);

        #endregion

        #region Texture & Material

        private enum TextureResolutionEnum
        {
            _128 = 128, _256 = 256, _512 = 512, _1024 = 1024,
            _2048 = 2048, _4K = 4096, _8K = 8192
        }

        private TextureResolutionEnum m_TextureResolutionSelection = TextureResolutionEnum._512;
        internal Texture2D GeneratedHeightmap;
        RenderTexture m_OutputTexture;

        /// <summary>
        /// Output texture synced from the terrain's HeightSlopeMap after generation,
        /// or rebuilt from GeneratedHeightmap as fallback.
        /// </summary>
        internal RenderTexture outputTexture
        {
            get
            {
                if (m_OutputTexture == null)
                {
                    if (m_target != null && m_target.HeightSlopeMap != null)
                        SyncOutputTextureFrom(m_target.HeightSlopeMap);
                    else if (GeneratedHeightmap != null)
                    {
                        int size = GeneratedHeightmap.width;
                        m_OutputTexture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBFloat)
                        {
                            enableRandomWrite = true,
                            wrapMode = TextureWrapMode.Clamp,
                        };
                        m_OutputTexture.Create();

                        var tmp = RenderTexture.active;
                        RenderTexture.active = m_OutputTexture;
                        Graphics.Blit(GeneratedHeightmap, m_OutputTexture);
                        RenderTexture.active = tmp;
                    }
                }
                return m_OutputTexture;
            }
            set => m_OutputTexture = value;
        }

        private Shader gradientShader;
        private Material gradientMaterial;

        #endregion

        // ================================================================
        //  Inspector GUI
        // ================================================================

        public override void OnInspectorGUI()
        {
            serializedObject.UpdateIfRequiredOrScript();

            EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);

            EditorGUI.BeginChangeCheck();
            DrawProperties();
            bool propertiesChanged = EditorGUI.EndChangeCheck();
            bool isValid = Validate();

            GUILayout.Space(10);

            GUI.enabled = isValid;
            DrawGenerationControls();

            GUI.enabled = (m_target != null && !Application.isPlaying);
            EditorGUI.BeginChangeCheck();
            editMode = GUILayout.Toggle(editMode, editMode ? "Done" : "Edit", "Button");
            if (EditorGUI.EndChangeCheck())
            {
                if (editMode)
                    EditorApplication.delayCall += EnterBlueprintEditMode;
                else
                    EditorApplication.delayCall += ExitBlueprintEditMode;
            }
            EditorGUILayout.EndVertical();
            GUI.enabled = true;

            if (isEditing)
                DrawTools();

            if (GUI.changed)
            {
                serializedObject.ApplyModifiedPropertiesWithoutUndo();
                EditorUtility.SetDirty(target);
                Repaint();
            }

            ProcessDirty();
        }

        protected virtual void DrawProperties() { }

        private void DrawGenerationControls()
        {
            EditorGUILayout.LabelField("Base Info", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            // Texture resolution
            EditorGUI.BeginChangeCheck();
            m_TextureResolutionSelection = (TextureResolutionEnum)EditorGUILayout.EnumPopup(
                "Texture Resolution", m_TextureResolutionSelection);
            if (EditorGUI.EndChangeCheck())
            {
                int newRes = (int)m_TextureResolutionSelection;
                SerializedProperty texResProp = serializedObject.FindProperty("m_TextureResolution");
                if (texResProp != null)
                {
                    texResProp.intValue = newRes;
                    serializedObject.ApplyModifiedProperties();
                }
                m_target.MarkDirty();
            }

            // Seed
            SerializedProperty seedProp = serializedObject.FindProperty("m_Seed");
            if (seedProp != null)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(seedProp, new GUIContent("Seed"));
                if (EditorGUI.EndChangeCheck())
                {
                    serializedObject.ApplyModifiedProperties();
                    m_target.MarkDirty();
                }
            }

            // Mesh dimensions
            EditorGUI.BeginChangeCheck();
            m_MeshSizeX = EditorGUILayout.FloatField("Mesh Width", m_MeshSizeX);
            m_MeshSizeZ = EditorGUILayout.FloatField("Mesh Length", m_MeshSizeZ);
            if (EditorGUI.EndChangeCheck())
            {
                m_target.MeshSizeX = m_MeshSizeX;
                m_target.MeshSizeZ = m_MeshSizeZ;
                m_target.MarkDirty();
            }

            EditorGUI.indentLevel--;

            // ── Height Scale (world-space, ScaleMax = terrain height) ──
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Height Scale", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            //m_HeightScaleMin = EditorGUILayout.FloatField("Scale Min", m_HeightScaleMin);
            //m_HeightScaleMax = EditorGUILayout.FloatField("Scale Max", m_HeightScaleMax);
            //m_HeightScaleMin = Mathf.Max(m_HeightScaleMin, m_HeightmapMin);
            //m_HeightScaleMax = Mathf.Min(m_HeightScaleMax, m_HeightmapMax);
            if (EditorGUI.EndChangeCheck())
            {
                m_target.HeightScaleMin = m_HeightScaleMin;
                m_target.HeightScaleMax = m_HeightScaleMax;
                m_target.MarkDirty();
            }

            // [0,1] slider: 1.0 = ScaleMax
            {
                float scaleSpan = Mathf.Max(m_HeightScaleMax - m_HeightScaleMin, 1e-6f);
                float sliderMin = (m_HeightScaleMin > 0f) ? 0f : 0f;
                // Slider shows ratio within ScaleMin..ScaleMax
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PrefixLabel("Scale Ratio");
                EditorGUILayout.MinMaxSlider(ref m_HeightScaleMin, ref m_HeightScaleMax, m_RangeMin, m_RangeMax);
                EditorGUILayout.EndHorizontal();
                if (EditorGUI.EndChangeCheck())
                {
                    m_HeightScaleMin = Mathf.Max(m_HeightScaleMin, 0f);
                    m_HeightScaleMax = Mathf.Max(m_HeightScaleMax, m_HeightScaleMin);
                    m_target.HeightScaleMin = m_HeightScaleMin;
                    m_target.HeightScaleMax = m_HeightScaleMax;
                    m_target.MarkDirty();
                }
            }
            EditorGUI.indentLevel--;

            // ── Height Range (world-space decay bounds) ──
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Height Range (decay bounds)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            m_RangeMin = EditorGUILayout.FloatField("Range Min", m_RangeMin);
            m_RangeMax = EditorGUILayout.FloatField("Range Max", m_RangeMax);
            m_RangeMin = Mathf.Max(m_RangeMin, 0f);
            m_RangeMax = Mathf.Max(m_RangeMax, m_RangeMin);
            if (EditorGUI.EndChangeCheck())
            {
                m_target.RangeMin = m_RangeMin;
                m_target.RangeMax = m_RangeMax;
                m_target.MarkDirty();
            }
            EditorGUI.indentLevel--;

            // ── Generate Button ──
            EditorGUILayout.Space(6);
            GUI.backgroundColor = Color.green;
            if (GUILayout.Button("Generate Terrain", GUILayout.Height(32)))
            {
                if (m_target != null)
                    m_target.GenerateTerrain();
            }
            GUI.backgroundColor = Color.white;
        }

        private void DrawTools()
        {
            GUIContent[] contents = new GUIContent[tools.Count];
            for (int i = 0; i < tools.Count; ++i)
                contents[i] = new GUIContent(tools[i].icon, tools[i].name);

            EditorGUILayout.Space();
            GUILayout.Box(GUIContent.none, FullWorldEditorUtils.GetSeparatorLineStyle());
            EditorGUILayout.Space();

            EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);
            EditorGUI.BeginChangeCheck();
            int newSelectedTool = FullWorldEditorUtils.DoToolBar(currentToolIndex, contents);
            EditorGUILayout.EndVertical();

            if (EditorGUI.EndChangeCheck())
            {

                if (currentTool != null) currentTool.OnDisable();
                currentToolIndex = newSelectedTool;
                if (currentTool != null) currentTool.OnEnable();
                SceneView.RepaintAll();
            }

            if (currentTool != null)
            {
                EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);
                EditorGUILayout.LabelField(currentTool.name, EditorStyles.boldLabel);

                string help = currentTool.GetHelpString();
                if (!string.IsNullOrEmpty(help))
                    EditorGUILayout.LabelField(help, EditorStyles.helpBox);
                EditorGUILayout.EndVertical();

                currentTool.OnInspectorGUI();
            }
        }


        private bool Validate() => true;

        // ================================================================
        //  Lifecycle
        // ================================================================

        internal virtual void OnEnable()
        {
            m_target = target as FullWorldTerrain;

            tools.Clear();
            renderModes.Clear();
            tools.Add(new TerrianBaseGerneratorTools(this));
            tools.Add(new TerrianEffectLayerTools(this));
            tools.Add(new BiomeControlTool(this));
            renderModes.Add(new TerrianMeshRender(this));

            gradientShader = Shader.Find("FullWorldShader/M_TerrianDebug");
            if (gradientShader != null)
                gradientMaterial = new Material(gradientShader) { hideFlags = HideFlags.HideAndDontSave };

#if UNITY_2019_1_OR_NEWER
            renderCallback = (ctx, cam) => DrawWithCamera(cam);
            RenderPipelineManager.beginCameraRendering += renderCallback;
#endif
            Camera.onPreCull += DrawWithCamera;
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            m_target.OnTerrainGenerateEnd += OnTerrainGenerateEndHandler;
            m_target.OnVegetationGenerateEnd += OnVegetationGenerateEndHandler;
            MaskEditSession.Instance.OnStrokePainted += OnMaskStrokePainted;

            m_TextureResolutionSelection = (TextureResolutionEnum)m_target.TextureResolution;
            m_HeightScaleMin = m_target.HeightScaleMin;
            m_HeightScaleMax = m_target.HeightScaleMax;
            m_MeshSizeX = m_target.MeshSizeX;
            m_MeshSizeZ = m_target.MeshSizeZ;
            m_RangeMin = m_target.RangeMin;
            m_RangeMax = m_target.RangeMax;
        }

        public virtual void OnDisable()
        {
            ExitBlueprintEditMode();

#if UNITY_2019_1_OR_NEWER
            RenderPipelineManager.beginCameraRendering -= renderCallback;
#endif
            Camera.onPreCull -= DrawWithCamera;
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            m_target.OnTerrainGenerateEnd -= OnTerrainGenerateEndHandler;
            m_target.OnVegetationGenerateEnd -= OnVegetationGenerateEndHandler;
            MaskEditSession.Instance.OnStrokePainted -= OnMaskStrokePainted;

            foreach (var tool in tools) { tool.OnDisable(); tool.OnDestroy(); }
            foreach (var mode in renderModes) { mode.OnDestroy(); }

            CleanupEditor();
        }

        // ================================================================
        //  Events
        // ================================================================

        private void OnTerrainGenerateEndHandler(FullWorldTerrain terrain)
        {
            foreach (var tool in tools)
                if (tool is TerrianEffectLayerTools layerTool)
                    layerTool.ClearPreviewCache();

            GenerateHeightMesh(terrain);

            if (terrain.HeightSlopeMap != null)
                SyncOutputTextureFrom(terrain.HeightSlopeMap);

            // Rebuild vegetation render data if instances exist, rather than discarding
            if (terrain.VegetationInstances != null && terrain.VegetationInstances.Count > 0)
                RebuildVegetationInstances(terrain);
            else
                CleanupVegetation();
        }

        private void OnVegetationGenerateEndHandler(FullWorldTerrain terrain)
        {
            RebuildVegetationInstances(terrain);
        }

        private void OnMaskStrokePainted()
        {
            if (m_target == null) return;
            m_target.MarkDirty();
            Repaint();
        }

        protected virtual void OnPlayModeStateChanged(PlayModeStateChange change) { }

        protected virtual void OnSceneGUI(SceneView sceneView)
        {
            if (!isEditing || sceneView.camera == null) return;

            Event e = Event.current;
            if (e.type == EventType.Repaint)
            {
                for (int i = 0; i < renderModes.Count; ++i)
                    if ((1 << i & 1) != 0)
                        renderModes[i].OnSceneRepaint(sceneView);

                DrawVegetationInstances(sceneView.camera);
                DrawMaxHeightAABB();
            }

            if (currentTool != null)
                currentTool.OnSceneGUI(sceneView);
        }

        /// <summary>
        /// Draws wireframe AABBs for the decay envelope and terrain height.
        ///
        /// Green box  = ScaleMin (below this, quadratic decay pulls up)
        /// Blue box   = ScaleMax (above this, quadratic decay pulls down)
        /// Red box    = RangeMax (hard ceiling, never exceeded)
        /// Orange box = TerrainHeight (actual terrain Y extent)
        /// </summary>
        private void DrawMaxHeightAABB()
        {
            if (m_target == null) return;

            Bounds srcBounds = m_target.Bounds;
            bool boundsValid = srcBounds.size.sqrMagnitude > 1e-6f;
            float extentX = boundsValid ? srcBounds.size.x : m_MeshSizeX;
            float extentZ = boundsValid ? srcBounds.size.z : m_MeshSizeZ;
            float baseY = boundsValid ? srcBounds.min.y : 0f;
            float halfX = extentX * 0.5f;
            float halfZ = extentZ * 0.5f;

            // Scale and Range are both world-space (meters)
            float scaleMinY = baseY + m_HeightScaleMin;
            float scaleMaxY = baseY + m_HeightScaleMax;
            float rangeMinY = baseY + m_RangeMin;
            float rangeMaxY = baseY + m_RangeMax;
            float terrainTopY = baseY + m_HeightScaleMax;

            // ── Shared corner vectors ──
            Vector3 c00 = new Vector3(-halfX, 0f, -halfZ);
            Vector3 c10 = new Vector3( halfX, 0f, -halfZ);
            Vector3 c11 = new Vector3( halfX, 0f,  halfZ);
            Vector3 c01 = new Vector3(-halfX, 0f,  halfZ);

            // ── ScaleMin face (green) ──
            Vector3 sm00 = new Vector3(c00.x, scaleMinY, c00.z);
            Vector3 sm10 = new Vector3(c10.x, scaleMinY, c10.z);
            Vector3 sm11 = new Vector3(c11.x, scaleMinY, c11.z);
            Vector3 sm01 = new Vector3(c01.x, scaleMinY, c01.z);

            // ── ScaleMax face (blue) ──
            Vector3 sx00 = new Vector3(c00.x, scaleMaxY, c00.z);
            Vector3 sx10 = new Vector3(c10.x, scaleMaxY, c10.z);
            Vector3 sx11 = new Vector3(c11.x, scaleMaxY, c11.z);
            Vector3 sx01 = new Vector3(c01.x, scaleMaxY, c01.z);

            // ── RangeMin face (yellow) ──
            Vector3 lo00 = new Vector3(c00.x, rangeMinY, c00.z);
            Vector3 lo10 = new Vector3(c10.x, rangeMinY, c10.z);
            Vector3 lo11 = new Vector3(c11.x, rangeMinY, c11.z);
            Vector3 lo01 = new Vector3(c01.x, rangeMinY, c01.z);

            // ── RangeMax face (red-orange) ──
            Vector3 hi00 = new Vector3(c00.x, rangeMaxY, c00.z);
            Vector3 hi10 = new Vector3(c10.x, rangeMaxY, c10.z);
            Vector3 hi11 = new Vector3(c11.x, rangeMaxY, c11.z);
            Vector3 hi01 = new Vector3(c01.x, rangeMaxY, c01.z);

            // ── Terrain top face (orange dashed) ──
            Vector3 max00 = new Vector3(c00.x, terrainTopY, c00.z);
            Vector3 max10 = new Vector3(c10.x, terrainTopY, c10.z);
            Vector3 max11 = new Vector3(c11.x, terrainTopY, c11.z);
            Vector3 max01 = new Vector3(c01.x, terrainTopY, c01.z);

            Color prevColor = Handles.color;

            // ScaleMin face (green)
            Handles.color = new Color(0.3f, 1f, 0.3f, 0.7f);
            Handles.DrawLine(sm00, sm10);
            Handles.DrawLine(sm10, sm11);
            Handles.DrawLine(sm11, sm01);
            Handles.DrawLine(sm01, sm00);

            // ScaleMax face (blue)
            Handles.color = new Color(0.3f, 0.6f, 1f, 0.7f);
            Handles.DrawLine(sx00, sx10);
            Handles.DrawLine(sx10, sx11);
            Handles.DrawLine(sx11, sx01);
            Handles.DrawLine(sx01, sx00);

            // RangeMin face (yellow)
            Handles.color = new Color(1f, 0.9f, 0.2f, 0.6f);
            Handles.DrawLine(lo00, lo10);
            Handles.DrawLine(lo10, lo11);
            Handles.DrawLine(lo11, lo01);
            Handles.DrawLine(lo01, lo00);

            // RangeMax face (red-orange)
            Handles.color = new Color(1f, 0.4f, 0.2f, 0.7f);
            Handles.DrawLine(hi00, hi10);
            Handles.DrawLine(hi10, hi11);
            Handles.DrawLine(hi11, hi01);
            Handles.DrawLine(hi01, hi00);

            // Terrain top face (orange dashed)
            Handles.color = new Color(1f, 0.7f, 0f, 0.5f);
            Handles.DrawDottedLine(max00, max10, 6f);
            Handles.DrawDottedLine(max10, max11, 6f);
            Handles.DrawDottedLine(max11, max01, 6f);
            Handles.DrawDottedLine(max01, max00, 6f);

            // Vertical dashed pillars (RangeMin → RangeMax)
            Handles.color = new Color(1f, 0.7f, 0f, 0.4f);
            Handles.DrawDottedLine(lo00, hi00, 4f);
            Handles.DrawDottedLine(lo10, hi10, 4f);
            Handles.DrawDottedLine(lo11, hi11, 4f);
            Handles.DrawDottedLine(lo11, hi11, 4f);
            Handles.DrawDottedLine(lo01, hi01, 4f);

            // ── Labels ──
            GUIStyle labelStyle = new GUIStyle(EditorStyles.miniLabel);
            labelStyle.alignment = TextAnchor.MiddleLeft;

            // RangeMin label (yellow)
            labelStyle.normal.textColor = new Color(1f, 0.9f, 0.2f);
            Handles.Label(
                new Vector3(halfX + 2f, rangeMinY, -halfZ),
                $"rangeMin={m_RangeMin:F1}m",
                labelStyle);

            // ScaleMin label (green)
            labelStyle.normal.textColor = new Color(0.3f, 1f, 0.3f);
            Handles.Label(
                new Vector3(halfX + 2f, scaleMinY, -halfZ),
                $"scaleMin={m_HeightScaleMin:F1}m",
                labelStyle);

            // ScaleMax label (blue)
            labelStyle.normal.textColor = new Color(0.3f, 0.6f, 1f);
            Handles.Label(
                new Vector3(halfX + 2f, scaleMaxY, -halfZ),
                $"scaleMax={m_HeightScaleMax:F1}m",
                labelStyle);

            // RangeMax label (red-orange)
            labelStyle.normal.textColor = new Color(1f, 0.4f, 0.2f);
            Handles.Label(
                new Vector3(halfX + 2f, rangeMaxY, -halfZ),
                $"rangeMax={m_RangeMax:F1}m",
                labelStyle);

            // Terrain height label (orange)
            labelStyle.normal.textColor = new Color(1f, 0.7f, 0f);
            Handles.Label(
                new Vector3(halfX + 2f, terrainTopY, -halfZ),
                $"H={m_HeightScaleMax:F1}m",
                labelStyle);

            Handles.color = prevColor;
        }

        protected virtual void DrawWithCamera(Camera cam)
        {
            if (!editMode) return;
            for (int i = 0; i < renderModes.Count; ++i)
                if ((1 << i & renderModeFlags) != 0)
                    renderModes[i].DrawWithCamera(cam);
        }

        // ================================================================
        //  Preview Mesh Generation
        // ================================================================

        /// <summary>
        /// Reads back the BiomeMap (GPU-generated) and builds a preview mesh
        /// with vertex positions from HeightSlopeMap and colors from BiomeMap.
        /// </summary>
        private void GenerateHeightMesh(FullWorldTerrain terrain)
        {
            var heightSlopeMap = terrain.HeightSlopeMap;
            var biomeMap = terrain.BiomeMap;
            if (heightSlopeMap == null) return;

            int resolution = terrain.TextureResolution;

            // Read back HeightSlopeMap for vertex positions
            var prevRT = RenderTexture.active;

            RenderTexture.active = heightSlopeMap;
            var heightTex = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false, true);
            heightTex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            heightTex.Apply();

            // Read back BiomeMap for vertex colors
            Color[] colors;
            if (biomeMap != null)
            {
                RenderTexture.active = biomeMap;
                var biomeTex = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false, true);
                biomeTex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
                biomeTex.Apply();
                colors = biomeTex.GetPixels();
                DestroyImmediate(biomeTex);
            }
            else
            {
                // Fallback: white if biome map not yet generated
                colors = new Color[resolution * resolution];
                for (int i = 0; i < colors.Length; i++)
                    colors[i] = Color.white;
            }

            RenderTexture.active = prevRT;

            var pixels = heightTex.GetPixels();

            // Scan height range for normalization
            float minH = float.MaxValue, maxH = float.MinValue;
            for (int i = 0; i < pixels.Length; i++)
            {
                float h = pixels[i].r;
                if (h < minH) minH = h;
                if (h > maxH) maxH = h;
            }
            float heightRange = Mathf.Max(maxH - minH, 1e-6f);

            m_HeightmapMin = minH;
            m_HeightmapMax = maxH;

            // Build grid mesh
            int vertexCount = resolution * resolution;
            var vertices = new Vector3[vertexCount];
            var uvs = new Vector2[vertexCount];
            var triangles = new int[(resolution - 1) * (resolution - 1) * 6];

            Bounds srcBounds = terrain.Bounds;
            bool boundsValid = srcBounds.size.sqrMagnitude > 1e-6f;
            float extentX = boundsValid ? srcBounds.size.x : m_MeshSizeX;
            float extentZ = boundsValid ? srcBounds.size.z : m_MeshSizeZ;
            float baseY = boundsValid ? srcBounds.min.y : 0f;
            float cellX = extentX / (resolution - 1);
            float cellZ = extentZ / (resolution - 1);
            float offX = -extentX * 0.5f;
            float offZ = -extentZ * 0.5f;

            for (int z = 0; z < resolution; z++)
            {
                for (int x = 0; x < resolution; x++)
                {
                    int idx = z * resolution + x;
                    float height = pixels[idx].r;

                    vertices[idx] = new Vector3(
                        offX + x * cellX,
                        baseY + height * m_HeightScaleMax,
                        offZ + z * cellZ);
                    uvs[idx] = new Vector2(
                        (float)x / (resolution - 1),
                        (float)z / (resolution - 1));
                }
            }

            // Triangulate grid
            int triIdx = 0;
            for (int z = 0; z < resolution - 1; z++)
            {
                for (int x = 0; x < resolution - 1; x++)
                {
                    int v0 = z * resolution + x;
                    int v1 = v0 + 1;
                    int v2 = (z + 1) * resolution + x;
                    int v3 = v2 + 1;

                    triangles[triIdx++] = v0;
                    triangles[triIdx++] = v2;
                    triangles[triIdx++] = v1;
                    triangles[triIdx++] = v1;
                    triangles[triIdx++] = v2;
                    triangles[triIdx++] = v3;
                }
            }

            if (m_PreviewMesh == null) m_PreviewMesh = new Mesh();
            else m_PreviewMesh.Clear();

            m_PreviewMesh.indexFormat = IndexFormat.UInt32;
            m_PreviewMesh.vertices = vertices;
            m_PreviewMesh.uv = uvs;
            m_PreviewMesh.colors = colors;
            m_PreviewMesh.triangles = triangles;
            m_PreviewMesh.RecalculateNormals();

            DestroyImmediate(heightTex);

            if (isEditing)
                EnsureHeightMeshObject();
        }

        private void EnsureHeightMeshObject()
        {
            if (m_PreviewMesh == null) return;

            if (m_HeightMeshObject == null && stage != null)
                m_HeightMeshObject = FindInStage(kHeightMeshObjectName);

            if (m_HeightMeshObject == null)
            {
                m_HeightMeshObject = new GameObject(kHeightMeshObjectName);
                m_HeightMeshObject.AddComponent<MeshFilter>();
                m_HeightMeshObject.AddComponent<MeshRenderer>();
                m_HeightMeshObject.AddComponent<MeshCollider>();
                AddOBJtoStage(m_HeightMeshObject);
            }

            m_HeightMeshObject.GetComponent<MeshFilter>().sharedMesh = m_PreviewMesh;

            // Keep collider mesh in sync so raycasting works for brush tools
            var collider = m_HeightMeshObject.GetComponent<MeshCollider>();
            if (collider != null)
                collider.sharedMesh = m_PreviewMesh;

            // Ensure material is always assigned — avoid HDRP pink fallback
            var renderer = m_HeightMeshObject.GetComponent<MeshRenderer>();
            if (gradientMaterial != null)
                renderer.sharedMaterial = gradientMaterial;
            else if (renderer.sharedMaterial == null)
            {
                // Retry shader find if it failed during OnEnable
                gradientShader = Shader.Find("FullWorldShader/M_TerrianDebug");
                if (gradientShader != null)
                {
                    gradientMaterial = new Material(gradientShader) { hideFlags = HideFlags.HideAndDontSave };
                    renderer.sharedMaterial = gradientMaterial;
                }
            }
        }

        private void DestroyHeightMeshObject()
        {
            if (m_HeightMeshObject != null)
            {
                DestroyImmediate(m_HeightMeshObject);
                m_HeightMeshObject = null;
            }
        }

        // ================================================================
        //  Edit Mode
        // ================================================================

        private void EnterBlueprintEditMode()
        {
            if (isEditing) return;

            ActiveEditorTracker.sharedTracker.isLocked = true;
            string assetPath = AssetDatabase.GetAssetPath(m_target);
            stage = FullWorldTerrainEditorStage.CreateStage(assetPath, this);
            StageUtility.GoToStage(stage, true);

            isEditing = true;
            m_target.EnableDebugerMode = true;
            m_target.MarkDirty();

            if (m_PreviewMesh != null)
                EnsureHeightMeshObject();
        }

        private void ExitBlueprintEditMode()

        {

            if (!isEditing) return;


            m_target.ReleaseLayerMaskRT();

            isEditing = false;
            DestroyHeightMeshObject();
            AssetDatabase.SaveAssets();
            StageUtility.GoToMainStage();
            GameObject.DestroyImmediate(stage);
            m_target.EnableDebugerMode = false;
        }

        // ================================================================
        //  Dirty Processing
        // ================================================================

        private void ProcessDirty()
        {
            if (m_target == null || !m_target.IsDirty) return;
            m_target.GenerateWorkflow();
        }

        // ================================================================
        //  Helpers
        // ================================================================

        internal void CleanupEditor()
        {
            DestroyHeightMeshObject();

            if (m_PreviewMesh != null)
            {
                DestroyImmediate(m_PreviewMesh);
                m_PreviewMesh = null;
            }

            if (m_OutputTexture != null)
            {
                m_OutputTexture.Release();
                m_OutputTexture = null;
            }

            if (gradientMaterial != null)
            {
                DestroyImmediate(gradientMaterial);
                gradientMaterial = null;
            }

            CleanupVegetation();
        }

        // ================================================================
        //  Vegetation Rendering
        // ================================================================

        private void EnsureVegetationMeshes()
        {
            if (m_TreeMesh == null)
                m_TreeMesh = VegetationMeshBuilder.BuildCone(1f, 0.35f, 8);

            if (m_BushMesh == null)
                m_BushMesh = VegetationMeshBuilder.BuildHemisphere(1f, 1f, 12, 6);

            if (m_VegetationMaterial == null)
            {
                // Create a minimal instancing-capable material for editor preview
                var shader = Shader.Find("HDRP/Lit");
                if (shader == null)
                    shader = Shader.Find("Standard");

                if (shader != null)
                {
                    m_VegetationMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                    m_VegetationMaterial.enableInstancing = true;
                }
            }
        }

        private void RebuildVegetationInstances(FullWorldTerrain terrain)
        {
            m_TreeMatrices.Clear();
            m_BushMatrices.Clear();

            var instances = terrain.VegetationInstances;
            if (instances == null) return;

            float globalScale = terrain.Vegetation.vegetationScale;

            foreach (var inst in instances)
            {
                var pos = inst.position;
                var rot = Quaternion.Euler(inst.tiltX, inst.rotation, inst.tiltZ);

                // height/radius are in real-world meters; apply vegetationScale
                float h = inst.height * globalScale;
                float r = inst.radius * globalScale;

                var scale = inst.type == VegetationType.Tree
                    ? new Vector3(r, h, r)       // cone: radius × height × radius
                    : new Vector3(r * 2f, h, r * 2f); // hemisphere: diameter × height × diameter

                var matrix = Matrix4x4.TRS(pos, rot, scale);

                if (inst.type == VegetationType.Tree)
                    m_TreeMatrices.Add(matrix);
                else
                    m_BushMatrices.Add(matrix);
            }
        }

        private void DrawVegetationInstances(Camera cam)
        {
            if (!editMode) return;
            if (m_TreeMatrices.Count == 0 && m_BushMatrices.Count == 0) return;

            EnsureVegetationMeshes();

            var mpb = new MaterialPropertyBlock();

            // Draw trees (cones)
            mpb.SetColor("_BaseColor", k_TreeColor);
            for (int i = 0; i < m_TreeMatrices.Count; i++)
            {
                Graphics.DrawMesh(m_TreeMesh, m_TreeMatrices[i], m_VegetationMaterial,
                    0, cam, 0, mpb, ShadowCastingMode.Off, false);
            }

            // Draw bushes (hemispheres)
            mpb.SetColor("_BaseColor", k_BushColor);
            for (int i = 0; i < m_BushMatrices.Count; i++)
            {
                Graphics.DrawMesh(m_BushMesh, m_BushMatrices[i], m_VegetationMaterial,
                    0, cam, 0, mpb, ShadowCastingMode.Off, false);
            }
        }

        private void CleanupVegetation()
        {
            m_TreeMatrices.Clear();
            m_BushMatrices.Clear();

            if (m_TreeMesh != null) { DestroyImmediate(m_TreeMesh); m_TreeMesh = null; }
            if (m_BushMesh != null) { DestroyImmediate(m_BushMesh); m_BushMesh = null; }
            if (m_VegetationMaterial != null) { DestroyImmediate(m_VegetationMaterial); m_VegetationMaterial = null; }
        }

        private void SyncOutputTextureFrom(RenderTexture source)
        {
            if (source == null) return;

            if (m_OutputTexture != null)
                m_OutputTexture.Release();

            m_OutputTexture = new RenderTexture(source.width, source.height, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
            };
            m_OutputTexture.Create();

            var tmp = RenderTexture.active;
            RenderTexture.active = m_OutputTexture;
            Graphics.Blit(source, m_OutputTexture);
            RenderTexture.active = tmp;
        }

        internal void AddOBJtoStage(GameObject obj)
        {
            obj.hideFlags = HideFlags.DontSave;
            SceneManager.MoveGameObjectToScene(obj, stage.scene);
        }

        internal GameObject FindInStage(string name)
        {
            foreach (var rootObj in stage.scene.GetRootGameObjects())
            {
                if (rootObj.name == name) return rootObj;
                var child = rootObj.transform.Find(name);
                if (child != null) return child.gameObject;
            }
            return null;
        }
    }
}
