using System;
using System.Collections.Generic;
using UnityEngine;

namespace FullWorld
{
    public enum VegetationType
    {
        Tree,
        Bush
    }

    [Serializable]
    public struct VegetationInstance
    {
        public Vector3 position;
        public VegetationType type;
        public float height;
        public float radius;
        public float rotation;
        public float tiltX;
        public float tiltZ;

        public VegetationInstance(Vector3 pos, VegetationType t, float h, float r, float rot, float tiltX = 0f, float tiltZ = 0f)
        {
            position = pos;
            type = t;
            height = h;
            radius = r;
            rotation = rot;
            this.tiltX = tiltX;
            this.tiltZ = tiltZ;
        }
    }

    [Serializable]
    public struct VegetationParams
    {
        [Header("Density")]
        [Range(0.1f, 50f)] public float density;
        [Range(0f, 1f)]  public float bushRatio;

        [Header("Cluster")]
        [Range(2f, 50f)] public float clusterRadius;

        [Header("Placement")]
        [Range(0.3f, 10f)] public float minDistance;
        [Range(0.1f, 1f)]  public float maxSlope;
        public bool restrictToBiomeZone;

        [Header("Slope Tilt")]
        [Range(0f, 1f)]  public float slopeTiltStrength;
        [Range(0f, 1f)]  public float slopeTiltRandomness;

        [Header("Tree Dimensions (meters)")]
        public Vector2 treeHeightRange;
        public Vector2 treeRadiusRange;

        [Header("Bush Dimensions (meters)")]
        public Vector2 bushHeightRange;
        public Vector2 bushRadiusRange;

        [Header("Scale")]
        [Range(0.01f, 10f)] public float vegetationScale;

        [Header("Seed")]
        public int vegetationSeed;

        public static VegetationParams Default => new VegetationParams
        {
            density = 10f,
            bushRatio = 0.4f,
            clusterRadius = 8f,
            minDistance = 1.5f,
            maxSlope = 0.5f,
            restrictToBiomeZone = true,
            slopeTiltStrength = 0.5f,
            slopeTiltRandomness = 0.3f,
            treeHeightRange = new Vector2(5f, 15f),
            treeRadiusRange = new Vector2(2f, 5f),
            bushHeightRange = new Vector2(0.5f, 2f),
            bushRadiusRange = new Vector2(0.5f, 1.5f),
            vegetationScale = 1f,
            vegetationSeed = 0,
        };
    }
}
