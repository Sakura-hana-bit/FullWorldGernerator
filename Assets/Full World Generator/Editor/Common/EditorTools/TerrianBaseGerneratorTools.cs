
using System.Collections;
using System.Collections.Generic;
using FullWorld;
using UnityEditor;
using UnityEngine;

namespace FullWorldEditor
{
    public class TerrianBaseGerneratorTools : BaseFullWorldEditorTool
    {
        #region State
        const string kComputeShaderPath = "Assets/Full World Generator/Runtime/Core/Shader/TerrainErosionDebug.compute";
        ComputeShader computeShader;
        RenderTexture outputTexture { get { return editor.outputTexture; } set { editor.outputTexture = value; } }
        Texture2D generatedHeightmap { get { return editor.GeneratedHeightmap; } set { editor.GeneratedHeightmap = value; } }
        int resolution = 512;
        bool needsGenerate;

        // Heightmap generation parameters
        float heightFrequency = 2.0f;
        int heightOctaves = 8;
        float heightLacunarity = 2.0f;
        float heightGain = 0.5f;
        float heightAmp = 0.3f;
        float heightFunctionScale = 1.0f;
        float heightScale = 1.0f;

        // Auto generate
        bool autoGenerate;

        // Confirm before overwrite
        bool confirmOverwrite = true;

        // Foldout states
        bool showNoiseParams = true;

        static readonly int
            ID_HeightFrequency = Shader.PropertyToID("_HeightFrequency"),
            ID_HeightOctaves = Shader.PropertyToID("_HeightOctaves"),
            ID_HeightLacunarity = Shader.PropertyToID("_HeightLacunarity"),
            ID_HeightGain = Shader.PropertyToID("_HeightGain"),
            ID_HeightAmp = Shader.PropertyToID("_HeightAmp"),
            ID_HeightFunctionScale = Shader.PropertyToID("_HeightFunctionScale"),
            ID_HeightScale = Shader.PropertyToID("_HeightScale"),
            ID_Output = Shader.PropertyToID("_Output");

        #endregion

        public TerrianBaseGerneratorTools(FullWorldTerrainEditor editor) : base(editor)
        {
            m_Icon = Resources.Load<Texture2D>("GenerateTerrianIcon");
            m_Name = "Base Terrain Generator";
            if (computeShader == null)
                computeShader = AssetDatabase.LoadAssetAtPath<ComputeShader>(kComputeShaderPath);

            //generatedHeightmap = editor.m_GeneratedHeightmap;
        }

        public override string GetHelpString()
        {
            if (generatedHeightmap != null)
                return $"Heightmap generated ({generatedHeightmap.width}x{generatedHeightmap.height}). Use the scene preview to visualize the terrain.";
            return "Configure noise parameters and generate a base terrain heightmap.";
        }

        public override void OnInspectorGUI()
        {
            // --- Resources ---
            EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);
            EditorGUILayout.LabelField("Resources", EditorStyles.boldLabel);
            EditorGUI.BeginChangeCheck();

            GUI.enabled = false;
            computeShader = (ComputeShader)EditorGUILayout.ObjectField("Compute Shader", computeShader, typeof(ComputeShader), false);
            GUI.enabled = true;

            if (EditorGUI.EndChangeCheck())
                needsGenerate = true;

            resolution = EditorGUILayout.IntPopup("Resolution", resolution,
                new[] { "256", "512", "1024" }, new[] { 256, 512, 1024 });
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();
            GUILayout.Box(GUIContent.none, FullWorldEditorUtils.GetSeparatorLineStyle());

            // --- Noise Parameters ---
            EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);
            showNoiseParams = EditorGUILayout.Foldout(showNoiseParams, "Noise Parameters", true, EditorStyles.foldoutHeader);
            if (showNoiseParams)
            {
                EditorGUI.indentLevel++;

                EditorGUI.BeginChangeCheck();
                heightFrequency = EditorGUILayout.FloatField("Frequency", heightFrequency);
                heightOctaves = EditorGUILayout.IntField("Octaves", heightOctaves);
                heightLacunarity = EditorGUILayout.FloatField("Lacunarity", heightLacunarity);
                heightGain = EditorGUILayout.FloatField("Gain", heightGain);
                heightAmp = EditorGUILayout.FloatField("Amplitude", heightAmp);
                heightFunctionScale = EditorGUILayout.FloatField("Function Scale", heightFunctionScale);
                heightScale = EditorGUILayout.FloatField("UV Scale", heightScale);
                if (EditorGUI.EndChangeCheck())
                    needsGenerate = true;

                EditorGUI.indentLevel--;
            }
            EditorGUILayout.EndVertical();

            EditorGUILayout.Space();
            GUILayout.Box(GUIContent.none, FullWorldEditorUtils.GetSeparatorLineStyle());

            // --- Generate ---
            EditorGUILayout.BeginVertical(EditorStyles.inspectorDefaultMargins);

            autoGenerate = EditorGUILayout.Toggle("Auto Generate", autoGenerate);
            confirmOverwrite = EditorGUILayout.Toggle("Confirm Overwrite", confirmOverwrite);

            EditorGUI.BeginDisabledGroup(computeShader == null || autoGenerate);
            if (GUILayout.Button("Generate Heightmap", GUILayout.Height(28)))
            {
                if (generatedHeightmap != null && confirmOverwrite)
                {
                    if (!EditorUtility.DisplayDialog("Overwrite Heightmap",
                        "A heightmap already exists. Do you want to regenerate and overwrite it?",
                        "Yes", "No"))
                    {
                        EditorGUI.EndDisabledGroup();
                        goto EndGenerate;
                    }
                }
                generatedHeightmap = GenerateHeightmap(resolution);
                needsGenerate = true;
            }
            EditorGUI.EndDisabledGroup();
        EndGenerate:

            if (computeShader == null)
                EditorGUILayout.HelpBox("Assign a Compute Shader (TerrainErosionDebug).", MessageType.Warning);

            if (generatedHeightmap != null)
            {
                EditorGUILayout.Space(4);
                EditorGUILayout.ObjectField("Generated Map", generatedHeightmap, typeof(Texture2D), false);
            }

            EditorGUILayout.EndVertical();

            // Dispatch generation if params changed
            if (needsGenerate && computeShader != null)
            {
                bool canGenerate = true;
                if (autoGenerate && generatedHeightmap != null && confirmOverwrite)
                {
                    canGenerate = EditorUtility.DisplayDialog("Overwrite Heightmap",
                        "A heightmap already exists. Parameters changed — regenerate?",
                        "Yes", "No");
                }

                if (autoGenerate&&canGenerate)
                {
                    if (generatedHeightmap == null)
                        generatedHeightmap = GenerateHeightmap(resolution);
                    else
                        DispatchGenerate();
                }
                needsGenerate = false;
            }
        }

        public override void OnDestroy()
        {
            ReleaseOutputTexture();
        }

        #region Generation

        void EnsureOutputTexture()
        {
            if (outputTexture == null || outputTexture.width != resolution || outputTexture.height != resolution)
            {
                ReleaseOutputTexture();
                outputTexture = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                outputTexture.Create();
            }
        }

        void ReleaseOutputTexture()
        {
            if (outputTexture != null)
            {
                outputTexture.Release();
                outputTexture = null;
            }
        }

        /// <summary>
        /// Generates the height+slope map on the GPU and saves as a Texture2D asset.
        /// </summary>
        Texture2D GenerateHeightmap(int size)
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

            tex.name = "BaseHeightSlopeMap";

            string folder = "Assets/Full World Generator/Runtime/Core/Shader";
            string path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/BaseHeightSlopeMap_{size}.asset");
            AssetDatabase.CreateAsset(tex, path);
            AssetDatabase.SaveAssets();
            return tex;
        }

        /// <summary>
        /// Re-dispatches the generate kernel to refresh the preview after param changes.
        /// Does not save a new asset — only updates the output RenderTexture.
        /// </summary>
        void DispatchGenerate()
        {
            if (computeShader == null) return;

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

            int threadGroups = Mathf.CeilToInt(resolution / 8f);
            computeShader.Dispatch(kernel, threadGroups, threadGroups, 1);


        }

        #endregion
    }
}
