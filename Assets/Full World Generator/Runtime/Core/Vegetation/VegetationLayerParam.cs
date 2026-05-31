using UnityEngine;

namespace FullWorld
{
    public abstract class BaseVegetationLayerParam : ScriptableObject
    {
        protected const string m_LayerType = "Default";
        public string GetLayerType { get { return m_LayerType; } }

        public abstract VegetationParams GetParameters();
    }

    [CreateAssetMenu(
        fileName = "NewVegetationLayerParam",
        menuName = "Full World Generator/Vegetation Layer Param")]
    public class DefaultVegetationLayerParam : BaseVegetationLayerParam
    {
        protected new const string m_LayerType = "Vegetation";

        [SerializeField] private VegetationParams m_Parameters = VegetationParams.Default;

        public override VegetationParams GetParameters() => m_Parameters;
    }
}
