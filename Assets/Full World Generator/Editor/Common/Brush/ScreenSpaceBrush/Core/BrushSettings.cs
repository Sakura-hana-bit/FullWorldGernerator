using UnityEngine;

namespace ScreenSpaceBrush
{
    /// <summary>
    /// Serializable brush parameters. Shared between the brush engine and any tool that drives it.
    /// This is a pure data container with no editor dependencies.
    /// </summary>
    [System.Serializable]
    public class BrushSettings
    {
        public enum PaintMode
        {
            Modify,
            Erase,
            Smooth
        }

        [Header("Brush Shape")]
        [Range(0.01f, 2f)]
        [Tooltip("Brush radius in UV space (0-1).")]
        public float radius = 0.05f;

        [Range(0f, 1f)]
        [Tooltip("Brush hardness. 1 = hard edge, 0 = fully feathered.")]
        public float hardness = 0.5f;

        [Header("Brush Strength")]
        [Range(0.001f, 1f)]
        [Tooltip("Paint strength per stamp. Lower = subtler blend, Color alpha controls transparency.")]
        public float opacity = 0.5f;

        [Header("Mode")]
        public PaintMode mode = PaintMode.Modify;

        [Header("Color (Modify mode)")]
        public Color color = Color.white;

        /// <summary>
        /// Compute the brush weight at a given normalized distance (0 = center, 1 = edge).
        /// Takes hardness/feathering into account.
        /// </summary>
        public float WeightAtNormalizedDistance(float normalizedDist)
        {
            if (normalizedDist >= 1f) return 0f;
            if (hardness >= 1f) return 1f;

            float featherStart = hardness;
            if (normalizedDist <= featherStart) return 1f;

            float t = (normalizedDist - featherStart) / (1f - featherStart);
            return 1f - t;
        }
    }
}
