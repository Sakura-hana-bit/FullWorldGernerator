using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using FullWorld;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace FullWorldEditor
{
    /// <summary>
    /// Terrain biome distribution + vegetation layer management tool.
    /// Provides biome threshold sliders with gradient preview,
    /// and a reorderable list of vegetation layers with per-layer
    /// mask thumbnails and property editing.
    /// </summary>
    public class BiomeControlTool : BaseFullWorldEditorTool
    {
        // ================================================================
        //  Constants & Static
        // ================================================================

        private const float k_ThumbnailSize = 64f;
        private const float k_BaseElementHeight = 46f;
        private static readonly Color k_BorderColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color k_EditingBorderColor = new Color(0.2f, 0.8f, 1f, 1f);
        private static readonly Type s_BaseParamType = typeof(BaseVegetationLayerParam);
        private static List<Type> s_CachedParamTypes;

        private static readonly Color k_WaterColor = new Color(0.12f, 0.38f, 0.72f);
        private static readonly Color k_SandColor   = new Color(0.82f, 0.74f, 0.48f);
        private static readonly Color k_VegetColor  = new Color(0.22f, 0.58f, 0.18f);
        private static readonly Color k_RockColor   = new Color(0.52f, 0.48f, 0.44f);
        private static readonly Color k_SnowColor   = new Color(0.92f, 0.94f, 0.96f);

        public static List<Type> GetAllVegetationLayerParamTypes()
        {
            if (s_CachedParamTypes != null)
                return s_CachedParamTypes;

            s_CachedParamTypes = AppDomain.CurrentDomain.GetAssemblies()
                .SelectMany(SafeGetTypes)
                .Where(t => t.IsSubclassOf(s_BaseParamType) && !t.IsAbstract)
                .ToList();

            return s_CachedParamTypes;
        }

        private static IEnumerable<Type> SafeGetTypes(Assembly asm)
        {
            try { return asm.GetTypes(); }
            catch (ReflectionTypeLoadException) { return Type.EmptyTypes; }
        }

        // ================================================================
        //  Serialized Properties
        // ================================================================

        protected ReorderableList layerList;
        private SerializedProperty m_LayersProperty;

        // ================================================================
        //  Selection & Editor State
        // ================================================================

        private int m_SelectedIndex = -1;
        private Editor m_ParamEditor;
        private UnityEngine.Object m_CachedParamRef;

        // ================================================================
        //  Mask Editing State
        // ================================================================

        private int m_EditingMaskIndex = -1;

        // ================================================================
        //  Biome Gradient Preview
        // ================================================================

        private Texture2D m_GradientBar;

        // ================================================================
        //  Preview Helpers
        // ================================================================

        private Texture2D m_WhiteMaskTex;

        // ================================================================
        //  Accessors & Constructor
        // ================================================================

        private FullWorldTerrain TerrainTarget => editor.target as FullWorldTerrain;

        public BiomeControlTool(FullWorldTerrainEditor editor) : base(editor)
        {
            m_Icon = Resources.Load<Texture2D>("ScatterIcon");
            m_Name = "BiomeControl";
        }

        // ================================================================
        //  Layer List Setup
        // ================================================================

        private void InitializeLayerList()
        {
            var so = editor.serializedObject;
            m_LayersProperty = so.FindProperty("m_VegetationLayers");

            layerList = new ReorderableList(so, m_LayersProperty, true, true, true, true);
            layerList.drawHeaderCallback = r => EditorGUI.LabelField(r, "Vegetation Layers");
            layerList.drawElementCallback = DrawLayerElement;
            layerList.elementHeightCallback = ComputeElementHeight;
            layerList.onSelectCallback = list => { m_SelectedIndex = list.index; RefreshParamEditor(); };
            layerList.onReorderCallback = OnLayerReordered;
            layerList.onAddDropdownCallback = (rect, _) => ShowAddLayerMenu(rect);
            layerList.onRemoveCallback = OnLayerRemoved;
        }

        // ================================================================
        //  Layer Element Drawing
        // ================================================================

        private void DrawLayerElement(Rect rect, int index, bool isActive, bool isFocused)
        {
            var elementProp = m_LayersProperty.GetArrayElementAtIndex(index);
            var enableProp = elementProp.FindPropertyRelative("enable");
            var paramProp = elementProp.FindPropertyRelative("param");
            bool enabled = enableProp.boolValue;

            rect.y += 2;

            if (index == m_SelectedIndex)
                EditorGUI.DrawRect(new Rect(rect.x - 2f, rect.y - 1, rect.width + 4f, ComputeElementHeight(index)),
                    new Color(0.3f, 0.5f, 0.85f, 0.25f));

            DrawLayerHeader(rect, index, paramProp, enableProp);

            rect.y += EditorGUIUtility.singleLineHeight + 2;
            DrawLayerTypeInfo(rect, paramProp);

            if (!enabled) return;

            rect.y += EditorGUIUtility.singleLineHeight + 4;

            float cursor = rect.x + 18f;
            float thumbSize = 48f;

            DrawMaskThumbnail(rect, index, cursor, thumbSize);

            // Mask controls to the right of the thumbnail
            float controlsX = cursor + thumbSize + 8f;
            float controlsWidth = rect.x + rect.width - controlsX;
            DrawMaskControlsBeside(rect, index, controlsX, controlsWidth, thumbSize);
        }

        private void DrawLayerHeader(Rect rect, int index, SerializedProperty paramProp, SerializedProperty enableProp)
        {
            float cursor = rect.x;

            var enableRect = new Rect(cursor, rect.y, 16f, EditorGUIUtility.singleLineHeight);
            EditorGUI.BeginChangeCheck();
            EditorGUI.PropertyField(enableRect, enableProp, GUIContent.none);
            if (EditorGUI.EndChangeCheck())
                RequestRegenerate();
            cursor += 18f;

            var indexContent = new GUIContent($"{index}");
            var indexSize = EditorStyles.label.CalcSize(indexContent);
            EditorGUI.LabelField(new Rect(cursor, rect.y, indexSize.x, EditorGUIUtility.singleLineHeight),
                indexContent, EditorStyles.miniLabel);
            cursor += indexSize.x + 2f;

            Texture icon = paramProp.objectReferenceValue != null
                ? AssetPreview.GetMiniThumbnail(paramProp.objectReferenceValue)
                : EditorGUIUtility.IconContent("ScriptableObject Icon").image as Texture2D;
            if (icon != null)
                GUI.DrawTexture(new Rect(cursor, rect.y, 16f, EditorGUIUtility.singleLineHeight), icon, ScaleMode.ScaleToFit);
            cursor += 18f;

            float fieldW = rect.x + rect.width - cursor;
            var prevObj = paramProp.objectReferenceValue;
            var newObj = EditorGUI.ObjectField(new Rect(cursor, rect.y, fieldW, EditorGUIUtility.singleLineHeight),
                prevObj, s_BaseParamType, false);
            if (newObj != prevObj)
            {
                if (newObj != null && !s_BaseParamType.IsInstanceOfType(newObj))
                    newObj = null;
                paramProp.objectReferenceValue = newObj;
                RequestRegenerate();
            }
        }

        private void DrawLayerTypeInfo(Rect rect, SerializedProperty paramProp)
        {
            float cursor = rect.x + 18f;
            var detailRect = new Rect(cursor, rect.y, rect.x + rect.width - cursor, EditorGUIUtility.singleLineHeight);

            if (paramProp.objectReferenceValue is BaseVegetationLayerParam param)
            {
                var vp = param.GetParameters();
                EditorGUI.LabelField(detailRect,
                    $"Type: {param.GetLayerType} | Density: {vp.density:F1} | Bush: {vp.bushRatio:P0}",
                    EditorStyles.miniLabel);
            }
            else
            {
                EditorGUI.LabelField(detailRect, "Drag in a LayerParam asset or use + to create one", EditorStyles.miniLabel);
            }
        }

        private float ComputeElementHeight(int index)
        {
            bool enabled = true;
            if (m_LayersProperty != null && index < m_LayersProperty.arraySize)
            {
                var enableProp = m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("enable");
                if (enableProp != null) enabled = enableProp.boolValue;
            }

            float h = k_BaseElementHeight;
            if (enabled)
                h += 48f + EditorGUIUtility.singleLineHeight + 7f; // thumbnail + label + padding

            return h;
        }

        // ================================================================
        //  Mask Drawing
        // ================================================================

        private void DrawMaskThumbnail(Rect rect, int index, float x, float width)
        {
            BaseMaskData maskData = null;
            if (m_LayersProperty != null && index < m_LayersProperty.arraySize)
            {
                var maskProp = m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
                maskData = maskProp?.objectReferenceValue as BaseMaskData;
            }

            float thumbSize = width;
            var thumbRect = new Rect(x, rect.y, thumbSize, thumbSize);

            Texture previewTex = maskData?.PreviewRT;
            if (previewTex == null) previewTex = GetOrCreateWhiteMaskTex();

            bool isEditing = maskData != null && MaskEditSession.Instance.Current == maskData;
            DrawPreviewBorder(thumbRect, isEditing ? k_EditingBorderColor : k_BorderColor, isEditing ? 2f : 1f);
            EditorGUI.DrawPreviewTexture(thumbRect, previewTex);

            EditorGUI.LabelField(new Rect(x, rect.y + thumbSize + 1f, thumbSize, EditorGUIUtility.singleLineHeight),
                isEditing ? "Mask*" : "Mask", isEditing ? EditorStyles.boldLabel : EditorStyles.miniLabel);
        }

        private void DrawMaskControlsBeside(Rect rect, int index, float x, float width, float thumbHeight)
        {
            float btnWidth = 42f;
            float btnGap = 3f;

            // Vertically center the controls block relative to the thumbnail
            float controlsHeight = EditorGUIUtility.singleLineHeight * 2f + 2f;
            float offsetY = (thumbHeight - controlsHeight) / 2f;

            // Object field row
            var objRect = new Rect(x, rect.y + offsetY, width, EditorGUIUtility.singleLineHeight);
            if (m_LayersProperty != null && index < m_LayersProperty.arraySize)
            {
                var maskProp = m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
                EditorGUI.BeginChangeCheck();
                EditorGUI.ObjectField(objRect, maskProp, typeof(BaseMaskData), GUIContent.none);
                if (EditorGUI.EndChangeCheck())
                {
                    GetMaskDataAt(index)?.MarkDirty();
                    editor.serializedObject.ApplyModifiedProperties();
                    EditorUtility.SetDirty(editor.target);
                    RequestRegenerate();
                }
            }

            // Buttons row
            float btnY = rect.y + offsetY + EditorGUIUtility.singleLineHeight + 2f;
            float btnX = x;

            if (GUI.Button(new Rect(btnX, btnY, btnWidth, EditorGUIUtility.singleLineHeight), "New", EditorStyles.miniButton))
                CreateMaskAsset(index);

            bool hasMask = GetMaskDataAt(index) != null;
            var maskData = GetMaskDataAt(index);
            bool isEditingThis = maskData != null && MaskEditSession.Instance.Current == maskData;

            // Clear
            EditorGUI.BeginDisabledGroup(!hasMask || isEditingThis);
            if (GUI.Button(new Rect(btnX + btnWidth + btnGap, btnY, btnWidth, EditorGUIUtility.singleLineHeight), "Clear", EditorStyles.miniButton))
                ClearMask(index);
            EditorGUI.EndDisabledGroup();

            // Edit / Done
            if (isEditingThis)
            {
                GUI.backgroundColor = Color.cyan;
                if (GUI.Button(new Rect(btnX + (btnWidth + btnGap) * 2, btnY, btnWidth, EditorGUIUtility.singleLineHeight), "Done", EditorStyles.miniButton))
                {
                    MaskEditSession.Instance.Deactivate();
                    m_EditingMaskIndex = -1;
                }
                GUI.backgroundColor = Color.white;
            }
            else
            {
                EditorGUI.BeginDisabledGroup(!hasMask);
                if (GUI.Button(new Rect(btnX + (btnWidth + btnGap) * 2, btnY, btnWidth, EditorGUIUtility.singleLineHeight), "Edit", EditorStyles.miniButton))
                {
                    m_EditingMaskIndex = index;
                    MaskEditSession.Instance.Activate(maskData);
                }
                EditorGUI.EndDisabledGroup();
            }
        }

        private static void DrawPreviewBorder(Rect innerRect, Color color, float thickness = 1f)
        {
            EditorGUI.DrawRect(new Rect(innerRect.x - thickness, innerRect.y - thickness,
                innerRect.width + thickness * 2, innerRect.height + thickness * 2), color);
        }

        // ================================================================
        //  Mask Asset Operations
        // ================================================================

        private void CreateMaskAsset(int index)
        {
            string savePath = EditorUtility.SaveFilePanelInProject(
                "Create Mask Asset", $"VegLayer{index}_Mask.asset", "asset",
                "Choose a location to save the mask asset.");
            if (string.IsNullOrEmpty(savePath)) return;

            var instance = ScriptableObject.CreateInstance<BaseMaskData>();
            int res = TerrainTarget?.TextureResolution ?? 512;
            instance.m_Mask = Enumerable.Repeat(1f, res * res).ToArray();

            AssetDatabase.CreateAsset(instance, savePath);
            AssetDatabase.SaveAssets();

            if (m_LayersProperty != null && index < m_LayersProperty.arraySize)
            {
                var maskProp = m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
                maskProp.objectReferenceValue = instance;
                editor.serializedObject.ApplyModifiedProperties();
                EditorUtility.SetDirty(editor.target);
                RequestRegenerate();
            }
        }

        private void ClearMask(int index)
        {
            if (m_LayersProperty == null || index >= m_LayersProperty.arraySize) return;
            var maskProp = m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
            if (maskProp == null) return;

            var maskData = maskProp.objectReferenceValue as BaseMaskData;
            if (maskData != null)
            {
                maskData.MarkDirty();
                string path = AssetDatabase.GetAssetPath(maskData);
                if (!string.IsNullOrEmpty(path) &&
                    EditorUtility.DisplayDialog("Delete Mask Asset",
                        $"Also delete the mask asset file?\n{path}", "Delete Asset", "Keep Asset"))
                    AssetDatabase.DeleteAsset(path);
            }

            maskProp.objectReferenceValue = null;
            editor.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(editor.target);
            RequestRegenerate();
        }

        // ================================================================
        //  Inspector GUI
        // ================================================================

        public override void OnInspectorGUI()
        {
            var terrain = TerrainTarget;
            if (terrain == null) return;

            var so = editor.serializedObject;
            so.Update();

            // ── Biome Distribution Section ──
            DrawBiomeSection(terrain);

            EditorGUILayout.Space(12);

            // ── Vegetation Layers Section ──
            if (layerList == null) InitializeLayerList();

            EditorGUILayout.LabelField("Vegetation Layers", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox(
                "Each layer defines a vegetation scatter pass with its own parameters and optional mask.\n" +
                "Drag items to reorder the execution order.", MessageType.Info);

            layerList.DoLayoutList();

            DrawSelectedParamDetail();

            EditorGUILayout.Space(4);

            // ── Generate Button & Instance Count ──
            if (GUILayout.Button("Generate Vegetation", GUILayout.Height(28)))
            {
                so.ApplyModifiedProperties();
                Undo.RecordObject(terrain, "Generate Vegetation");
                terrain.GenerateVegetation();
            }

            DrawInstanceCount(terrain);

            so.ApplyModifiedProperties();
        }

        // ================================================================
        //  Biome Distribution Section
        // ================================================================

        private void DrawBiomeSection(FullWorldTerrain terrain)
        {
            var biome = terrain.Biome;

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Biome Distribution", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox(
                "Height-based biome thresholds in normalized [0,1] space.\n" +
                "Layering: Water → Sand → Vegetation → Rock → Snow.\n" +
                "Steep slopes override to rock regardless of height zone.",
                MessageType.Info);

            DrawGradientPreview(ref biome);

            EditorGUILayout.Space(6);

            // ── Height Zones ──
            EditorGUILayout.LabelField("Height Zones", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();
            biome.waterLine     = EditorGUILayout.Slider("Water Line", biome.waterLine, 0f, 1f);
            biome.sandEnd       = EditorGUILayout.Slider("Sand End", biome.sandEnd, 0f, 1f);
            biome.vegetationEnd = EditorGUILayout.Slider("Vegetation End", biome.vegetationEnd, 0f, 1f);
            biome.rockEnd       = EditorGUILayout.Slider("Rock End", biome.rockEnd, 0f, 1f);
            biome.snowStart     = EditorGUILayout.Slider("Snow Start", biome.snowStart, 0f, 1f);
            bool changed = EditorGUI.EndChangeCheck();

            EditorGUI.indentLevel--;

            // Enforce ordering
            biome.sandEnd       = Mathf.Max(biome.sandEnd, biome.waterLine);
            biome.vegetationEnd = Mathf.Max(biome.vegetationEnd, biome.sandEnd);
            biome.rockEnd       = Mathf.Max(biome.rockEnd, biome.vegetationEnd);
            biome.snowStart     = Mathf.Max(biome.snowStart, biome.rockEnd);

            EditorGUILayout.Space(4);

            // ── Slope Zones ──
            EditorGUILayout.LabelField("Slope Zones", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            EditorGUI.BeginChangeCheck();
            biome.rockSlopeStart = EditorGUILayout.Slider("Rock Slope Start", biome.rockSlopeStart, 0.1f, 0.8f);
            biome.rockSlopeEnd   = EditorGUILayout.Slider("Rock Slope End", biome.rockSlopeEnd, 0.3f, 1f);
            changed |= EditorGUI.EndChangeCheck();

            EditorGUI.indentLevel--;

            biome.rockSlopeEnd = Mathf.Max(biome.rockSlopeEnd, biome.rockSlopeStart);

            if (changed)
            {
                Undo.RecordObject(terrain, "Modify Biome Distribution");
                terrain.Biome = biome;
            }
        }

        // ================================================================
        //  Gradient Preview Bar
        // ================================================================

        private void DrawGradientPreview(ref FullWorldTerrain.BiomeDistribution biome)
        {
            const float barHeight = 24f;

            EditorGUILayout.Space(2);
            var rect = GUILayoutUtility.GetRect(10f, barHeight, GUILayout.ExpandWidth(true));

            EnsureGradientBar();
            UpdateGradientBar(biome);
            GUI.DrawTexture(rect, m_GradientBar, ScaleMode.StretchToFill);

            DrawThresholdLine(rect, biome.waterLine, Color.cyan);
            DrawThresholdLine(rect, biome.sandEnd, k_SandColor * 1.3f);
            DrawThresholdLine(rect, biome.vegetationEnd, Color.green);
            DrawThresholdLine(rect, biome.rockEnd, Color.gray);
            DrawThresholdLine(rect, biome.snowStart, Color.white);

            var labelRect = new Rect(rect.x, rect.yMax + 2f, rect.width, EditorGUIUtility.singleLineHeight);
            float labelW = labelRect.width / 5f;
            GUI.Label(new Rect(labelRect.x, labelRect.y, labelW, labelRect.height),
                $"W:{biome.waterLine:F2}", EditorStyles.miniLabel);
            GUI.Label(new Rect(labelRect.x + labelW, labelRect.y, labelW, labelRect.height),
                $"S:{biome.sandEnd:F2}", EditorStyles.miniLabel);
            GUI.Label(new Rect(labelRect.x + labelW * 2, labelRect.y, labelW, labelRect.height),
                $"V:{biome.vegetationEnd:F2}", EditorStyles.miniLabel);
            GUI.Label(new Rect(labelRect.x + labelW * 3, labelRect.y, labelW, labelRect.height),
                $"R:{biome.rockEnd:F2}", EditorStyles.miniLabel);
            GUI.Label(new Rect(labelRect.x + labelW * 4, labelRect.y, labelW, labelRect.height),
                $"Sn:{biome.snowStart:F2}", EditorStyles.miniLabel);

            EditorGUILayout.Space(18f);
        }

        private void EnsureGradientBar()
        {
            if (m_GradientBar != null) return;
            m_GradientBar = new Texture2D(256, 1, TextureFormat.RGBA32, false, true);
            m_GradientBar.hideFlags = HideFlags.DontSave;
        }

        private void UpdateGradientBar(FullWorldTerrain.BiomeDistribution biome)
        {
            if (m_GradientBar == null) return;

            int w = m_GradientBar.width;
            var pixels = m_GradientBar.GetPixels();

            for (int x = 0; x < w; x++)
            {
                float t = (float)x / (w - 1);
                Color c;
                if (t < biome.waterLine)          c = k_WaterColor;
                else if (t < biome.sandEnd)       c = k_SandColor;
                else if (t < biome.vegetationEnd)  c = k_VegetColor;
                else if (t < biome.rockEnd)       c = k_RockColor;
                else                              c = k_SnowColor;
                pixels[x] = c;
            }

            m_GradientBar.SetPixels(pixels);
            m_GradientBar.Apply(false);
        }

        private static void DrawThresholdLine(Rect barRect, float normalizedPos, Color color)
        {
            float x = barRect.x + normalizedPos * barRect.width;
            var lineRect = new Rect(x, barRect.y, 2f, barRect.height);
            EditorGUI.DrawRect(lineRect, color);
        }

        // ================================================================
        //  Selected Param Detail Editor
        // ================================================================

        private void DrawSelectedParamDetail()
        {
            if (m_SelectedIndex < 0 || m_SelectedIndex >= m_LayersProperty.arraySize)
            {
                m_SelectedIndex = -1;
                DestroyParamEditor();
                return;
            }

            var paramObj = m_LayersProperty.GetArrayElementAtIndex(m_SelectedIndex).FindPropertyRelative("param").objectReferenceValue;
            if (paramObj == null)
            {
                DestroyParamEditor();
                EditorGUILayout.HelpBox("No LayerParam assigned to the selected slot.", MessageType.Warning);
                return;
            }

            if (m_CachedParamRef != paramObj)
                RefreshParamEditor();

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField($"Layer Properties — {paramObj.name}", EditorStyles.boldLabel);
            EditorGUILayout.BeginVertical();

            if (m_ParamEditor != null)
            {
                EditorGUI.BeginChangeCheck();
                m_ParamEditor.OnInspectorGUI();
                if (EditorGUI.EndChangeCheck()) RequestRegenerate();
            }
            else
            {
                var paramSo = new SerializedObject(paramObj);
                paramSo.Update();
                var prop = paramSo.GetIterator();
                bool enterChildren = true;
                while (prop.NextVisible(enterChildren))
                {
                    if (prop.name == "m_Script") { enterChildren = false; continue; }
                    EditorGUILayout.PropertyField(prop, true);
                    enterChildren = false;
                }
                paramSo.ApplyModifiedProperties();
            }

            EditorGUILayout.EndVertical();
        }

        // ================================================================
        //  Param Editor Lifecycle
        // ================================================================

        private void RefreshParamEditor()
        {
            DestroyParamEditor();
            if (m_SelectedIndex < 0 || m_SelectedIndex >= m_LayersProperty.arraySize) return;

            var paramObj = m_LayersProperty.GetArrayElementAtIndex(m_SelectedIndex).FindPropertyRelative("param").objectReferenceValue;
            if (paramObj == null) return;

            m_CachedParamRef = paramObj;
            m_ParamEditor = Editor.CreateEditor(paramObj);
        }

        private void DestroyParamEditor()
        {
            if (m_ParamEditor != null) { UnityEngine.Object.DestroyImmediate(m_ParamEditor); m_ParamEditor = null; }
            m_CachedParamRef = null;
        }

        // ================================================================
        //  Instance Count Display
        // ================================================================

        private void DrawInstanceCount(FullWorldTerrain terrain)
        {
            int trees = 0, bushes = 0;
            var instances = terrain.VegetationInstances;
            if (instances != null)
            {
                foreach (var inst in instances)
                {
                    if (inst.type == VegetationType.Tree) trees++;
                    else bushes++;
                }
            }
            int total = instances != null ? instances.Count : 0;
            EditorGUILayout.LabelField($"Instances: {total} total ({trees} trees, {bushes} bushes)", EditorStyles.miniLabel);
        }

        // ================================================================
        //  Layer List Callbacks
        // ================================================================

        private void OnLayerReordered(ReorderableList list)
        {
            editor.serializedObject.ApplyModifiedProperties();
            RequestRegenerate();
        }

        private void OnLayerRemoved(ReorderableList list)
        {
            var elementProp = m_LayersProperty.GetArrayElementAtIndex(list.index);
            var paramProp = elementProp.FindPropertyRelative("param");
            var maskProp = elementProp.FindPropertyRelative("mask");

            if (paramProp.objectReferenceValue != null)
            {
                string path = AssetDatabase.GetAssetPath(paramProp.objectReferenceValue);
                if (!string.IsNullOrEmpty(path) &&
                    EditorUtility.DisplayDialog("Delete Layer Asset",
                        $"Also delete the asset file?\n{path}", "Delete Asset", "Keep Asset"))
                    AssetDatabase.DeleteAsset(path);
            }

            if (maskProp.objectReferenceValue is BaseMaskData maskData)
            {
                maskData.MarkDirty();
                string path = AssetDatabase.GetAssetPath(maskData);
                if (!string.IsNullOrEmpty(path) &&
                    EditorUtility.DisplayDialog("Delete Mask Asset",
                        $"Also delete the mask asset file?\n{path}", "Delete Asset", "Keep Asset"))
                    AssetDatabase.DeleteAsset(path);
            }

            if (m_SelectedIndex == list.index) { m_SelectedIndex = -1; DestroyParamEditor(); }
            else if (m_SelectedIndex > list.index) m_SelectedIndex--;

            m_LayersProperty.DeleteArrayElementAtIndex(list.index);

            editor.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(editor.target);
            RequestRegenerate();
        }

        // ================================================================
        //  Add Layer Menu
        // ================================================================

        private void ShowAddLayerMenu(Rect buttonRect)
        {
            var menu = new GenericMenu();

            menu.AddItem(new GUIContent("Empty Slot"), false, OnAddEmptySlot);

            menu.AddSeparator("");

            var paramTypes = GetAllVegetationLayerParamTypes();
            foreach (var type in paramTypes)
            {
                var captured = type;
                menu.AddItem(new GUIContent("Create/" + ObjectNames.NicifyVariableName(type.Name)),
                    false, () => OnAddLayerSelected(captured));
            }

            menu.DropDown(buttonRect);
        }

        private void OnAddEmptySlot()
        {
            int idx = m_LayersProperty.arraySize;
            m_LayersProperty.InsertArrayElementAtIndex(idx);

            var newEntry = m_LayersProperty.GetArrayElementAtIndex(idx);
            newEntry.FindPropertyRelative("param").objectReferenceValue = null;
            newEntry.FindPropertyRelative("enable").boolValue = true;
            newEntry.FindPropertyRelative("mask").objectReferenceValue = null;

            editor.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(editor.target);
            RequestRegenerate();
        }

        private void OnAddLayerSelected(Type paramType)
        {
            string savePath = EditorUtility.SaveFilePanelInProject(
                $"Create {ObjectNames.NicifyVariableName(paramType.Name)}",
                $"New{paramType.Name}.asset", "asset",
                $"Choose a location to save the {paramType.Name} asset.");
            if (string.IsNullOrEmpty(savePath)) return;

            var instance = ScriptableObject.CreateInstance(paramType);
            AssetDatabase.CreateAsset(instance, savePath);
            AssetDatabase.SaveAssets();

            int idx = m_LayersProperty.arraySize;
            m_LayersProperty.InsertArrayElementAtIndex(idx);

            var newEntry = m_LayersProperty.GetArrayElementAtIndex(idx);
            newEntry.FindPropertyRelative("param").objectReferenceValue = instance;
            newEntry.FindPropertyRelative("enable").boolValue = true;
            newEntry.FindPropertyRelative("mask").objectReferenceValue = null;

            editor.serializedObject.ApplyModifiedProperties();
            EditorUtility.SetDirty(editor.target);
            RequestRegenerate();
        }

        // ================================================================
        //  Helpers
        // ================================================================

        private void RequestRegenerate() { if (TerrainTarget != null) TerrainTarget.MarkDirty(); }

        private BaseMaskData GetMaskDataAt(int index)
        {
            if (m_LayersProperty == null || index >= m_LayersProperty.arraySize) return null;
            return m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("mask")?.objectReferenceValue as BaseMaskData;
        }

        private Texture2D GetOrCreateWhiteMaskTex()
        {
            if (m_WhiteMaskTex != null) return m_WhiteMaskTex;
            m_WhiteMaskTex = new Texture2D(2, 2, TextureFormat.RGBAFloat, false, true);
            var px = new Color[4];
            for (int i = 0; i < px.Length; i++) px[i] = Color.white;
            m_WhiteMaskTex.SetPixels(px);
            m_WhiteMaskTex.Apply(false, true);
            return m_WhiteMaskTex;
        }

        // ================================================================
        //  Lifecycle
        // ================================================================

        public override void OnEnable() { base.OnEnable(); InitializeLayerList(); }

        public override void OnDisable()
        {
            if (MaskEditSession.Instance.Current != null)
            {
                MaskEditSession.Instance.Deactivate();
                m_EditingMaskIndex = -1;
            }

            base.OnDisable();
            DestroyParamEditor();
            if (m_WhiteMaskTex != null)
            {
                UnityEngine.Object.DestroyImmediate(m_WhiteMaskTex);
                m_WhiteMaskTex = null;
            }
            layerList = null;
        }
    }
}
