
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;
namespace FullWorldEditor
{
    public class TerrianMeshRender : BaseRenderMode
    {
        public override string name
        {
            get { return "Particles"; }
        }

        private Shader shader;
        private Material material;
        private MaterialPropertyBlock mpb;


        public TerrianMeshRender(FullWorldTerrainEditor editor) : base(editor)
        {

            mpb = new MaterialPropertyBlock();
        }

        void CreateMaterialIfNeeded()
        {
            if (shader == null)
            {
                shader = Shader.Find("FullWorldShader/M_TerrianDebug");
                if (shader != null)
                {
                    if (!shader.isSupported)
                        Debug.LogWarning("Particle rendering shader not suported.");

                    if (material == null || material.shader != shader)
                    {
                        GameObject.DestroyImmediate(material);
                        material = new Material(shader);
                        material.hideFlags = HideFlags.HideAndDontSave;
                    }
                }
            }
        }

        public override void DrawWithCamera(Camera camera)
        {
            //CreateMaterialIfNeeded();
            //mpb.Clear();
            //Graphics.DrawMesh(editor.m_PerviewMesh, Matrix4x4.identity, material, 0, camera, 0, mpb);
        }

        public override void OnSceneRepaint(SceneView sceneView)
        {
           // editor.DrawGradientMesh();
        }

        public override void Refresh()
        {
        }

        public override void OnDestroy()
        {
            GameObject.DestroyImmediate(material);

        }
    }
}