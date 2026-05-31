using UnityEngine;
using UnityEngine.Rendering;

namespace FullWorld
{
    internal class ErosionLayer : BaseTerrianLayer
    {
        ErosionLayerParam Param;
        ComputeShader shader;
        string shaderPath = "Shader/TerrainErosionCS";
        int kernelIndex;

        RenderTexture outputRT;

        static readonly int
            ID_ErosionScale = Shader.PropertyToID("_ErosionScale"),
            ID_ErosionStrength = Shader.PropertyToID("_ErosionStrength"),
            ID_ErosionGullyWeight = Shader.PropertyToID("_ErosionGullyWeight"),
            ID_ErosionDetail = Shader.PropertyToID("_ErosionDetail"),
            ID_ErosionRounding = Shader.PropertyToID("_ErosionRounding"),
            ID_ErosionOnset = Shader.PropertyToID("_ErosionOnset"),
            ID_ErosionAssumedSlope = Shader.PropertyToID("_ErosionAssumedSlope"),
            ID_ErosionCellScale = Shader.PropertyToID("_ErosionCellScale"),
            ID_ErosionNormalization = Shader.PropertyToID("_ErosionNormalization"),
            ID_ErosionOctaves = Shader.PropertyToID("_ErosionOctaves"),
            ID_ErosionLacunarity = Shader.PropertyToID("_ErosionLacunarity"),
            ID_ErosionGain = Shader.PropertyToID("_ErosionGain"),
            ID_TerrainHeightOffset = Shader.PropertyToID("_TerrainHeightOffset"),
            ID_ErosionEnabled = Shader.PropertyToID("_ErosionEnabled"),
            ID_DefaultHeight = Shader.PropertyToID("_DefaultHeight"),
            ID_GrassHeight = Shader.PropertyToID("_GrassHeight"),
            ID_WaterHeight = Shader.PropertyToID("_WaterHeight"),
            ID_FadeTargetDivisor = Shader.PropertyToID("_FadeTargetDivisor"),
            ID_Seed = Shader.PropertyToID("_Seed"),
            ID_HeightSlopeMap = Shader.PropertyToID("_HeightSlopeMap"),
            ID_Output = Shader.PropertyToID("_Output"),
            ID_Mask = Shader.PropertyToID("_Mask");


        public override void ExecuteInternal(TerriaContext context)
        {
            if (shader == null || Param == null) return;

            EnsureOutputTexture(context.resolution);

            var cmd = context.cmd;

            // Set cbuffer params
            cmd.SetComputeFloatParam(shader, ID_ErosionScale, Param.erosionScale);
            cmd.SetComputeFloatParam(shader, ID_ErosionStrength, Param.erosionStrength);
            cmd.SetComputeFloatParam(shader, ID_ErosionGullyWeight, Param.erosionGullyWeight);
            cmd.SetComputeFloatParam(shader, ID_ErosionDetail, Param.erosionDetail);
            cmd.SetComputeVectorParam(shader, ID_ErosionRounding, Param.erosionRounding);
            cmd.SetComputeVectorParam(shader, ID_ErosionOnset, Param.erosionOnset);
            cmd.SetComputeVectorParam(shader, ID_ErosionAssumedSlope, Param.erosionAssumedSlope);
            cmd.SetComputeFloatParam(shader, ID_ErosionCellScale, Param.erosionCellScale);
            cmd.SetComputeFloatParam(shader, ID_ErosionNormalization, Param.erosionNormalization);
            cmd.SetComputeIntParam(shader, ID_ErosionOctaves, Param.erosionOctaves);
            cmd.SetComputeFloatParam(shader, ID_ErosionLacunarity, Param.erosionLacunarity);
            cmd.SetComputeFloatParam(shader, ID_ErosionGain, Param.erosionGain);
            cmd.SetComputeVectorParam(shader, ID_TerrainHeightOffset, Param.terrainHeightOffset);
            cmd.SetComputeFloatParam(shader, ID_ErosionEnabled, Param.erosionEnabled ? 1f : 0f);
            cmd.SetComputeFloatParam(shader, ID_DefaultHeight, Param.defaultHeight);
            cmd.SetComputeFloatParam(shader, ID_GrassHeight, Param.grassHeight);
            cmd.SetComputeFloatParam(shader, ID_WaterHeight, Param.waterHeight);
            cmd.SetComputeFloatParam(shader, ID_FadeTargetDivisor, Param.fadeTargetDivisor);
            cmd.SetComputeVectorParam(shader, ID_Seed, context.seed);

            // Bind textures
            cmd.SetComputeTextureParam(shader, kernelIndex, ID_Mask, mask);
            cmd.SetComputeTextureParam(shader, kernelIndex, ID_HeightSlopeMap, context.heightSlopeMap);
            cmd.SetComputeTextureParam(shader, kernelIndex, ID_Output, outputRT);

            // Dispatch
            int threadGroups = Mathf.CeilToInt(context.resolution / 8f);
            cmd.DispatchCompute(shader, kernelIndex, threadGroups, threadGroups, 1);

            // Copy eroded result back into context for next layer
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
            Param = param as ErosionLayerParam;
            shader = Resources.Load<ComputeShader>(shaderPath);
            if (shader == null)
            {
                Debug.LogError(shaderPath + " is Miss.");
                return;
            }
            kernelIndex = shader.FindKernel("CSErosion");
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
