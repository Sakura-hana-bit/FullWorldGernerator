
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace FullWorld
{
    public abstract class BaseTerrianLayerParam : ScriptableObject
    {
        protected const string m_LayerType = "Default";
        public string GetLayerType { get { return m_LayerType; } }

        internal abstract BaseTerrianLayer CreateLayer();
    }
}
