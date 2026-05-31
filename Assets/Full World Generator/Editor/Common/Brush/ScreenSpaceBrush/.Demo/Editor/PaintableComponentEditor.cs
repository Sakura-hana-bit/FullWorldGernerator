#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;

namespace ScreenSpaceBrush
{
    [CustomEditor(typeof(PaintableComponent))]
    public class PaintableComponentEditor : Editor
    {
        private const int PreviewSize = 128;

        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            EditorGUILayout.Space(8);

            var comp = (PaintableComponent)target;

            if (GUILayout.Button("Initialize / Reset Paint Texture", GUILayout.Height(28)))
            {
                Undo.RecordObject(comp, "Initialize Paint Texture");
                comp.InitializeTexture();
                comp.EnsureMeshCollider();
                EditorUtility.SetDirty(comp);
            }

            if (GUILayout.Button("Ensure MeshCollider"))
            {
                Undo.RecordObject(comp, "Ensure MeshCollider");
                comp.EnsureMeshCollider();
            }

            if (comp.PaintTexture != null)
            {
                if (GUILayout.Button("Save Texture to Asset"))
                    comp.SaveTextureToAsset();
            }

            EditorGUILayout.Space(8);

            if (comp.PaintTexture != null)
            {
                EditorGUILayout.LabelField("Paint Texture Preview", EditorStyles.boldLabel);

                Rect previewRect = GUILayoutUtility.GetRect(PreviewSize, PreviewSize, GUILayout.ExpandWidth(false));
                previewRect.width = PreviewSize;
                previewRect.height = PreviewSize;
                previewRect.x = (EditorGUIUtility.currentViewWidth - PreviewSize) * 0.5f;

                EditorGUI.DrawPreviewTexture(previewRect, comp.PaintTexture);

                EditorGUILayout.LabelField(
                    $"{comp.PaintTexture.width}x{comp.PaintTexture.height} {comp.PaintTexture.format}",
                    EditorStyles.miniLabel);
            }
        }
    }
}
#endif
