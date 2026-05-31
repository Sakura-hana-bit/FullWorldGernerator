using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;

namespace FullWorld
{
    public struct TerriaContext
    {
        public CommandBuffer cmd;
        public RenderTexture heightSlopeMap;         // Working copy — layers modify this
        public RenderTexture heightSlopeMapOriginal; // Immutable base — never modified by layers
        public int resolution;
        public Vector2 seed;

        // User data for instrumentation / cross-layer communication
        private Dictionary<string, object> m_UserData;

        public void SetUserData(string key, object value)
        {
            if (m_UserData == null) m_UserData = new Dictionary<string, object>();
            m_UserData[key] = value;
        }

        public T GetUserData<T>(string key, T defaultValue = default)
        {
            if (m_UserData != null && m_UserData.TryGetValue(key, out var val) && val is T typed)
                return typed;
            return defaultValue;
        }

        public bool TryGetUserData<T>(string key, out T value)
        {
            value = default;
            if (m_UserData != null && m_UserData.TryGetValue(key, out var val) && val is T typed)
            {
                value = typed;
                return true;
            }
            return false;
        }

        public void RemoveUserData(string key)
        {
            if (m_UserData != null) m_UserData.Remove(key);
        }

        public void ClearUserData()
        {
            m_UserData?.Clear();
        }
    }


    public abstract class BaseTerrianLayer
    {
        protected const string m_LayerType = "Default";
        protected Texture mask = Texture2D.whiteTexture;
        /// <summary>
        /// Callback invoked after ExecuteInternal completes. Subscribers can use this for
        /// profiling, logging, or any post-layer instrumentation.
        /// </summary>
        public event Action<TerriaContext> OnPostExecute;

        public abstract void OnSetup(TerriaContext context, BaseTerrianLayerParam param);
        public abstract void OnDestroy(TerriaContext context);
        public abstract void ExecuteInternal(TerriaContext context);



        /// <summary>
        /// Backward-compatible overload without instrumentation context.
        /// </summary>
        public void Execute(TerriaContext context)
        {
            ExecuteInternal(context);

            OnPostExecute?.Invoke(context);
        }

                /// <summary>
        /// Backward-compatible overload without instrumentation context.
        /// </summary>
        public void Execute(TerriaContext context,Texture mask)
        {
            this.mask = mask;
            ExecuteInternal(context);

            OnPostExecute?.Invoke(context);
        }
    }
}
