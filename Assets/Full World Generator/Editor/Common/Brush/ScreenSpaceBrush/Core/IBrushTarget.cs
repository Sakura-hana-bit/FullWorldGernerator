using UnityEngine;

namespace ScreenSpaceBrush
{
    /// <summary>
    /// Interface for any object that can receive CPU brush painting.
    /// Implement this to make your component paintable by the ScreenSpaceBrush tool.
    /// Examples: weight painting targets, terrain painting targets, texture painting targets, etc.
    /// </summary>
    public interface IBrushTarget
    {
        /// <summary>
        /// Get the texture that will be painted on.
        /// </summary>
        Texture2D GetPaintTexture();

        /// <summary>
        /// Called when a brush stroke begins on this target.
        /// </summary>
        void OnStrokeBegin();

        /// <summary>
        /// Called after each paint application (stamp or line segment).
        /// Use this to apply texture changes or update visualization.
        /// </summary>
        void OnPaintApplied();

        /// <summary>
        /// Called when a brush stroke ends on this target.
        /// Use this for auto-save, dirty marking, etc.
        /// </summary>
        void OnStrokeEnd();
    }

    /// <summary>
    /// Extended interface for targets that support GPU (Compute Shader) painting.
    /// Implement this alongside IBrushTarget to enable GPU-accelerated painting.
    /// The RenderTexture stays on the GPU during painting — call SyncGpuToCpu()
    /// to read pixels back to the CPU-side Texture2D (e.g. for saving).
    /// </summary>
    public interface IGpuBrushTarget : IBrushTarget
    {
        /// <summary>
        /// Get the RenderTexture that will be painted on via Compute Shader.
        /// Return null if the RenderTexture is not yet ready.
        /// </summary>
        RenderTexture GetPaintRenderTexture();

        /// <summary>
        /// Copy GPU RenderTexture pixels back to the CPU-side Texture2D.
        /// Called at stroke end so the Texture2D returned by GetPaintTexture() is up to date.
        /// </summary>
        void SyncGpuToCpu();
    }
}
