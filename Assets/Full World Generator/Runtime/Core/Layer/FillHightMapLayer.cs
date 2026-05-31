using UnityEngine;
using UnityEngine.Rendering;

namespace FullWorld
{
    internal class FillHightMapLayer : BaseTerrianLayer
    {
        FillHightMapLayerParam Param;
        ComputeShader shader;
        string shaderPath = "Shader/FillHeightmapCS";
        int kernelIndex;

        RenderTexture outputRT;

        static readonly int
            ID_BlendMode = Shader.PropertyToID("_BlendMode"),
            ID_Opacity = Shader.PropertyToID("_Opacity"),
            ID_TextureType = Shader.PropertyToID("_TextureType"),
            ID_Invert = Shader.PropertyToID("_Invert"),
            ID_HeightRemapMin = Shader.PropertyToID("_HeightRemapMin"),
            ID_HeightRemapMax = Shader.PropertyToID("_HeightRemapMax"),
            ID_HeightOffset = Shader.PropertyToID("_HeightOffset"),
            ID_Seed = Shader.PropertyToID("_Seed"),
            ID_TextureResolution = Shader.PropertyToID("_TextureResolution"),
            ID_FillSourceMap = Shader.PropertyToID("_FillSourceMap"),
            ID_HeightSlopeMap = Shader.PropertyToID("_HeightSlopeMap"),
            ID_Output = Shader.PropertyToID("_Output"),
            ID_Mask = Shader.PropertyToID("_Mask");

        public override void ExecuteInternal(TerriaContext context)
        {
            if (shader == null || Param == null || Param.m_HightMap == null) return;

            EnsureOutputTexture(context.resolution);

            var cmd = context.cmd;

            cmd.SetComputeIntParam(shader, ID_BlendMode, (int)Param.blendMode);
            cmd.SetComputeFloatParam(shader, ID_Opacity, Param.opacity);
            cmd.SetComputeIntParam(shader, ID_TextureType, (int)Param.m_TextureType);
            cmd.SetComputeFloatParam(shader, ID_Invert, Param.invert ? 1f : 0f);
            cmd.SetComputeFloatParam(shader, ID_HeightRemapMin, Param.heightRemapMin);
            cmd.SetComputeFloatParam(shader, ID_HeightRemapMax, Param.heightRemapMax);
            cmd.SetComputeFloatParam(shader, ID_HeightOffset, Param.heightOffset);
            cmd.SetComputeVectorParam(shader, ID_Seed, context.seed);
            cmd.SetComputeFloatParam(shader, ID_TextureResolution, context.resolution);

            cmd.SetComputeTextureParam(shader, kernelIndex, ID_FillSourceMap, Param.m_HightMap);
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
            Param = param as FillHightMapLayerParam;
            shader = Resources.Load<ComputeShader>(shaderPath);
            if (shader == null)
            {
                Debug.LogError(shaderPath + " is Miss.");
                return;
            }
            kernelIndex = shader.FindKernel("CSFillHeightmap");
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
