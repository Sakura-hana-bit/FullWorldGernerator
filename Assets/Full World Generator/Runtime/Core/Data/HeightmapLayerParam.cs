using UnityEngine;

namespace FullWorld
{
    [CreateAssetMenu(fileName = "HeightmapLayer", menuName = "Full World Gernerator/Heightmap Layer", order = 124)]
    public class HeightmapLayerParam : BaseTerrianLayerParam
    {
        protected new const string m_LayerType = "Heightmap";

        [Header("Heightmap Generation"), SerializeField]
        public float heightFrequency = 1.0f;
        [SerializeField,Range(1,6)]
        public int heightOctaves = 5;
        [SerializeField]
        public float heightLacunarity = 2.0f;
        [SerializeField]
        public float heightGain = 0.5f;
        [SerializeField]
        public float heightAmp = 1.0f;
        [SerializeField]
        public float heightFunctionScale = 1.0f;
        [SerializeField]
        public float heightScale = 1.0f;
        [SerializeField]
        public float fadeTargetDivisor = 1.0f;

        [Header("Height Offset"), SerializeField]
        public Vector2 terrainHeightOffset = new Vector2(0f, 0f);

        [Header("Terrain Constants"), SerializeField]
        public float defaultHeight = 0f;
        [SerializeField]
        public float grassHeight = 0f;
        [SerializeField]
        public float waterHeight = 0f;

        internal override BaseTerrianLayer CreateLayer()
        {
            return new HeightmapLayer();
        }
    }
}
