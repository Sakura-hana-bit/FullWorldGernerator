using FullWorld;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

namespace FullWorldEditor
{
    /// <summary>
    /// Terrain effect layer management tool.
    /// Provides a reorderable list of layers with per-layer debug preview,
    /// PS-style mask thumbnails, and property editing for the selected layer.
    /// Click "Edit" button next to mask controls to enter GPU brush editing mode.
    /// </summary>
    public class TerrianEffectLayerTools : BaseFullWorldEditorTool
    {
        // ================================================================
        //  Constants & Static
        // ================================================================

        private const float k_ThumbnailSize = 64f;
        private const float k_BaseElementHeight = 46f;
        private static readonly Color k_BorderColor = new Color(0.15f, 0.15f, 0.15f, 1f);
        private static readonly Color k_EditingBorderColor = new Color(0.2f, 0.8f, 1f, 1f);
        private static readonly Type s_BaseParamType = typeof(BaseTerrianLayerParam);
        private static List<Type> s_CachedParamTypes;

        public static List<Type> GetAllTerrianLayerParamTypes()
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
        //  Preview Cache
        // ================================================================

        private enum PreviewMode { Height, Grad, Combined, Ridge, Valley }
        private PreviewMode m_PreviewMode = PreviewMode.Height;
        private Dictionary<int, Texture2D> m_PreviewCache = new Dictionary<int, Texture2D>();
        private int m_PreviewCacheMode = -1;
        private Texture2D m_WhiteMaskTex;

        // ================================================================
        //  Accessors & Constructor
        // ================================================================

        private FullWorldTerrain TerrainTarget => editor.target as FullWorldTerrain;

        public TerrianEffectLayerTools(FullWorldTerrainEditor editor) : base(editor)
        {
            m_Icon = Resources.Load<Texture2D>("TerrianLayer");
            m_Name = "TerrianLayer";
        }

        // ================================================================
        //  Layer List Setup
        // ================================================================

        private void InitializeLayerList()
        {
            var so = editor.serializedObject;
            m_LayersProperty = so.FindProperty("m_Layers");

            layerList = new ReorderableList(so, m_LayersProperty, true, true, true, true);
            layerList.drawHeaderCallback = r => EditorGUI.LabelField(r, "Terrain Effect Layers");
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

            float cursor = rect.x + 18f;
            var debugRT = TerrainTarget?.GetDebugLayerCache(index);

            rect.y += EditorGUIUtility.singleLineHeight * 0.5f;

            if (debugRT != null)
            {
                DrawLayerPreviewThumbnail(rect, cursor, debugRT, index);
                DrawLinkSymbol(new Rect(cursor + k_ThumbnailSize + 1f, rect.y + k_ThumbnailSize * 0.3f, 14f, k_ThumbnailSize * 0.4f));
                DrawMaskThumbnail(rect, index, cursor + k_ThumbnailSize + 16f, Mathf.Min(k_ThumbnailSize, rect.width * 0.45f));
            }
            else
            {
                DrawMaskThumbnail(rect, index, cursor, Mathf.Min(k_ThumbnailSize, rect.width * 0.45f));
            }

            rect.y += k_ThumbnailSize + 4f;
            DrawMaskControls(rect, index, cursor);
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

            if (paramProp.objectReferenceValue != null)
            {
                var layerSo = new SerializedObject(paramProp.objectReferenceValue);
                var typeProp = layerSo.FindProperty("GetLayerType");
                if (typeProp != null)
                    EditorGUI.LabelField(detailRect, $"Type: {typeProp.stringValue}", EditorStyles.miniLabel);
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
                h += k_ThumbnailSize + EditorGUIUtility.singleLineHeight + 8f;

            return h;
        }

        // ================================================================
        //  Preview & Mask Drawing
        // ================================================================

        private static readonly GUIStyle s_LinkStyle = new GUIStyle
        {
            alignment = TextAnchor.MiddleCenter,
            fontSize = 13,
            normal = { textColor = Color.black },
            hover = { textColor = Color.black },
            active = { textColor = Color.black },
            focused = { textColor = Color.black },
        };

        private void DrawLinkSymbol(Rect rect) => GUI.Label(rect, "∞", s_LinkStyle);

        private void DrawLayerPreviewThumbnail(Rect rect, float x, RenderTexture debugRT, int index)
        {
            var previewTex = ConvertRTToPreview(debugRT, index);
            if (previewTex == null) return;

            float w = Mathf.Min(k_ThumbnailSize, rect.width * 0.45f);
            var previewRect = new Rect(x, rect.y, w, k_ThumbnailSize);
            DrawPreviewBorder(previewRect);
            EditorGUI.DrawPreviewTexture(previewRect, previewTex);
        }

        private void DrawMaskThumbnail(Rect rect, int index, float x, float width)
        {
            BaseMaskData maskData = null;
            if (m_LayersProperty != null && index < m_LayersProperty.arraySize)
            {
                var maskProp = m_LayersProperty.GetArrayElementAtIndex(index).FindPropertyRelative("mask");
                maskData = maskProp?.objectReferenceValue as BaseMaskData;
            }

            var thumbRect = new Rect(x, rect.y, width, k_ThumbnailSize);

            Texture previewTex = maskData?.PreviewRT;
            if (previewTex == null) previewTex = GetOrCreateWhiteMaskTex();

            // 正在编辑的 Mask 使用高亮边框
            bool isEditing = maskData != null && MaskEditSession.Instance.Current == maskData;
            DrawPreviewBorder(thumbRect, isEditing ? k_EditingBorderColor : k_BorderColor, isEditing ? 2f : 1f);
            EditorGUI.DrawPreviewTexture(thumbRect, previewTex);

            EditorGUI.LabelField(new Rect(x, rect.y + k_ThumbnailSize + 1f, width, EditorGUIUtility.singleLineHeight),
                isEditing ? "Mask (Editing)" : "Mask", isEditing ? EditorStyles.boldLabel : EditorStyles.miniLabel);
        }

        private void DrawMaskControls(Rect rect, int index, float leftCursor)
        {
            float width = rect.x + rect.width - leftCursor;
            float btnWidth = 42f;
            float btnGap = 3f;
            int btnCount = 3; // New, Clear, Edit

            var objRect = new Rect(leftCursor, rect.y, width - (btnWidth + btnGap) * btnCount - 4f, EditorGUIUtility.singleLineHeight);
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

            float btnX = objRect.x + objRect.width + btnGap;

            // New
            if (GUI.Button(new Rect(btnX, rect.y, btnWidth, EditorGUIUtility.singleLineHeight), "New", EditorStyles.miniButton))
                CreateMaskAsset(index);

            bool hasMask = GetMaskDataAt(index) != null;
            var maskData = GetMaskDataAt(index);
            bool isEditingThis = maskData != null && MaskEditSession.Instance.Current == maskData;

            // Clear
            EditorGUI.BeginDisabledGroup(!hasMask || isEditingThis);
            if (GUI.Button(new Rect(btnX + (btnWidth + btnGap), rect.y, btnWidth, EditorGUIUtility.singleLineHeight), "Clear", EditorStyles.miniButton))
                ClearMask(index);
            EditorGUI.EndDisabledGroup();

            // Edit / Done
            if (isEditingThis)
            {
                GUI.backgroundColor = Color.cyan;
                if (GUI.Button(new Rect(btnX + (btnWidth + btnGap) * 2, rect.y, btnWidth, EditorGUIUtility.singleLineHeight), "Done", EditorStyles.miniButton))
                {
                    MaskEditSession.Instance.Deactivate();
                    m_EditingMaskIndex = -1;
                }
                GUI.backgroundColor = Color.white;
            }
            else
            {
                EditorGUI.BeginDisabledGroup(!hasMask);
                if (GUI.Button(new Rect(btnX + (btnWidth + btnGap) * 2, rect.y, btnWidth, EditorGUIUtility.singleLineHeight), "Edit", EditorStyles.miniButton))
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

        private static void DrawPreviewBorder(Rect innerRect)
        {
            DrawPreviewBorder(innerRect, k_BorderColor, 1f);
        }

        // ================================================================
        //  Mask Asset Operations
        // ================================================================

        private void CreateMaskAsset(int index)
        {
            string savePath = EditorUtility.SaveFilePanelInProject(
                "Create Mask Asset", $"Layer{index}_Mask.asset", "asset",
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
            if (layerList == null) InitializeLayerList();

            EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Terrain Effect Layers", EditorStyles.largeLabel);
            EditorGUILayout.HelpBox("Drag items to reorder the execution order of terrain effect layers.", MessageType.Info);

            layerList.DoLayoutList();

            DrawSelectedLayerPreview();
            DrawSelectedParamDetail();

            EditorGUILayout.EndVertical();
        }

        private void DrawSelectedLayerPreview()
        {
            if (m_SelectedIndex < 0 || m_SelectedIndex >= m_LayersProperty.arraySize) return;

            var debugRT = TerrainTarget?.GetDebugLayerCache(m_SelectedIndex);
            if (debugRT == null) return;

            EditorGUILayout.Space(4);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("Layer Preview", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            m_PreviewMode = (PreviewMode)EditorGUILayout.EnumPopup(m_PreviewMode, GUILayout.Width(90f));
            if (EditorGUI.EndChangeCheck()) ClearPreviewCache();

            // Export button
            if (GUILayout.Button("Export ▾", EditorStyles.miniButton, GUILayout.Width(70f)))
            {
                var menu = new GenericMenu();
                menu.AddItem(new GUIContent("Height"), false, () => ExportChannel(debugRT, ExportData.Height));
                menu.AddItem(new GUIContent("Slope"), false, () => ExportChannel(debugRT, ExportData.Slope));
                menu.AddItem(new GUIContent("Ridge Map"), false, () => ExportChannel(debugRT, ExportData.Ridge));
                menu.AddItem(new GUIContent("Valley Mask"), false, () => ExportChannel(debugRT, ExportData.Valley));
                menu.AddItem(new GUIContent("Erosion Delta"), false, () => ExportChannel(debugRT, ExportData.ErosionDelta));
                menu.AddItem(new GUIContent("All Channels (RGBA)"), false, () => ExportChannel(debugRT, ExportData.All));
                menu.DropDown(GUILayoutUtility.GetLastRect());
            }

            EditorGUILayout.EndHorizontal();

            var tex = ConvertRTToPreview(debugRT, m_SelectedIndex);
            if (tex == null) return;

            float size = 256f;
            var rect = GUILayoutUtility.GetRect(size, size, GUILayout.MaxWidth(size), GUILayout.MaxHeight(size));
            DrawPreviewBorder(rect);
            EditorGUI.DrawPreviewTexture(rect, tex, null, ScaleMode.ScaleToFit);
        }

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
                var so = new SerializedObject(paramObj);
                so.Update();
                var prop = so.GetIterator();
                bool enterChildren = true;
                while (prop.NextVisible(enterChildren))
                {
                    if (prop.name == "m_Script") { enterChildren = false; continue; }
                    EditorGUILayout.PropertyField(prop, true);
                    enterChildren = false;
                }
                so.ApplyModifiedProperties();
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

            var paramTypes = GetAllTerrianLayerParamTypes();
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
        //  Export
        // ================================================================

        private enum ExportData { Height, Slope, Ridge, Valley, ErosionDelta, All }

        /// <summary>
        /// Unpacks 4 channels from a Pack4-encoded float.
        /// Pack4 stores 4 values as: s0 + s1/256 + s2/65536 + s3/16777216
        /// where each s_i = floor(clamp01(v_i) * 255) / 255.
        /// Returns the 4 normalized [0,1] channel values.
        /// </summary>
        private static void Unpack4(float packed, out float ch0, out float ch1, out float ch2, out float ch3)
        {
            double scaled = (double)packed * 255.0;
            int b0 = (int)System.Math.Floor(scaled) & 0xFF;
            scaled = (scaled - System.Math.Floor(scaled)) * 256.0;
            int b1 = (int)System.Math.Floor(scaled) & 0xFF;
            scaled = (scaled - System.Math.Floor(scaled)) * 256.0;
            int b2 = (int)System.Math.Floor(scaled) & 0xFF;
            scaled = (scaled - System.Math.Floor(scaled)) * 256.0;
            int b3 = (int)System.Math.Floor(scaled) & 0xFF;

            ch0 = b0 / 255f;
            ch1 = b1 / 255f;
            ch2 = b2 / 255f;
            ch3 = b3 / 255f;
        }

        private void ExportChannel(RenderTexture rt, ExportData channel)
        {
            if (rt == null) return;

            string channelName;
            switch (channel)
            {
                case ExportData.Height:       channelName = "Height"; break;
                case ExportData.Slope:        channelName = "Slope"; break;
                case ExportData.Ridge:        channelName = "Ridge"; break;
                case ExportData.Valley:       channelName = "Valley"; break;
                case ExportData.ErosionDelta: channelName = "ErosionDelta"; break;
                default:                      channelName = "All"; break;
            }
            string defaultName = $"Layer{m_SelectedIndex}_{channelName}";

            string savePath = EditorUtility.SaveFilePanelInProject(
                $"Export {channelName}", $"{defaultName}.raw", "raw",
                $"Export {channelName} as raw.");
            if (string.IsNullOrEmpty(savePath)) return;

            // Read back source pixels
            var prev = RenderTexture.active;
            RenderTexture.active = rt;
            var srcTex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            srcTex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            srcTex.Apply();
            var srcPixels = srcTex.GetPixels();
            RenderTexture.active = prev;
            UnityEngine.Object.DestroyImmediate(srcTex);

            int w = rt.width, h = rt.height;
            var outTex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
      
            var outPixels = new Color[w * h];

            for (int i = 0; i < srcPixels.Length; i++)
            {
                float height = srcPixels[i].r;
                float slopeX = srcPixels[i].g;
                float slopeY = srcPixels[i].b;
                float slopeMag = Mathf.Sqrt(slopeX * slopeX + slopeY * slopeY);
                float packed = srcPixels[i].a;

                // Unpack A channel: 4 x 8-bit values packed by Pack4()
                Unpack4(packed, out float erosionDeltaN, out float ridgeMapN, out float treesN, out float debugN);
                // Convert from [0,1] back to [-1,1]
                float erosionDelta = erosionDeltaN * 2f - 1f;
                float ridgeMap = ridgeMapN * 2f - 1f;

                Color c;
                switch (channel)
                {
                    case ExportData.Height:
                        c = new Color(height, height, height, 1f);
                        break;
                    case ExportData.Slope:
                        c = new Color(slopeMag, slopeMag, slopeMag, 1f);
                        break;
                    case ExportData.Ridge:
                        {
                            // -1 = crease/valley, +1 = ridge → remap to [0,1]
                            float v = ridgeMap * 0.5f + 0.5f;
                            c = new Color(v, v, v, 1f);
                            break;
                        }
                    case ExportData.Valley:
                        {
                            // Valley mask: -ridgeMap, so deep valley → 1
                            float v = Mathf.Clamp01(-ridgeMap);
                            c = new Color(v, v, v, 1f);
                            break;
                        }
                    case ExportData.ErosionDelta:
                        {
                            float v = erosionDelta * 0.5f + 0.5f;
                            c = new Color(v, v, v, 1f);
                            break;
                        }
                    case ExportData.All:
                    default:
                        c = new Color(height, slopeMag, ridgeMap * 0.5f + 0.5f, 1f);
                        break;
                }

                outPixels[i] = c;
            }

            outTex.SetPixels(outPixels);
            outTex.Apply(true);
            Debug.Log(outTex.isReadable);
            var pngData = outTex.EncodeToEXR();

            ExportTextureToRAW(outTex, savePath);

            UnityEngine.Object.DestroyImmediate(outTex);

            //System.IO.File.WriteAllBytes(savePath, pngData);
            AssetDatabase.ImportAsset(savePath);

            var importer = AssetImporter.GetAtPath(savePath) as TextureImporter;
            if (importer != null)
            {
                importer.textureType = TextureImporterType.Default;
                importer.sRGBTexture = false;
                importer.SaveAndReimport();
            }

            Debug.Log($"[TerrianEffectLayerTools] Exported {channelName} to {savePath}");
        }

        // ================================================================
        //  Preview Conversion
        // ================================================================

        private Texture2D ConvertRTToPreview(RenderTexture rt, int index)
        {
            if (rt == null) return null;

            int mode = (int)m_PreviewMode;
            if (m_PreviewCacheMode != mode)
            {
                m_PreviewCacheMode = mode;
                ClearPreviewCache();
            }

            if (m_PreviewCache.TryGetValue(index, out var cached) && cached != null)
                return cached;

            var prev = RenderTexture.active;
            RenderTexture.active = rt;

            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0, false);

            var pixels = tex.GetPixels();
            for (int i = 0; i < pixels.Length; i++)
            {
                float h = pixels[i].r;
                float slopeMag = Mathf.Sqrt(pixels[i].g * pixels[i].g + pixels[i].b * pixels[i].b);
                float packed = pixels[i].a;

                // Unpack ridgeMap from A channel
                Unpack4(packed, out _, out float ridgeMapN, out _, out _);
                float ridgeMap = ridgeMapN * 2f - 1f;

                switch (m_PreviewMode)
                {
                    case PreviewMode.Grad:
                        pixels[i] = new Color(slopeMag, slopeMag, slopeMag, 1f);
                        break;
                    case PreviewMode.Combined:
                        pixels[i] = new Color(h, slopeMag, slopeMag * 0.5f, 1f);
                        break;
                    case PreviewMode.Ridge:
                        {
                            float rn = ridgeMap * 0.5f + 0.5f;
                            pixels[i] = new Color(rn, rn, rn, 1f);
                            break;
                        }
                    case PreviewMode.Valley:
                        {
                            float v = Mathf.Clamp01(-ridgeMap);
                            pixels[i] = new Color(v, v * 0.8f, v * 0.3f, 1f);
                            break;
                        }
                    default:
                        pixels[i] = new Color(h, h, h, 1f);
                        break;
                }
            }
            tex.SetPixels(pixels);
            tex.Apply(false, true);
            RenderTexture.active = prev;

            m_PreviewCache[index] = tex;
            return tex;
        }

        internal void ClearPreviewCache()
        {
            foreach (var kvp in m_PreviewCache)
                if (kvp.Value != null)
                    UnityEngine.Object.DestroyImmediate(kvp.Value);
            m_PreviewCache.Clear();
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
        public static void ExportTextureToRAW(Texture2D texture, string filePath,
    int bitDepth = 16, int channel = 4, bool flipVertical = false)
        {
            if (texture == null)
            {
                Debug.LogError("Texture is null!");
                return;
            }

            if (!texture.isReadable)
            {
                Debug.LogError($"Texture '{texture.name}' is not readable. Please enable Read/Write in import settings.");
                return;
            }

            int width = texture.width;
            int height = texture.height;

            Debug.Log($"Exporting Texture: {texture.name} ({width}x{height}) to {filePath}");
            Debug.Log($"Bit Depth: {bitDepth}, Channel: {channel}, Flip Vertical: {flipVertical}");

            // 获取像素数据
            Color[] pixels = texture.GetPixels();

            using (FileStream fs = new FileStream(filePath, FileMode.Create))
            using (BinaryWriter writer = new BinaryWriter(fs))
            {
                for (int y = 0; y < height; y++)
                {
                    int writeY = flipVertical ? (height - 1 - y) : y;

                    for (int x = 0; x < width; x++)
                    {
                        int index = writeY * width + x;
                        Color pixel = pixels[index];

                        float value = 0f;

                        // 选择通道
                        switch (channel)
                        {
                            case 0: // R通道
                                value = pixel.r;
                                break;
                            case 1: // G通道
                                value = pixel.g;
                                break;
                            case 2: // B通道
                                value = pixel.b;
                                break;
                            case 3: // A通道
                                value = pixel.a;
                                break;
                            case 4: // 灰度（平均值）
                            default:
                                value = (pixel.r + pixel.g + pixel.b) / 3f;
                                break;
                        }

                        // 写入数据
                        switch (bitDepth)
                        {
                            case 8:
                                byte byteValue = (byte)(value * 255f);
                                writer.Write(byteValue);
                                break;

                            case 16:
                                ushort ushortValue = (ushort)(value * 65535f);
                                writer.Write(ushortValue);
                                break;

                            case 32:
                                // 32位浮点
                                writer.Write(value);
                                break;

                            default:
                                Debug.LogError($"Unsupported bit depth: {bitDepth}. Using 16-bit.");
                                ushort fallbackValue = (ushort)(value * 65535f);
                                writer.Write(fallbackValue);
                                break;
                        }
                    }
                }
            }

            Debug.Log($"RAW file exported: {filePath}");
            Debug.Log($"File size: {new FileInfo(filePath).Length} bytes");
        }
        // ================================================================
        //  Lifecycle
        // ================================================================

        public override void OnEnable() { base.OnEnable(); InitializeLayerList(); }

        public override void OnDisable()
        {
            // 退出编辑模式 → Deactivate 会触发 SyncToCpu 持久化
            if (MaskEditSession.Instance.Current != null)
            {
                MaskEditSession.Instance.Deactivate();
                m_EditingMaskIndex = -1;
            }

            base.OnDisable();
            DestroyParamEditor();
            ClearPreviewCache();
            if (m_WhiteMaskTex != null)
            {
                UnityEngine.Object.DestroyImmediate(m_WhiteMaskTex);
                m_WhiteMaskTex = null;
            }
            layerList = null;
        }
    }
}
