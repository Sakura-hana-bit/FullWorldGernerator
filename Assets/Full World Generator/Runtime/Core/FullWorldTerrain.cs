using System;
using System.Collections.Generic;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Profiling;
using UnityEngine.Rendering;
using UnityEngine.UI;

namespace FullWorld
{
    /// <summary>
    /// Bundles a single terrain layer's configuration so that reordering,
    /// duplication, and serialization always keep param / enable / mask in sync.
    /// </summary>
    [Serializable]
    public struct TerrianLayerEntry
    {
        public BaseTerrianLayerParam param;
        public bool enable;
        public BaseMaskData mask;
    }

    /// <summary>
    /// Bundles a single vegetation layer's configuration: param, enable toggle, and optional mask.
    /// </summary>
    [Serializable]
    public struct VegetationLayerEntry
    {
        public BaseVegetationLayerParam param;
        public bool enable;
        public BaseMaskData mask;
    }

    /// <summary>
    /// Procedural terrain asset that manages GPU-based height/slope generation
    /// through a composable layer system. Each layer modifies the HeightSlopeMap
    /// (ARGBFloat: R=height, G=slopeX, B=slopeY, A=packed data).
    /// After all layers execute, a BiomeMap is generated via compute shader.
    /// </summary>
    [CreateAssetMenu(
        fileName = "skinned cloth blueprint",
        menuName = "Full World Gernerator/Procedural Terrain",
        order = 122)]
    public class FullWorldTerrain : ScriptableObject
    {
        public delegate void TerrainCallback(FullWorldTerrain blueprint);
        public event TerrainCallback OnTerrainGenerateEnd;
        public event TerrainCallback OnVegetationGenerateEnd;

        // ================================================================
        //  Biome Distribution (runtime, shared with editor & GPU)
        // ================================================================

        /// <summary>
        /// Height-based biome thresholds in normalized [0,1] space.
        /// Layering: Water → Sand → Vegetation → Rock → Snow.
        /// Steep slopes override to rock regardless of height zone.
        /// </summary>
        public struct BiomeDistribution
        {
            [Header("Water")]      [Range(0f, 0.5f)] public float waterLine;
            [Header("Sand")]       [Range(0f, 0.5f)] public float sandEnd;
            [Header("Vegetation")] [Range(0f, 1f)]   public float vegetationEnd;
            [Header("Rock")]       [Range(0f, 1f)]   public float rockEnd;
            [Header("Snow")]       [Range(0f, 1f)]   public float snowStart;
            [Header("Slope")]      [Range(0.1f, 0.8f)] public float rockSlopeStart;
                                   [Range(0.3f, 1f)]    public float rockSlopeEnd;
        }

        // ================================================================
        //  Serialized Data
        // ================================================================

        [SerializeField] private Bounds m_bounds;
        [SerializeField] private float4[] m_GeneratedHeightmap;
        [SerializeField] private int m_Resolution = 512;
        [SerializeField] private int m_TextureResolution = 512;
        [SerializeField] private int m_Seed = 0;
        [SerializeField] private float m_MeshSizeX = 512f;
        [SerializeField] private float m_MeshSizeZ = 512f;

        // HeightScale envelope — world-space heights (meters). ScaleMax = terrain's max height.
        // The UI slider shows [0,1] where 1.0 = ScaleMax.
        // Heights outside [ScaleMin, ScaleMax] receive quadratic decay pulling them toward the boundary.
        [SerializeField] private float m_HeightScaleMin = 0f;
        [SerializeField] private float m_HeightScaleMax = 50f;

        // Range envelope — absolute height bounds in world space (meters).
        // Final heights are guaranteed to stay within this range after quadratic decay.
        [SerializeField] private float m_HeightRangeMin = 0f;
        [SerializeField] private float m_HeightRangeMax = 50f;

        // Layer configuration
        [SerializeField] private TerrianLayerEntry[] m_Layers;

        // Biome parameters
        [SerializeField] private BiomeDistribution m_Biome = new BiomeDistribution
        {
            waterLine      = 0.20f,
            sandEnd        = 0.28f,
            vegetationEnd  = 0.60f,
            rockEnd        = 0.85f,
            snowStart      = 0.85f,
            rockSlopeStart = 0.30f,
            rockSlopeEnd   = 0.70f,
        };

        // Vegetation parameters
        [SerializeField] private VegetationParams m_Vegetation = VegetationParams.Default;

        // Vegetation layers
        [SerializeField] private VegetationLayerEntry[] m_VegetationLayers = new VegetationLayerEntry[0];

        // ================================================================
        //  Public Access
        // ================================================================

        public Bounds Bounds => m_bounds;
        public int Resolution => m_Resolution;
        public int TextureResolution
        {
            get => m_TextureResolution;
            set { m_TextureResolution = value; MarkDirty(); }
        }
        public int Seed
        {
            get => m_Seed;
            set { m_Seed = value; MarkDirty(); }
        }
        public float HeightScaleMin
        {
            get => m_HeightScaleMin;
            set { m_HeightScaleMin = value; MarkDirty(); }
        }
        public float HeightScaleMax
        {
            get => m_HeightScaleMax;
            set { m_HeightScaleMax = value; MarkDirty(); }
        }
        public float RangeMin
        {
            get => m_HeightRangeMin;
            set { m_HeightRangeMin = value; MarkDirty(); }
        }
        public float RangeMax
        {
            get => m_HeightRangeMax;
            set { m_HeightRangeMax = value; MarkDirty(); }
        }
        public float MeshSizeX
        {
            get => m_MeshSizeX;
            set { m_MeshSizeX = value; MarkDirty(); }
        }
        public float MeshSizeZ
        {
            get => m_MeshSizeZ;
            set { m_MeshSizeZ = value; MarkDirty(); }
        }
        public RenderTexture HeightSlopeMap => heightSlopeMap;
        public RenderTexture BiomeMap => biomeMap;
        public TerrianLayerEntry[] Layers => m_Layers;

        public BiomeDistribution Biome
        {
            get => m_Biome;
            set { m_Biome = value; MarkDirty(); }
        }

        public VegetationParams Vegetation
        {
            get => m_Vegetation;
            set { m_Vegetation = value; MarkDirty(); }
        }

        public VegetationLayerEntry[] VegetationLayers => m_VegetationLayers;

        public List<VegetationInstance> VegetationInstances => m_VegetationInstances;

        // ================================================================
        //  Dirty State
        // ================================================================

        [NonSerialized] private bool isDirty;
        public bool IsDirty => isDirty;
        public void MarkDirty() => isDirty = true;

        // ================================================================
        //  Runtime State
        // ================================================================

        [NonSerialized] private BaseTerrianLayer[] layers;
        [NonSerialized] private RenderTexture heightSlopeMapOriginal;
        [NonSerialized] private RenderTexture heightSlopeMap;
        [NonSerialized] private RenderTexture biomeMap;
        [NonSerialized] public bool EnableDebugerMode = false;

        // Vegetation instances (generated after BiomeMap)
        [NonSerialized] private List<VegetationInstance> m_VegetationInstances = new List<VegetationInstance>();

        // Debug cache for layer intermediate results
        [NonSerialized] private Dictionary<int, RenderTexture> m_DebugLayerCache =
            new Dictionary<int, RenderTexture>();
        [NonSerialized] private Action<TerriaContext>[] m_DebugCallbacks;

        // Compute shader for biome map generation
        [NonSerialized] private ComputeShader biomeMapCS;
        [NonSerialized] private int biomeMapKernelIndex;

        // Compute shader for height remap
        [NonSerialized] private ComputeShader remapHeightCS;
        [NonSerialized] private int remapKernelIndex;

        // Compute shader for height clamping (Scale + Range quadratic decay)
        [NonSerialized] private ComputeShader clampHeightCS;
        [NonSerialized] private int clampHeightKernelIndex;

        // Unity Terrain (runtime output)
        [NonSerialized] private Terrain m_Terrain;
        [NonSerialized] private TerrainData m_TerrainData;
        [NonSerialized] private Material m_TerrainMaterial;

        private const string kTerrainObjectName = "Terrain";

        // ================================================================
        //  Generation Workflow
        // ================================================================

        public void GenerateWorkflow()
        {
            isDirty = false;
            ReSize();

            using (var cmd = new CommandBuffer() { name = "TerrainGeneration" })
            {
                cmd.BeginSample("TerrainGeneration");

                var tempTex = EnsureHeightSlopeMap(cmd);

                var context = new TerriaContext
                {
                    cmd = cmd,
                    heightSlopeMap = heightSlopeMap,
                    heightSlopeMapOriginal = heightSlopeMapOriginal,
                    resolution = m_TextureResolution,
                    seed = SeedToOffset(m_Seed),
                };

                // Setup
                Profiler.BeginSample("TerrainLayer.Setup");
                cmd.BeginSample("TerrainLayer.Setup");
                AllTerrianLayerSetUp(context);
                cmd.EndSample("TerrainLayer.Setup");
                Profiler.EndSample();

                // Execute layers
                Profiler.BeginSample("TerrainLayer.Execute");
                cmd.BeginSample("TerrainLayer.Execute");
                AllTerrianLayerExecute(context);
                cmd.EndSample("TerrainLayer.Execute");
                Profiler.EndSample();

                // Clamp heights: quadratic decay outside HeightScale, hard clamp to Range
                cmd.BeginSample("ClampHeight");
                ClampHeightMap(context);
                cmd.EndSample("ClampHeight");

                // Generate biome map from clamped HeightSlopeMap
                context.cmd.BeginSample("BiomeMap");
                GenerateBiomeMap(context);
                context.cmd.EndSample("BiomeMap");

                // Submit GPU work (BiomeMap + Remap must complete before CPU readback)
                cmd.EndSample("TerrainGeneration");
                Graphics.ExecuteCommandBuffer(cmd);

                if (tempTex != null)
                    DestroyImmediate(tempTex);

                // Cleanup
                Profiler.BeginSample("TerrainLayer.OnDestroy");
                AllTerrianLayerOnDestroy(context);
                Profiler.EndSample();

                // Produce Unity Terrain (CPU readback of GPU-remapped heightmap)
                // GenerateTerrain();

                OnTerrainGenerateEnd?.Invoke(this);
            }
        }

        // ================================================================
        //  Biome Map Generation (GPU)
        // ================================================================

        /// <summary>
        /// Dispatches BiomeMapCS to classify each pixel of HeightSlopeMap
        /// into a biome color, writing the result to the BiomeMap texture.
        /// </summary>
        private void GenerateBiomeMap(TerriaContext context)
        {
            EnsureBiomeMapResources();

            if (biomeMapCS == null)
            {
                Debug.LogWarning("[FullWorldTerrain] BiomeMapCS not found, skipping biome generation.");
                return;
            }

            int kernel = biomeMapKernelIndex;
            int threadGroups = Mathf.CeilToInt(context.resolution / 8f);

            context.cmd.SetComputeTextureParam(biomeMapCS, kernel, "_HeightSlopeMap", context.heightSlopeMap);
            context.cmd.SetComputeTextureParam(biomeMapCS, kernel, "_BiomeMapOutput", biomeMap);

            // Biome distribution parameters
            context.cmd.SetComputeFloatParam(biomeMapCS, "_WaterLine", m_Biome.waterLine);
            context.cmd.SetComputeFloatParam(biomeMapCS, "_SandEnd", m_Biome.sandEnd);
            context.cmd.SetComputeFloatParam(biomeMapCS, "_VegetationEnd", m_Biome.vegetationEnd);
            context.cmd.SetComputeFloatParam(biomeMapCS, "_RockEnd", m_Biome.rockEnd);
            context.cmd.SetComputeFloatParam(biomeMapCS, "_SnowStart", m_Biome.snowStart);
            context.cmd.SetComputeFloatParam(biomeMapCS, "_RockSlopeStart", m_Biome.rockSlopeStart);
            context.cmd.SetComputeFloatParam(biomeMapCS, "_RockSlopeEnd", m_Biome.rockSlopeEnd);

            context.cmd.DispatchCompute(biomeMapCS, kernel, threadGroups, threadGroups, 1);
        }


        private void EnsureBiomeMapResources()
        {
            // Load compute shader
            if (biomeMapCS == null)
            {
                biomeMapCS = Resources.Load<ComputeShader>("Shader/BiomeMapCS");
                if (biomeMapCS != null)
                    biomeMapKernelIndex = biomeMapCS.FindKernel("CSBiomeMap");
            }

            // Create or resize biome map RenderTexture
            bool needRebuild = biomeMap == null || biomeMap.width != m_TextureResolution;
            if (needRebuild)
            {
                if (biomeMap != null) biomeMap.Release();
                biomeMap = new RenderTexture(
                    m_TextureResolution, m_TextureResolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                biomeMap.Create();
            }
        }

        // ================================================================
        //  Height Clamping (GPU — Scale quadratic decay + Range hard clamp)
        // ================================================================

        private void EnsureClampHeightResources()
        {
            if (clampHeightCS == null)
            {
                clampHeightCS = Resources.Load<ComputeShader>("Shader/ClampHeightCS");
                if (clampHeightCS != null)
                    clampHeightKernelIndex = clampHeightCS.FindKernel("CSClampHeight");
            }
        }

        /// <summary>
        /// Dispatches ClampHeightCS to pull heights outside [HeightScaleMin, HeightScaleMax]
        /// back toward the Scale boundary via quadratic decay, and hard-clamps the result
        /// to [HeightRangeMin, HeightRangeMax]. Uses heightSlopeMapOriginal as a temp
        /// swap buffer so the compute shader can read/write without aliasing.
        /// </summary>
        private void ClampHeightMap(TerriaContext context)
        {
            EnsureClampHeightResources();

            if (clampHeightCS == null)
            {
                Debug.LogWarning("[FullWorldTerrain] ClampHeightCS not found, skipping height clamping.");
                return;
            }

            int kernel = clampHeightKernelIndex;
            int threadGroups = Mathf.CeilToInt(context.resolution / 8f);

            // Swap: copy working map → Original (temp read buffer), then compute reads from
            // Original and writes clamped result back to the working map.
            context.cmd.CopyTexture(context.heightSlopeMap, context.heightSlopeMapOriginal);

            context.cmd.SetComputeTextureParam(clampHeightCS, kernel, "_HeightSlopeMapSrc", context.heightSlopeMapOriginal);
            context.cmd.SetComputeTextureParam(clampHeightCS, kernel, "_HeightSlopeMapOut", context.heightSlopeMap);

            // Scale and Range are world-space → normalize by HeightScaleMax for GPU
            float terrainH = Mathf.Max(m_HeightScaleMax, 1e-6f);
            context.cmd.SetComputeFloatParam(clampHeightCS, "_HeightScaleMin", m_HeightScaleMin / terrainH);
            context.cmd.SetComputeFloatParam(clampHeightCS, "_HeightScaleMax", m_HeightScaleMax / terrainH);
            context.cmd.SetComputeFloatParam(clampHeightCS, "_HeightRangeMin", m_HeightRangeMin / terrainH);
            context.cmd.SetComputeFloatParam(clampHeightCS, "_HeightRangeMax", m_HeightRangeMax / terrainH);

            context.cmd.DispatchCompute(clampHeightCS, kernel, threadGroups, threadGroups, 1);
        }

        // ================================================================
        //  Unity Terrain Production (Runtime)
        // ================================================================

        /// <summary>
        /// Reads back the HeightSlopeMap from GPU and writes the height data
        /// into a <see cref="TerrainData"/> (and creates/updates a Terrain
        /// component). Also sets the alphamap from BiomeMap for visual
        /// biome coloring. Called automatically after <see cref="GenerateWorkflow"/>
        /// completes.
        /// </summary>
        public void GenerateTerrain()
        {
            if (heightSlopeMap == null)
            {
                //Debug.LogWarning("[FullWorldTerrain] HeightSlopeMap not ready, skipping terrain generation.");
                //return;

                GenerateWorkflow();
            }

            int resolution = m_TextureResolution;

            // ---- GPU Readback: HeightSlopeMap ----
            var prevRT = RenderTexture.active;
            RenderTexture.active = heightSlopeMap;
            var heightTex = new Texture2D(resolution, resolution, TextureFormat.RGBAFloat, false, true);
            heightTex.ReadPixels(new Rect(0, 0, resolution, resolution), 0, 0);
            heightTex.Apply();
            var heightPixels = heightTex.GetPixels();
            RenderTexture.active = prevRT;

            // ---- Ensure Terrain Object ----
            EnsureTerrainObject();

            // ---- TerrainData setup ----
            int heightmapRes = resolution; // TerrainData requires power-of-two+1 internally, but we can use custom
            float extentX = m_bounds.size.sqrMagnitude > 1e-6f ? m_bounds.size.x : m_MeshSizeX;
            float extentZ = m_bounds.size.sqrMagnitude > 1e-6f ? m_bounds.size.z : m_MeshSizeZ;

            if (m_TerrainData == null)
            {
                m_TerrainData = new TerrainData();
                m_TerrainData.heightmapResolution = heightmapRes;
                m_TerrainData.size = new Vector3(extentX, m_HeightScaleMax, extentZ);
            }
            else
            {
                m_TerrainData.heightmapResolution = heightmapRes;
                m_TerrainData.size = new Vector3(extentX, m_HeightScaleMax, extentZ);
            }

            // ---- Heightmap: copy from heightPixels (already remapped by GenerateWorkflow) ----
            float[,] heights = new float[heightmapRes, heightmapRes];
            for (int y = 0; y < heightmapRes; y++)
            {
                for (int x = 0; x < heightmapRes; x++)
                {
                    int idx = y * resolution + x;
                    heights[y, x] = heightPixels[idx].r;
                }
            }
            m_TerrainData.SetHeights(0, 0, heights);

            // ---- Alphamap: biome coloring from BiomeMap ----
            if (biomeMap != null)
            {
                int alphaRes = Mathf.Min(heightmapRes, biomeMap.width);

                // Ensure splat prototypes exist before calling SetAlphamaps
                int splatCount = 4;
                var prototypes = m_TerrainData.terrainLayers;
                if (prototypes == null || prototypes.Length < splatCount)
                {
                    var newProtos = new TerrainLayer[splatCount];
                    for (int i = 0; i < splatCount; i++)
                    {
                        newProtos[i] = new TerrainLayer();
                        newProtos[i].diffuseTexture = null; // default white
                    }
                    m_TerrainData.terrainLayers = newProtos;
                }

                // Set alphamap resolution to match our data size
                m_TerrainData.alphamapResolution = alphaRes;

                // GPU readback of BiomeMap
                RenderTexture.active = biomeMap;
                var biomeTex = new Texture2D(alphaRes, alphaRes, TextureFormat.RGBAFloat, false, true);
                biomeTex.ReadPixels(new Rect(0, 0, alphaRes, alphaRes), 0, 0);
                biomeTex.Apply();
                var biomePixels = biomeTex.GetPixels();
                RenderTexture.active = prevRT;
                DestroyImmediate(biomeTex);

                float[,,] splat = new float[alphaRes, alphaRes, splatCount];
                for (int y = 0; y < alphaRes; y++)
                {
                    for (int x = 0; x < alphaRes; x++)
                    {
                        int idx = y * alphaRes + x;
                        Color c = biomePixels[idx];
                        float total = c.r + c.g + c.b + c.a + 1e-6f;
                        splat[y, x, 0] = c.r / total; // water
                        splat[y, x, 1] = c.g / total; // sand
                        splat[y, x, 2] = c.b / total; // vegetation
                        splat[y, x, 3] = c.a / total; // rock/snow
                    }
                }
                m_TerrainData.SetAlphamaps(0, 0, splat);
            }

            // ---- Assign to Terrain component ----
            m_Terrain.terrainData = m_TerrainData;
            m_Terrain.materialTemplate = m_TerrainMaterial;

            DestroyImmediate(heightTex);
        }

        private void EnsureTerrainObject()
        {
            if (m_Terrain != null) return;

            m_Terrain = GameObject.Find(kTerrainObjectName)?.GetComponent<Terrain>();
            if (m_Terrain != null) return;

            var go = new GameObject(kTerrainObjectName);
            m_Terrain = go.AddComponent<Terrain>();
            m_Terrain.materialTemplate = m_TerrainMaterial;
        }

        /// <summary>
        /// Assigns a custom material to the terrain.
        /// </summary>
        public void SetTerrainMaterial(Material mat)
        {
            m_TerrainMaterial = mat;
            if (m_Terrain != null)
                m_Terrain.materialTemplate = mat;
        }

        /// <summary>
        /// Destroys the terrain GameObject and releases TerrainData.
        /// </summary>
        public void DestroyTerrain()
        {
            if (m_Terrain != null)
            {
                if (Application.isPlaying)
                    Destroy(m_Terrain.gameObject);
                else
                    DestroyImmediate(m_Terrain.gameObject);
                m_Terrain = null;
            }

            if (m_TerrainData != null)
            {
                if (Application.isPlaying)
                    Destroy(m_TerrainData);
                else
                    DestroyImmediate(m_TerrainData);
                m_TerrainData = null;
            }

            if (m_TerrainMaterial != null)
            {
                DestroyImmediate(m_TerrainMaterial);
                m_TerrainMaterial = null;
            }
        }

        // ================================================================
        //  Vegetation Generation (CPU, clustered distribution)
        // ================================================================

        public void GenerateVegetation()
        {
            m_VegetationInstances.Clear();

            if (heightSlopeMap == null || biomeMap == null)
                return;

            float extentX = m_bounds.size.sqrMagnitude > 1e-6f ? m_bounds.size.x : m_MeshSizeX;
            float extentZ = m_bounds.size.sqrMagnitude > 1e-6f ? m_bounds.size.z : m_MeshSizeZ;
            float heightScale = m_HeightScaleMax;

            if (m_VegetationLayers != null && m_VegetationLayers.Length > 0)
            {
                for (int i = 0; i < m_VegetationLayers.Length; i++)
                {
                    var entry = m_VegetationLayers[i];
                    if (!entry.enable || entry.param == null) continue;

                    var layerParam = entry.param.GetParameters();

                    float[] maskData = null;
                    if (entry.mask != null)
                    {
                        entry.mask.SyncToCpu();
                        maskData = entry.mask.m_Mask;
                    }

                    var layerInstances = VegetationScatterer.Scatter(
                        heightSlopeMap,
                        biomeMap,
                        layerParam,
                        m_Biome,
                        heightScale,
                        extentX,
                        extentZ,
                        m_TextureResolution,
                        maskData);

                    m_VegetationInstances.AddRange(layerInstances);
                }
            }
            else
            {
                m_VegetationInstances = VegetationScatterer.Scatter(
                    heightSlopeMap,
                    biomeMap,
                    m_Vegetation,
                    m_Biome,
                    heightScale,
                    extentX,
                    extentZ,
                    m_TextureResolution);
            }

            OnVegetationGenerateEnd?.Invoke(this);
        }

        // ================================================================
        //  Layer Lifecycle
        // ================================================================

        internal void AllTerrianLayerSetUp(TerriaContext context) =>
            TerrianLayerSetUpAt(context, m_Layers.Length);

        internal void AllTerrianLayerExecute(TerriaContext context) =>
            TerrianLayerExecuteAt(context, m_Layers.Length);

        internal void AllTerrianLayerOnDestroy(TerriaContext context) =>
            TerrianLayerOnDestroyAt(context, m_Layers.Length);

        internal void TerrianLayerSetUpAt(TerriaContext context, int count)
        {
            for (int i = 0; i < count; i++)
            {
                if (m_Layers[i].param == null) continue;
                layers[i] = m_Layers[i].param.CreateLayer();
                if (m_Layers[i].enable)
                    layers[i].OnSetup(context, m_Layers[i].param);
            }
        }

        internal void TerrianLayerExecuteAt(TerriaContext context, int count)
        {
            // Setup debug caching when in editor debug mode
            if (EnableDebugerMode)
            {
                AllocateDebugCache(count);
                m_DebugCallbacks = new Action<TerriaContext>[count];
                for (int i = 0; i < count; i++)
                {
                    if (!m_Layers[i].enable || m_Layers[i].param == null) continue;
                    int capturedIndex = i;
                    m_DebugCallbacks[i] = (ctx) =>
                    {
                        ctx.cmd.CopyTexture(ctx.heightSlopeMap, m_DebugLayerCache[capturedIndex]);
                    };
                    layers[i].OnPostExecute += m_DebugCallbacks[i];
                }
            }
            else
            {
                ReleaseDebugCache();
            }

            // Execute all enabled layers
            for (int i = 0; i < count; i++)
                if (m_Layers[i].enable && layers[i] != null)
                    layers[i].Execute(context, m_Layers[i].mask == null ? Texture2D.whiteTexture : m_Layers[i].mask.PreviewRT);

            // Teardown debug callbacks
            if (EnableDebugerMode && m_DebugCallbacks != null)
            {
                for (int i = 0; i < m_DebugCallbacks.Length; i++)
                    if (m_DebugCallbacks[i] != null && layers[i] != null)
                        layers[i].OnPostExecute -= m_DebugCallbacks[i];
                m_DebugCallbacks = null;
            }
        }

        internal void TerrianLayerOnDestroyAt(TerriaContext context, int count)
        {
            for (int i = 0; i < count; i++)
                if (m_Layers[i].enable && layers[i] != null)
                {
                    layers[i].OnDestroy(context);
#if UNITY_EDITOR

#else
                    m_Layers[i].mask?.ReleaseRT();
#endif
                }
        }

        // ================================================================
        //  Debug Cache
        // ================================================================

        private void AllocateDebugCache(int layerCount)
        {
            ReleaseDebugCache();
            for (int i = 0; i < layerCount; i++)
            {
                if (!m_Layers[i].enable || m_Layers[i].param == null) continue;
                var rt = new RenderTexture(
                    m_TextureResolution, m_TextureResolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                rt.Create();
                m_DebugLayerCache[i] = rt;
            }
        }

        public void ReleaseDebugCache()
        {
            foreach (var kvp in m_DebugLayerCache)
                if (kvp.Value != null) kvp.Value.Release();
            m_DebugLayerCache.Clear();
        }

        public RenderTexture GetDebugLayerCache(int layerIndex)
        {
            m_DebugLayerCache.TryGetValue(layerIndex, out var rt);
            return rt;
        }



        // ================================================================
        //  Mask RenderTexture Cache
        // ================================================================

        public void ReleaseLayerMaskRT()
        {
            for (int i = 0; i < m_Layers.Length; i++)
                if (m_Layers[i].enable)
                    m_Layers[i].mask?.ReleaseRT();
        }

        // ================================================================
        //  Helpers
        // ================================================================

        internal void ReSize()
        {
            layers = new BaseTerrianLayer[m_Layers.Length];
        }

        /// <summary>
        /// Creates or resizes the HeightSlopeMap pair and uploads the base heightmap data.
        /// Returns a temporary Texture2D that the caller must destroy after GPU execution.
        /// </summary>
        Texture2D EnsureHeightSlopeMap(CommandBuffer cmd)
        {
            Texture2D tempTex = null;
            bool needRebuild = heightSlopeMap == null ||
                               heightSlopeMap.width != m_TextureResolution;

            if (needRebuild)
            {
                if (heightSlopeMap != null) heightSlopeMap.Release();
                if (heightSlopeMapOriginal != null) heightSlopeMapOriginal.Release();

                heightSlopeMapOriginal = new RenderTexture(
                    m_TextureResolution, m_TextureResolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                heightSlopeMapOriginal.Create();

                heightSlopeMap = new RenderTexture(
                    m_TextureResolution, m_TextureResolution, 0, RenderTextureFormat.ARGBFloat)
                {
                    enableRandomWrite = true,
                    wrapMode = TextureWrapMode.Clamp,
                };
                heightSlopeMap.Create();
            }

            //// Upload serialized heightmap → original (immutable base layer)
            //if (m_GeneratedHeightmap != null && m_GeneratedHeightmap.Length > 0)
            //{
            //    tempTex = new Texture2D(
            //        m_TextureResolution, m_TextureResolution,
            //        TextureFormat.RGBAFloat, false, true);
            //    var pixels = new Color[m_TextureResolution * m_TextureResolution];
            //    for (int i = 0; i < pixels.Length && i < m_GeneratedHeightmap.Length; i++)
            //    {
            //        var v = m_GeneratedHeightmap[i];
            //        pixels[i] = new Color(v.x, v.y, v.z, v.w);
            //    }
            //    tempTex.SetPixels(pixels);
            //    tempTex.Apply();
            //    cmd.CopyTexture(tempTex, heightSlopeMapOriginal);
            //}

            // Copy original → working copy (layers modify the working copy)
            cmd.Blit(Texture2D.blackTexture, heightSlopeMap);

            return tempTex;
        }

        /// <summary>
        /// Converts an integer seed to a well-distributed float2 offset in [0,1)
        /// for noise hash perturbation.
        /// </summary>
        internal static Vector2 SeedToOffset(int seed)
        {
            float s = seed;
            float x = Mathf.Sin(s * 127.1f) * 43758.5453f;
            float y = Mathf.Sin(s * 269.5f) * 43758.5453f;
            return new Vector2(x - Mathf.Floor(x), y - Mathf.Floor(y));
        }
    }
}
