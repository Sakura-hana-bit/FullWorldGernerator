using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace FullWorldEditor
{
    /// <summary>
    /// Custom PreviewSceneStage for FullWorldTerrain blueprint editing.
    /// Provides an isolated scene for terrain preview and tool interaction.
    /// </summary>
    public class FullWorldTerrainEditorStage : PreviewSceneStage
    {
        FullWorldTerrainEditor m_TerrainEditor;
        string m_AssetPath;
        public override string assetPath => m_AssetPath;

        protected override GUIContent CreateHeaderContent() =>
            new GUIContent("Full World Generator Editor",
                Resources.Load<Texture2D>("Icons/Full World Terrian Icon"));

        internal static FullWorldTerrainEditorStage CreateStage(
            string assetPath, FullWorldTerrainEditor terrainEditor)
        {
            var stage = CreateInstance<FullWorldTerrainEditorStage>();
            stage.Init(assetPath, terrainEditor);
            return stage;
        }

        private void Init(string assetPath, FullWorldTerrainEditor terrainEditor)
        {
            m_AssetPath = assetPath;
            m_TerrainEditor = terrainEditor;
        }

        protected override bool OnOpenStage()
        {
            base.OnOpenStage();
            if (!File.Exists(assetPath))
            {
                Debug.LogError(
                    $"FullWorldTerrainEditorStage: asset not found at {assetPath}");
                return false;
            }
            return true;
        }

        protected override void OnCloseStage()
        {
            // Cleanup is handled by FullWorldTerrainEditor.OnDisable()
            // to avoid double-cleanup and premature material destruction.
            base.OnCloseStage();
        }

        protected override void OnFirstTimeOpenStageInSceneView(SceneView sceneView)
        {
            sceneView.Frame(m_TerrainEditor.bounds);

            var sv = sceneView.sceneViewState;
            sv.showFlares = false;
            sv.alwaysRefresh = false;
            sv.showFog = false;
            sv.showSkybox = false;
            sv.showImageEffects = false;
            sv.showParticleSystems = false;
            sceneView.sceneLighting = true;
        }
    }
}
