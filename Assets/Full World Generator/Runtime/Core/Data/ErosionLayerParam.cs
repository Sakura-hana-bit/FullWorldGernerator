using UnityEngine;

namespace FullWorld
{
    [CreateAssetMenu(fileName = "ErosionLayer", menuName = "Full World Gernerator/Erosion Layer", order = 123)]
    public class ErosionLayerParam : BaseTerrianLayerParam
    {
        [Header("Erosion"), SerializeField]
        public float erosionScale = 1.5f;
        [SerializeField]
        public float erosionStrength = 0.22f;
        [SerializeField]
        public float erosionGullyWeight = 0.5f;
        [SerializeField]
        public float erosionDetail =1.5f;
        [SerializeField]
        public Vector4 erosionRounding = new Vector4(0.1f, 0f, 1.0f, 2.0f);
        [SerializeField]
        public Vector4 erosionOnset = new Vector4(1.25f, 1.25f, 2.8f, 1.5f);
        [SerializeField]
        public Vector2 erosionAssumedSlope = new Vector2(0.7f, 1.0f);
        [SerializeField]
        public float erosionCellScale = 0.7f;
        [SerializeField]
        public float erosionNormalization = 0.5f;
        [SerializeField]
        public int erosionOctaves = 5;
        [SerializeField]
        public float erosionLacunarity = 2.0f;
        [SerializeField]
        public float erosionGain = 0.5f;
        [SerializeField]
        public bool erosionEnabled = true;

        [Header("Height Offset"),SerializeField]
        public Vector2 terrainHeightOffset = new Vector2(0f, 0f);

        [Header("Fade Target"), SerializeField]
        public float fadeTargetDivisor = 1.0f;

        [Header("Terrain Constants"), SerializeField]
        public float defaultHeight = 0.45f;
        [SerializeField]
        public float grassHeight = 0.465f;
        [SerializeField]
        public float waterHeight = 0.46f;

        internal override BaseTerrianLayer CreateLayer()
        {
            return new ErosionLayer();
        }
    }
}
