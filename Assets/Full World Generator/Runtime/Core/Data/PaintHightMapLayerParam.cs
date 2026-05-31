using UnityEngine;

namespace FullWorld
{
    [CreateAssetMenu(fileName = "PaintHightMapLayer", menuName = "Full World Gernerator/PaintHightMap Layer", order = 127)]
    public class PaintHightMapLayerParam : BaseTerrianLayerParam
    {
        public enum BlendMode
        {
            Overwrite,
            Normal,
            Multiply,
            Screen,
            Overlay,
            SoftLight,
            HardLight,
            Add,
            Subtract,
            Difference,
            Darken,
            Lighten,
            ColorBurn,
            ColorDodge
        }

        protected new const string m_LayerType = "PaintHightMapLayer";

        [Header("Paint"), SerializeField, Range(-1f, 1f)]
        public float targetHeight = 0.5f;

        [Header("Blend"), SerializeField]
        public BlendMode blendMode = BlendMode.Normal;
        [SerializeField, Range(0f, 1f)]
        public float opacity = 1.0f;
        [SerializeField]
        public bool invert = false;

        internal override BaseTerrianLayer CreateLayer()
        {
            return new PaintHightMapLayer();
        }
    }
}
