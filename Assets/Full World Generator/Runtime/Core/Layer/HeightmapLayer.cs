using UnityEngine;
using UnityEngine.Rendering;

namespace FullWorld
{
    internal class HeightmapLayer : BaseTerrianLayer
    {
        HeightmapLayerParam Param;
        ComputeShader shader;
        string shaderPath = "Shader/TerrainErosionCS";
        int kernelIndex;

        RenderTexture outputRT;

        static readonly int
            ID_HeightFrequency = Shader.PropertyToID("_HeightFrequency"),
            ID_HeightOctaves = Shader.PropertyToID("_HeightOctaves"),
            ID_HeightLacunarity = Shader.PropertyToID("_HeightLacunarity"),
            ID_HeightGain = Shader.PropertyToID("_HeightGain"),
            ID_HeightAmp = Shader.PropertyToID("_HeightAmp"),
            ID_HeightFunctionScale = Shader.PropertyToID("_HeightFunctionScale"),
            ID_HeightScale = Shader.PropertyToID("_HeightScale"),
            ID_FadeTargetDivisor = Shader.PropertyToID("_FadeTargetDivisor"),
            ID_TerrainHeightOffset = Shader.PropertyToID("_TerrainHeightOffset"),
            ID_DefaultHeight = Shader.PropertyToID("_DefaultHeight"),
            ID_GrassHeight = Shader.PropertyToID("_GrassHeight"),
            ID_WaterHeight = Shader.PropertyToID("_WaterHeight"),
            ID_HeightSlopeMap = Shader.PropertyToID("_HeightSlopeMap"),
            ID_Output = Shader.PropertyToID("_Output"),
            ID_Seed = Shader.PropertyToID("_Seed"),
            ID_Mask = Shader.PropertyToID("_Mask");

        public override void ExecuteInternal(TerriaContext context)
        {
            if (shader == null || Param == null) return;

            EnsureOutputTexture(context.resolution);

            var cmd = context.cmd;

            cmd.SetComputeFloatParam(shader, ID_HeightFrequency, Param.heightFrequency);
            cmd.SetComputeIntParam(shader, ID_HeightOctaves, Param.heightOctaves);
            cmd.SetComputeFloatParam(shader, ID_HeightLacunarity, Param.heightLacunarity);
            cmd.SetComputeFloatParam(shader, ID_HeightGain, Param.heightGain);
            cmd.SetComputeFloatParam(shader, ID_HeightAmp, Param.heightAmp);
            cmd.SetComputeFloatParam(shader, ID_HeightFunctionScale, Param.heightFunctionScale);
            cmd.SetComputeFloatParam(shader, ID_HeightScale, Param.heightScale);
            cmd.SetComputeFloatParam(shader, ID_FadeTargetDivisor, Param.fadeTargetDivisor);
            cmd.SetComputeVectorParam(shader, ID_TerrainHeightOffset, Param.terrainHeightOffset);
            cmd.SetComputeFloatParam(shader, ID_DefaultHeight, Param.defaultHeight);
            cmd.SetComputeFloatParam(shader, ID_GrassHeight, Param.grassHeight);
            cmd.SetComputeFloatParam(shader, ID_WaterHeight, Param.waterHeight);
            cmd.SetComputeVectorParam(shader, ID_Seed, context.seed);

            cmd.SetComputeTextureParam(shader, kernelIndex, ID_HeightSlopeMap, context.heightSlopeMap);
            cmd.SetComputeTextureParam(shader, kernelIndex, ID_Output, outputRT);
            cmd.SetComputeTextureParam(shader, kernelIndex, ID_Mask, mask);

            int threadGroups = Mathf.CeilToInt(context.resolution / 8f);
            cmd.DispatchCompute(shader, kernelIndex, threadGroups, threadGroups, 1);

            cmd.CopyTexture(outputRT, context.heightSlopeMap);
        }

        public override void OnDestroy(TerriaContext context)
        {
            if (outputRT != null)
            {
                outputRT.Release();
                outputRT = null;
            }
        }

        public override void OnSetup(TerriaContext context, BaseTerrianLayerParam param)
        {
            Param = param as HeightmapLayerParam;
            shader = Resources.Load<ComputeShader>(shaderPath);
            if (shader == null)
            {
                Debug.LogError(shaderPath + " is Miss.");
                return;
            }
            kernelIndex = shader.FindKernel("CSGenerateHeightmap");
        }

        void EnsureOutputTexture(int resolution)
        {
            if (outputRT != null && outputRT.width == resolution)
                return;

            if (outputRT != null)
                outputRT.Release();

            outputRT = new RenderTexture(resolution, resolution, 0, RenderTextureFormat.ARGBFloat)
            {
                enableRandomWrite = true,
                wrapMode = TextureWrapMode.Clamp,
            };
            outputRT.Create();
        }
    }
}
