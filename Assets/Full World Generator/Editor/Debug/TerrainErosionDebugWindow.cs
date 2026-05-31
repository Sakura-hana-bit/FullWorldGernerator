using FullWorld;
using UnityEditor;
using UnityEngine;

namespace FullWorldGenerator.Editor
{
    public class TerrainErosionDebugWindow : EditorWindow
    {
        [SerializeField] ErosionLayerParam erosionLayer;

        #region Heightmap Generation

        [Header("Heightmap Generation")]
        [SerializeField] float heightFrequency = 2.0f;
        [SerializeField] int heightOctaves = 8;
        [SerializeField] float heightLacunarity = 2.0f;
        [SerializeField] float heightGain = 0.5f;
        [SerializeField] float heightAmp = 0.3f;
        [SerializeField] float heightFunctionScale = 1.0f;
        [SerializeField] float heightScale = 1.0f;

        #endregion

        enum DebugMode
        {
            ErodedHeight = 0,
            RidgeMap = 1,
            Trees = 2,
            FadeTarget = 3,
            ErosionDelta = 4,
            OriginalHeight = 5,
        }

        [SerializeField] DebugMode debugMode = DebugMode.ErodedHeight;
        [SerializeField] float fadeTargetDivisor = 1.0f;
        [SerializeField] int resolution = 512;

        const string kComputeShaderPath = "Assets/Full World Generator/Runtime/Resources/Shader/TerrainErosionCS.compute";
        ComputeShader computeShader;
        Texture2D heightSlopeMap;
        RenderTexture outputTexture;
        Vector2 paramScroll;
        bool showErosion = true;
        bool showHeightOffset;
        bool showTerrainConstants;
        bool showHeightmapGen = true;
        bool needsDispatch = true;
        SerializedObject serializedObj;

        // Scene preview
        GameObject previewGO;
        Mesh previewMesh;
        MeshFilter previewMeshFilter;
        bool showScenePreview;
        float previewHeightScale = 50f;
        float previewMeshResolution = 256f;
        Color[] heightReadback;

        static readonly int
            ID_ErosionScale = Shader.PropertyToID("_ErosionScale"),
            ID_ErosionStrength = Shader.PropertyToID("_ErosionStrength"),
            ID_ErosionGullyWeight = Shader.PropertyToID("_ErosionGullyWeight"),
            ID_ErosionDetail = Shader.PropertyToID("_ErosionDetail"),
            ID_ErosionRounding = Shader.PropertyToID("_ErosionRounding"),
            ID_ErosionOnset = Shader.PropertyToID("_ErosionOnset"),
            ID_ErosionAssumedSlope = Shader.PropertyToID("_ErosionAssumedSlope"),
            ID_ErosionCellScale = Shader.PropertyToID("_ErosionCellScale"),
            ID_ErosionNormalization = Shader.PropertyToID("_ErosionNormalization"),
            ID_ErosionOctaves = Shader.PropertyToID("_ErosionOctaves"),
            ID_ErosionLacunarity = Shader.PropertyToID("_ErosionLacunarity"),
            ID_ErosionGain = Shader.PropertyToID("_ErosionGain"),
            ID_TerrainHeightOffset = Shader.PropertyToID("_TerrainHeightOffset"),
            ID_ErosionEnabled = Shader.PropertyToID("_ErosionEnabled"),
            ID_DefaultHeight = Shader.PropertyToID("_DefaultHeight"),
            ID_GrassHeight = Shader.PropertyToID("_GrassHeight"),
            ID_WaterHeight = Shader.PropertyToID("_WaterHeight"),
            ID_DebugMode = Shader.PropertyToID("_DebugMode"),
            ID_HeightSlopeMap = Shader.PropertyToID("_HeightSlopeMap"),
            ID_Output = Shader.PropertyToID("_Output"),
            ID_HeightFrequency = Shader.PropertyToID("_HeightFrequency"),
            ID_HeightOctaves = Shader.PropertyToID("_HeightOctaves"),
            ID_HeightLacunarity = Shader.PropertyToID("_HeightLacunarity"),
            ID_HeightGain = Shader.PropertyToID("_HeightGain"),
            ID_HeightAmp = Shader.PropertyToID("_HeightAmp"),
            ID_HeightFunctionScale = Shader.PropertyToID("_HeightFunctionScale"),
            ID_HeightScale = Shader.PropertyToID("_HeightScale"),
            ID_FadeTargetDivisor = Shader.PropertyToID("_FadeTargetDivisor");

        [MenuItem("Tools/Full World Generator/Terrain Erosion Debug")]
        static void Open()
        {
            GetWindow<TerrainErosionDebugWindow>("Erosion Debug");
        }

        void OnEnable()
        {
            serializedObj = new SerializedObject(this);
            if (computeShader == null)
                computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kComputeShaderPath);
        }

        void OnGUI()
        {
            serializedObj.Update();

            // --- Resources (fixed at top) ---
            EditorGUILayout.LabelField("Resources", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();
            heightSlopeMap = (Texture2D)EditorGUILayout.ObjectField("Height+Slope Map", heightSlopeMap, typeof(Texture2D), false);
            if (EditorGUI.EndChangeCheck()) needsDispatch = true;

            EditorGUILayout.BeginHorizontal();
            GUILayout.Space(EditorGUIUtility.labelWidth + 4);
            if (GUILayout.Button("Generate Initial Map", GUILayout.Height(20)))
            {
                heightSlopeMap = GenerateInitialHeightmap(resolution);
                EditorUtility.SetDirty(this);
                needsDispatch = true;
            }
            EditorGUILayout.EndHorizontal();

            resolution = EditorGUILayout.IntPopup("Resolution", resolution, new[] { "256", "512", "1024" }, new[] { 256, 512, 1024 });

            EditorGUI.BeginChangeCheck();
            debugMode = (DebugMode)EditorGUILayout.EnumPopup("Debug View", debugMode);
            if (EditorGUI.EndChangeCheck()) needsDispatch = true;

            if (GUILayout.Button("Generate", GUILayout.Height(24))) needsDispatch = true;

            // --- Scrollable parameter area ---
            EditorGUILayout.Space(4);
            paramScroll = EditorGUILayout.BeginScrollView(paramScroll, GUILayout.ExpandHeight(true));

            EditorGUI.BeginChangeCheck();

            // Erosion Layer
            EditorGUI.BeginChangeCheck();
            EditorGUILayout.PropertyField(serializedObj.FindProperty("erosionLayer"));
            if (EditorGUI.EndChangeCheck()) needsDispatch = true;

            if (erosionLayer != null)
            {
                var layerSO = new SerializedObject(erosionLayer);
                layerSO.Update();

                EditorGUI.BeginChangeCheck();

                showErosion = EditorGUILayout.Foldout(showErosion, "Erosion Parameters", true, EditorStyles.foldoutHeader);
                if (showErosion)
                {
                    EditorGUI.indentLevel++;
                    DrawLayerField(layerSO, "erosionEnabled");
                    DrawLayerField(layerSO, "erosionScale");
                    DrawLayerField(layerSO, "erosionStrength");
                    DrawLayerField(layerSO, "erosionGullyWeight");
                    DrawLayerField(layerSO, "erosionDetail");
                    DrawLayerField(layerSO, "erosionRounding");
                    DrawLayerField(layerSO, "erosionOnset");
                    DrawLayerField(layerSO, "erosionAssumedSlope");
                    DrawLayerField(layerSO, "erosionCellScale");
                    DrawLayerField(layerSO, "erosionNormalization");
                    DrawLayerField(layerSO, "erosionOctaves");
                    DrawLayerField(layerSO, "erosionLacunarity");
                    DrawLayerField(layerSO, "erosionGain");
                    EditorGUI.indentLevel--;
                }

                showHeightOffset = EditorGUILayout.Foldout(showHeightOffset, "Height Offset", true, EditorStyles.foldoutHeader);
                if (showHeightOffset)
                {
                    EditorGUI.indentLevel++;
                    DrawLayerField(layerSO, "terrainHeightOffset");
                    EditorGUI.indentLevel--;
                }

                showTerrainConstants = EditorGUILayout.Foldout(showTerrainConstants, "Terrain Constants", true, EditorStyles.foldoutHeader);
                if (showTerrainConstants)
                {
                    EditorGUI.indentLevel++;
                    DrawLayerField(layerSO, "defaultHeight");
                    DrawLayerField(layerSO, "grassHeight");
                    DrawLayerField(layerSO, "waterHeight");
                    EditorGUI.indentLevel--;
                }

                if (layerSO.ApplyModifiedProperties())
                    EditorUtility.SetDirty(erosionLayer);
            }

            showHeightmapGen = EditorGUILayout.Foldout(showHeightmapGen, "Heightmap Generation", true, EditorStyles.foldoutHeader);
            if (showHeightmapGen)
            {
                EditorGUI.indentLevel++;
                DrawField("heightFrequency");
                DrawField("heightOctaves");
                DrawField("heightLacunarity");
                DrawField("heightGain");
                DrawField("heightAmp");
                DrawField("heightFunctionScale");
                DrawField("heightScale");
                DrawField("fadeTargetDivisor");
                EditorGUI.indentLevel--;
            }

            if (EditorGUI.EndChangeCheck()) needsDispatch = true;

            EditorGUILayout.EndScrollView();

            // --- Scene Preview ---
            EditorGUILayout.Space(4);
            EditorGUI.BeginChangeCheck();
            showScenePreview = EditorGUILayout.Foldout(showScenePreview, "Scene Preview", true, EditorStyles.foldoutHeader);
            if (showScenePreview)
            {
                EditorGUI.indentLevel++;
                previewHeightScale = EditorGUILayout.FloatField("Height Scale", previewHeightScale);
                previewMeshResolution = EditorGUILayout.Slider("Mesh Density", previewMeshResolution, 32, 512);
                if (GUILayout.Button("Create / Update Preview"))
                    UpdateScenePreview();
                if (previewGO != null && GUILayout.Button("Remove Preview"))
                    DestroyScenePreview();
                EditorGUI.indentLevel--;
            }
            if (EditorGUI.EndChangeCheck() && previewGO != null)
            {
                ApplyHeightsToPreviewMesh();
                needsDispatch = true;
            }

            // --- 2D Preview (fixed at bottom, capped height) ---
            if (outputTexture != null)
            {
                float maxPreviewHeight = 256f;
                float previewWidth = EditorGUIUtility.currentViewWidth - 30;
                float aspect = (float)outputTexture.width / outputTexture.height;
                float previewHeight = Mathf.Min(previewWidth / aspect, maxPreviewHeight);
                Rect rect = GUILayoutUtility.GetRect(previewWidth, previewHeight, GUILayout.MaxWidth(previewWidth), GUILayout.MaxHeight(previewHeight));
                EditorGUI.DrawPreviewTexture(rect, outputTexture);
            }

            // Dispatch after layout so preview rect is available next frame
            if (needsDispatch && computeShader != null)
            {
                DispatchCompute();
                needsDispatch = false;
            }

            serializedObj.ApplyModifiedProperties();
        }

        void DrawField(string propertyName)
        {
            SerializedProperty prop = serializedObj.FindProperty(propertyName);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }

        void DrawLayerField(SerializedObject layerSO, string propertyName)
        {
            SerializedProperty prop = layerSO.FindProperty(propertyName);
            if (prop != null) EditorGUILayout.PropertyField(prop, true);
        }

        void DispatchCompute()
        {
            if (erosionLayer == null) return;

            EnsureOutputTexture();

            int kernel = computeShader.FindKernel("CSErosionDebug");

            computeShader.SetFloat(ID_ErosionScale, erosionLayer.erosionScale);
            computeShader.SetFloat(ID_ErosionStrength, erosionLayer.erosionStrength);
            computeShader.SetFloat(ID_ErosionGullyWeight, erosionLayer.erosionGullyWeight);
            computeShader.SetFloat(ID_ErosionDetail, erosionLayer.erosionDetail);
            computeShader.SetVector(ID_ErosionRounding, erosionLayer.erosionRounding);
            computeShader.SetVector(ID_ErosionOnset, erosionLayer.erosionOnset);
            computeShader.SetVector(ID_ErosionAssumedSlope, (Vector4)erosionLayer.erosionAssumedSlope);
            computeShader.SetFloat(ID_ErosionCellScale, erosionLayer.erosionCellScale);
            computeShader.SetFloat(ID_ErosionNormalization, erosionLayer.erosionNormalization);
            computeShader.SetInt(ID_ErosionOctaves, erosionLayer.erosionOctaves);
            computeShader.SetFloat(ID_ErosionLacunarity, erosionLayer.erosionLacunarity);
            computeShader.SetFloat(ID_ErosionGain, erosionLayer.erosionGain);
            computeShader.SetVector(ID_TerrainHeightOffset, (Vector4)erosionLayer.terrainHeightOffset);
            computeShader.SetFloat(ID_ErosionEnabled, erosionLayer.erosionEnabled ? 1f : 0f);
            computeShader.SetFloat(ID_DefaultHeight, erosionLayer.defaultHeight);
            computeShader.SetFloat(ID_GrassHeight, erosionLayer.grassHeight);
            computeShader.SetFloat(ID_WaterHeight, erosionLayer.waterHeight);
            computeShader.SetFloat(ID_FadeTargetDivisor, fadeTargetDivisor);
            computeShader.SetInt(ID_DebugMode, (int)debugMode);

            if (heightSlopeMap != null)
                computeShader.SetTexture(kernel, ID_HeightSlopeMap, heightSlopeMap);
            else
                computeShader.SetTexture(kernel, ID_HeightSlopeMap, Texture2D.blackTexture);

            computeShader.SetTexture(kernel, ID_Output, outputTexture);

            int threadGroups = Mathf.CeilToInt(resolution / 8f);
            computeShader.Dispatch(kernel, threadGroups, threadGroups, 1);

            // Read back heights and apply to scene preview mesh
            ApplyHeightsToPreviewMesh();
        }

        void EnsureOutputTexture()
        {
            if (outputTexture == null || outputTexture.width != resolution || outputTexture.height != resolution)
            {
                if (outputTexture != null) outputTexture.Release();
                outputTexture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                outputTexture.Create();
            }
        }

        /// <summary>
        /// Generates the initial height+slope map using FractalNoise on the GPU.
        /// Dispatches the CSGenerateHeightmap kernel, reads back the result, and saves as an asset.
        /// </summary>
        Texture2D GenerateInitialHeightmap(int size)
        {
            if (computeShader == null) return null;

            EnsureOutputTexture();

            int kernel = computeShader.FindKernel("CSGenerateHeightmap");

            computeShader.SetFloat(ID_HeightFrequency, heightFrequency);
            computeShader.SetInt(ID_HeightOctaves, heightOctaves);
            computeShader.SetFloat(ID_HeightLacunarity, heightLacunarity);
            computeShader.SetFloat(ID_HeightGain, heightGain);
            computeShader.SetFloat(ID_HeightAmp, heightAmp);
            computeShader.SetFloat(ID_HeightFunctionScale, heightFunctionScale);
            computeShader.SetFloat(ID_HeightScale, heightScale);

            computeShader.SetTexture(kernel, ID_Output, outputTexture);

            int threadGroups = Mathf.CeilToInt(size / 8f);
            computeShader.Dispatch(kernel, threadGroups, threadGroups, 1);

            // Read back from GPU
            var tmp = RenderTexture.active;
            RenderTexture.active = outputTexture;
            var tex = new Texture2D(size, size, TextureFormat.RGBAFloat, false, true);
            tex.ReadPixels(new Rect(0, 0, size, size), 0, 0);
            tex.Apply();
            RenderTexture.active = tmp;

            tex.name = "InitialHeightSlopeMap";

            string folder = "Assets/Full World Generator/Runtime/Resources/Shader/Debug";
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/InitialHeightSlopeMap_{size}.asset");
            AssetDatabase.CreateAsset(tex, path);
            AssetDatabase.SaveAssets();
            return tex;
        }

        void UpdateScenePreview()
        {
            if (outputTexture == null) return;

            if (previewGO == null)
            {
                previewGO = GameObject.Find("__ErosionPreview__");
                if (previewGO == null)
                {
                    previewGO = new GameObject("__ErosionPreview__");
                    previewGO.hideFlags = HideFlags.DontSave;
                }
            }

            previewMeshFilter = previewGO.GetComponent<MeshFilter>();
            if (previewMeshFilter == null) previewMeshFilter = previewGO.AddComponent<MeshFilter>();

            var renderer = previewGO.GetComponent<MeshRenderer>();
            if (renderer == null)
            {
                renderer = previewGO.AddComponent<MeshRenderer>();
                renderer.sharedMaterial = new Material(Shader.Find("Standard")) { hideFlags = HideFlags.HideAndDontSave };
            }

            int res = Mathf.ClosestPowerOfTwo((int)previewMeshResolution);
            BuildPreviewMesh(res);
            previewMeshFilter.sharedMesh = previewMesh;

            ApplyHeightsToPreviewMesh();
        }

        void BuildPreviewMesh(int subdivisions)
        {
            int vCount = (subdivisions + 1) * (subdivisions + 1);
            var vertices = new Vector3[vCount];
            var uvs = new Vector2[vCount];
            var indices = new int[subdivisions * subdivisions * 6];

            float extent = resolution;
            float halfExtent = extent * 0.5f;
            float cellSize = extent / subdivisions;

            for (int y = 0; y <= subdivisions; y++)
            {
                for (int x = 0; x <= subdivisions; x++)
                {
                    int idx = y * (subdivisions + 1) + x;
                    float u = (float)x / subdivisions;
                    float v = (float)y / subdivisions;
                    vertices[idx] = new Vector3(-halfExtent + x * cellSize, 0f, -halfExtent + y * cellSize);
                    uvs[idx] = new Vector2(u, v);
                }
            }

            int tri = 0;
            for (int y = 0; y < subdivisions; y++)
            {
                for (int x = 0; x < subdivisions; x++)
                {
                    int i0 = y * (subdivisions + 1) + x;
                    int i1 = i0 + 1;
                    int i2 = i0 + (subdivisions + 1);
                    int i3 = i2 + 1;
                    indices[tri++] = i0; indices[tri++] = i2; indices[tri++] = i1;
                    indices[tri++] = i1; indices[tri++] = i2; indices[tri++] = i3;
                }
            }

            if (previewMesh != null) DestroyImmediate(previewMesh);
            previewMesh = new Mesh { hideFlags = HideFlags.HideAndDontSave };
            previewMesh.indexFormat = UnityEngine.Rendering.IndexFormat.UInt32;
            previewMesh.SetVertices(vertices);
            previewMesh.SetUVs(0, uvs);
            previewMesh.SetTriangles(indices, 0);
            previewMesh.RecalculateNormals();
        }

        /// <summary>
        /// Reads back the compute output RenderTexture and writes heights
        /// directly into the preview mesh vertex Y positions.
        /// </summary>
        void ApplyHeightsToPreviewMesh()
        {
            if (previewMesh == null || outputTexture == null) return;

            // Read back from GPU — linear texture to preserve float precision (no sRGB conversion)
            var tmp = RenderTexture.active;
            RenderTexture.active = outputTexture;
            int w = outputTexture.width, h = outputTexture.height;
            var readTex = new Texture2D(w, h, TextureFormat.RGBAFloat, false, true);
            readTex.ReadPixels(new Rect(0, 0, w, h), 0, 0);
            readTex.Apply();
            RenderTexture.active = tmp;
            heightReadback = readTex.GetPixels();
            DestroyImmediate(readTex);

            var vertices = previewMesh.vertices;
            var uvs = previewMesh.uv;

            float extent = resolution;
            float halfExtent = extent * 0.5f;
            int meshRes = (int)Mathf.Sqrt(vertices.Length) - 1;
            float cellSize = extent / meshRes;

            for (int i = 0; i < vertices.Length; i++)
            {
                float u = uvs[i].x;
                float v = uvs[i].y;

                // Bilinear sample height from readback
                float px = u * (w - 1);
                float py = v * (h - 1);
                int x0 = Mathf.FloorToInt(px), y0 = Mathf.FloorToInt(py);
                int x1 = Mathf.Min(x0 + 1, w - 1), y1 = Mathf.Min(y0 + 1, h - 1);
                float fx = px - x0, fy = py - y0;

                Color c00 = heightReadback[y0 * w + x0];
                Color c10 = heightReadback[y0 * w + x1];
                Color c01 = heightReadback[y1 * w + x0];
                Color c11 = heightReadback[y1 * w + x1];
                float height = Mathf.Lerp(Mathf.Lerp(c00.r, c10.r, fx), Mathf.Lerp(c01.r, c11.r, fx), fy);

                int xi = Mathf.RoundToInt(u * meshRes);
                int yi = Mathf.RoundToInt(v * meshRes);
                vertices[i] = new Vector3(-halfExtent + xi * cellSize, height * previewHeightScale, -halfExtent + yi * cellSize);
            }

            previewMesh.SetVertices(vertices);
            previewMesh.RecalculateNormals();
        }

        void DestroyScenePreview()
        {
            if (previewGO != null)
            {
                // Clean up material we created
                var renderer = previewGO.GetComponent<MeshRenderer>();
                if (renderer != null && renderer.sharedMaterial != null)
                    DestroyImmediate(renderer.sharedMaterial);
                DestroyImmediate(previewGO);
                previewGO = null;
            }
            if (previewMesh != null)
            {
                DestroyImmediate(previewMesh);
                previewMesh = null;
            }
        }

        void OnDestroy()
        {
            DestroyScenePreview();
            if (outputTexture != null)
            {
                outputTexture.Release();
                DestroyImmediate(outputTexture);
                outputTexture = null;
            }
        }
    }
}
