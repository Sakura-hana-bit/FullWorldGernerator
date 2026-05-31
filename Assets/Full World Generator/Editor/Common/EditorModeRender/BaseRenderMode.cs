using UnityEngine;
using UnityEditor;
using System.Collections;

namespace FullWorldEditor
{
    public abstract class BaseRenderMode
    {
        protected FullWorldTerrainEditor editor;
        public abstract string name
        {
            get;
        }
        public BaseRenderMode(FullWorldTerrainEditor editor)
        {
            this.editor = editor;
        }

        public virtual void DrawWithCamera(Camera camera) { }
        public virtual void OnSceneRepaint(SceneView sceneView) { }
        public virtual void Refresh() { }

        public virtual void OnDestroy() { }
    }
}