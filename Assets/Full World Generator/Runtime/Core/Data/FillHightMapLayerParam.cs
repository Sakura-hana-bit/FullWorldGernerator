using UnityEngine;

namespace FullWorld
{
    [CreateAssetMenu(fileName = "FillHightMapLayer", menuName = "Full World Gernerator/FillHightMap Layer", order = 126)]
    public class FillHightMapLayerParam : BaseTerrianLayerParam
    {
        public enum SrcTextureType
        {
            Hight,
            HightWithGrad
        }

        public enum BlendMode
        {
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

        protected new const string m_LayerType = "FillHightMapLayer";

        [Header("Source"), SerializeField]
        internal Texture m_HightMap;
        [SerializeField]
        internal SrcTextureType m_TextureType;

        [Header("Blend"), SerializeField]
        public BlendMode blendMode = BlendMode.Normal;
        [SerializeField, Range(0f, 1f)]
        public float opacity = 1.0f;
        [SerializeField]
        public bool invert = false;

        [Header("Height Remap"), SerializeField]
        public float heightRemapMin = 0f;
        [SerializeField]
        public float heightRemapMax = 1f;
        [SerializeField]
        public float heightOffset = 0f;

        internal override BaseTerrianLayer CreateLayer()
        {
            return new FillHightMapLayer();
        }
    }
}
