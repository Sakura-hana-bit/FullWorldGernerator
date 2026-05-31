using UnityEngine;
using UnityEngine.Rendering;

namespace FullWorld
{
    internal class PaintHightMapLayer : BaseTerrianLayer
    {
        PaintHightMapLayerParam Param;
        ComputeShader shader;
        string shaderPath = "Shader/PaintHeightmapCS";
        int kernelIndex;

        RenderTexture outputRT;

        static readonly int
            ID_BlendMode = Shader.PropertyToID("_BlendMode"),
            ID_Opacity = Shader.PropertyToID("_Opacity"),
            ID_TargetHeight = Shader.PropertyToID("_TargetHeight"),
            ID_Invert = Shader.PropertyToID("_Invert"),
            ID_TextureResolution = Shader.PropertyToID("_TextureResolution"),
            ID_MaskResolution = Shader.PropertyToID("_MaskResolution"),
            ID_HeightSlopeMap = Shader.PropertyToID("_HeightSlopeMap"),
            ID_Output = Shader.PropertyToID("_Output"),
            ID_Mask = Shader.PropertyToID("_Mask");

        public override void ExecuteInternal(TerriaContext context)
        {
            if (shader == null || Param == null) return;

            EnsureOutputTexture(context.resolution);

            var cmd = context.cmd;

            cmd.SetComputeIntParam(shader, ID_BlendMode, (int)Param.blendMode);
            cmd.SetComputeFloatParam(shader, ID_Opacity, Param.opacity);
            cmd.SetComputeFloatParam(shader, ID_TargetHeight, Param.targetHeight);
            cmd.SetComputeFloatParam(shader, ID_Invert, Param.invert ? 1f : 0f);
            cmd.SetComputeFloatParam(shader, ID_TextureResolution, context.resolution);

            // Pass mask resolution so the compute shader can compute the mask
            // gradient with the correct texel size and scaling factor.
            int maskRes = mask != null ? mask.width : context.resolution;
            cmd.SetComputeFloatParam(shader, ID_MaskResolution, (float)maskRes);

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
            Param = param as PaintHightMapLayerParam;
            shader = Resources.Load<ComputeShader>(shaderPath);
            if (shader == null)
            {
                Debug.LogError(shaderPath + " is Miss.");
                return;
            }
            kernelIndex = shader.FindKernel("CSPaintHeightmap");
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
