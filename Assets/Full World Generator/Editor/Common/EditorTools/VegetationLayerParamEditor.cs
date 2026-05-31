using FullWorld;
using UnityEditor;
using UnityEngine;

namespace FullWorldEditor
{
    [CustomEditor(typeof(DefaultVegetationLayerParam))]
    public class DefaultVegetationLayerParamEditor : Editor
    {
        private SerializedProperty m_Parameters;

        private void OnEnable()
        {
            m_Parameters = serializedObject.FindProperty("m_Parameters");
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            if (m_Parameters == null) return;

            // ── Density ──
            EditorGUILayout.LabelField("Density", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            PropertySlider("Density (per 1000m²)", "density");
            PropertySlider("Bush Ratio", "bushRatio");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Cluster ──
            EditorGUILayout.LabelField("Cluster", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            PropertySlider("Cluster Radius", "clusterRadius");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Placement ──
            EditorGUILayout.LabelField("Placement", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;

            var biomeToggle = m_Parameters.FindPropertyRelative("restrictToBiomeZone");
            if (biomeToggle != null)
            {
                EditorGUI.BeginChangeCheck();
                EditorGUILayout.PropertyField(biomeToggle, new GUIContent("Restrict to Biome Zone"));
                if (EditorGUI.EndChangeCheck())
                    serializedObject.ApplyModifiedProperties();

                if (biomeToggle.boolValue)
                    EditorGUILayout.HelpBox("Vegetation only placed within the Vegetation biome height band (Sand End → Vegetation End).", MessageType.Info);
                else
                    EditorGUILayout.HelpBox("Vegetation can be placed anywhere that slope allows, regardless of biome height.", MessageType.Info);
            }

            PropertySlider("Min Distance", "minDistance");
            PropertySlider("Max Slope", "maxSlope");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Slope Tilt ──
            EditorGUILayout.LabelField("Slope Tilt", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            PropertySlider("Tilt Strength", "slopeTiltStrength");
            PropertySlider("Tilt Randomness", "slopeTiltRandomness");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Tree Dimensions ──
            EditorGUILayout.LabelField("Tree Dimensions (meters)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            MinMaxField("Height", "treeHeightRange");
            MinMaxField("Radius", "treeRadiusRange");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Bush Dimensions ──
            EditorGUILayout.LabelField("Bush Dimensions (meters)", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            MinMaxField("Height", "bushHeightRange");
            MinMaxField("Radius", "bushRadiusRange");
            EditorGUI.indentLevel--;
            EditorGUILayout.Space(4);

            // ── Scale & Seed ──
            EditorGUILayout.LabelField("Scale & Seed", EditorStyles.boldLabel);
            EditorGUI.indentLevel++;
            PropertySlider("Vegetation Scale", "vegetationScale");
            EditorGUILayout.PropertyField(m_Parameters.FindPropertyRelative("vegetationSeed"), new GUIContent("Vegetation Seed"));
            EditorGUI.indentLevel--;

            serializedObject.ApplyModifiedProperties();
        }

        // ── Helper Methods ──

        private void PropertySlider(string label, string fieldName)
        {
            var prop = m_Parameters.FindPropertyRelative(fieldName);
            if (prop != null)
                EditorGUILayout.PropertyField(prop, new GUIContent(label));
        }

        private void MinMaxField(string label, string fieldName)
        {
            var prop = m_Parameters.FindPropertyRelative(fieldName);
            if (prop == null) return;

            float minVal = prop.vector2Value.x;
            float maxVal = prop.vector2Value.y;

            float labelW = EditorGUIUtility.labelWidth;
            float fieldW = (EditorGUIUtility.currentViewWidth - labelW - 30f) * 0.5f;

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.PrefixLabel(label);

            EditorGUI.BeginChangeCheck();
            minVal = EditorGUILayout.FloatField(minVal, GUILayout.Width(fieldW));
            GUILayout.Label("→", EditorStyles.miniLabel, GUILayout.Width(14f));
            maxVal = EditorGUILayout.FloatField(maxVal, GUILayout.Width(fieldW));
            if (EditorGUI.EndChangeCheck())
            {
                maxVal = Mathf.Max(maxVal, minVal);
                prop.vector2Value = new Vector2(minVal, maxVal);
            }

            EditorGUILayout.EndHorizontal();
        }
    }
}
