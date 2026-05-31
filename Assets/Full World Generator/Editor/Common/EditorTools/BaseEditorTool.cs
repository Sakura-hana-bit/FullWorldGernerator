
using System.Collections;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace FullWorldEditor
{
    public abstract class BaseFullWorldEditorTool
    {
        protected FullWorldTerrainEditor editor;
        protected string m_Name;
        protected Texture m_Icon;

        public string name
        {
            get { return m_Name; }
        }

        public Texture icon
        {
            get
            {
                return m_Icon;
            }
        }

        public BaseFullWorldEditorTool(FullWorldTerrainEditor editor)
        {
            this.editor = editor;
        }

        public virtual void OnEnable() { }
        public virtual void OnDisable() {  }
        public virtual void OnDestroy() { }
        public virtual string GetHelpString() { return string.Empty; }

        public abstract void OnInspectorGUI();

        public virtual void OnSceneGUI(SceneView sceneView) { }

        public virtual bool Editable(int index) { return editor.visible[index]; }
    }
}