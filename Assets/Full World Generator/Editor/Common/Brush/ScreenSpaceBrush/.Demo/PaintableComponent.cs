using UnityEngine;

namespace ScreenSpaceBrush
{
    /// <summary>
    /// Standard IBrushTarget + IGpuBrushTarget implementation. Manages a UV paint texture on a mesh.
    /// Can be auto-added by SceneViewBrushTool when painting on a mesh without an IBrushTarget.
    /// Supports both CPU (Texture2D) and GPU (RenderTexture + Compute Shader) painting.
    ///
    /// To create a custom painting target (e.g. for weight painting, terrain, etc.),
    /// implement IBrushTarget (and optionally IGpuBrushTarget) on your own component.
    /// </summary>
    [RequireComponent(typeof(MeshRenderer), typeof(MeshFilter))]
    public class PaintableComponent : MonoBehaviour, IBrushTarget, IGpuBrushTarget
    {
        #region Enums

        public enum TextureResolution
        {
            _256 = 256,
            _512 = 512,
            _1024 = 1024,
            _2048 = 2048,
            _4096 = 4096
        }

        #endregion

        #region Serialized Fields

        [Header("Texture Settings")]
        [SerializeField] private TextureResolution resolution = TextureResolution._1024;
        [SerializeField] private Color fillColor = Color.black;

        [Header("Visualization")]
        [Tooltip("Shader property name to assign the paint texture to (e.g. _BaseColorMap for HDRP).")]
        [SerializeField] private string materialPropertyName = "_BaseColorMap";
        [Tooltip("If true, the paint texture is automatically assigned to the material for visualization.")]
        [SerializeField] private bool autoAssignToMaterial = true;

        [Header("Auto Save")]
        [Tooltip("If true, the paint texture is automatically saved as an asset after each stroke.")]
        [SerializeField] private bool autoSave = true;
        [Tooltip("Folder path under Assets/ where the texture asset will be saved.")]
        [SerializeField] private string saveFolder = "Assets/PaintTextures";

        [Header("Paint Texture")]
        [SerializeField, HideInInspector] private Texture2D paintTexture;
        [SerializeField, HideInInspector] private string savedAssetPath;

        #endregion

        #region Runtime State

        private MeshCollider _meshCollider;
        private Texture2D _originalTexture;
        private RenderTexture _renderTexture;
        private bool _isDirty;
        private bool _gpuPaintingActive;
        private Texture _lastAssignedTexture;

        #endregion

        #region Public Properties

        public Texture2D PaintTexture => paintTexture;
        public int TextureSize => (int)resolution;
        public string MaterialPropertyName => materialPropertyName;
        public bool AutoSaveEnabled => autoSave;
        public bool IsDirty => _isDirty;

        #endregion

        // ═══════════════════════════════════════════════════════════════════
        //  IBrushTarget — CPU Painting Path
        // ═══════════════════════════════════════════════════════════════════

        public Texture2D GetPaintTexture()
        {
            if (paintTexture == null)
                InitializeTexture();
            return paintTexture;
        }

        public void OnStrokeBegin()
        {
            EnsureMeshCollider();
            GetPaintTexture();
        }

        public void OnPaintApplied()
        {
            AssignToMaterial();
        }

        public void OnStrokeEnd()
        {
            _isDirty = true;
            AutoSaveIfEnabled();
        }

        // ═══════════════════════════════════════════════════════════════════
        //  IGpuBrushTarget — GPU Painting Path
        // ═══════════════════════════════════════════════════════════════════

        /// <summary>
        /// Get or create the RenderTexture used for GPU painting.
        /// Initialized from the CPU-side Texture2D on first access.
        /// </summary>
        public RenderTexture GetPaintRenderTexture()
        {
            EnsureRenderTexture();

            // When transitioning to GPU mode, sync CPU-painted data to the RenderTexture
            if (!_gpuPaintingActive && _renderTexture != null && paintTexture != null)
                Graphics.Blit(paintTexture, _renderTexture);

            _gpuPaintingActive = true;
            return _renderTexture;
        }

        /// <summary>
        /// Read GPU RenderTexture pixels back to the CPU-side Texture2D.
        /// Called at stroke end so the Texture2D is up to date for saving.
        /// </summary>
        public void SyncGpuToCpu()
        {
            if (_renderTexture == null || paintTexture == null) return;

            RenderTexture prev = RenderTexture.active;
            RenderTexture.active = _renderTexture;
            paintTexture.ReadPixels(new Rect(0, 0, _renderTexture.width, _renderTexture.height), 0, 0);
            paintTexture.Apply();
            RenderTexture.active = prev;

            _gpuPaintingActive = false;
            _lastAssignedTexture = null; // Force reassign paintTexture on next call
            AssignToMaterial(); // Switch from RenderTexture to serializable paintTexture
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Public API
        // ═══════════════════════════════════════════════════════════════════

        public void InitializeTexture()
        {
            int size = (int)resolution;

            if (paintTexture != null)
            {
                if (paintTexture.width == size && paintTexture.height == size)
                {
                    FillTexture(paintTexture, fillColor);
                    paintTexture.Apply();
                    AssignToMaterial();
                    if (_renderTexture != null)
                        Graphics.Blit(paintTexture, _renderTexture);
                    return;
                }
                DestroyImmediate(paintTexture);
            }

            paintTexture = new Texture2D(size, size, TextureFormat.RGBAHalf, false, true)
            {
                name = $"{gameObject.name}_PaintTexture",
                wrapMode = TextureWrapMode.Clamp,
                filterMode = FilterMode.Bilinear
            };
            FillTexture(paintTexture, fillColor);
            paintTexture.Apply();
            AssignToMaterial();

            if (_renderTexture != null)
                Graphics.Blit(paintTexture, _renderTexture);
        }

        public void EnsureMeshCollider()
        {
            if (_meshCollider == null)
                _meshCollider = GetComponent<MeshCollider>();

            if (_meshCollider == null)
                _meshCollider = gameObject.AddComponent<MeshCollider>();

            var meshFilter = GetComponent<MeshFilter>();
            if (meshFilter != null && meshFilter.sharedMesh != null)
                _meshCollider.sharedMesh = meshFilter.sharedMesh;
        }

        public void AssignToMaterial()
        {
            if (!autoAssignToMaterial || paintTexture == null) return;

            var renderer = GetComponent<MeshRenderer>();
            if (renderer == null) return;

            var mat = renderer.sharedMaterial;
            if (mat == null) return;

            if (mat.HasProperty(materialPropertyName))
            {
                if (_originalTexture == null)
                    _originalTexture = mat.GetTexture(materialPropertyName) as Texture2D;

                // Use GPU RenderTexture only when GPU painting is active;
                // otherwise use the CPU Texture2D (fixes CPU mode invisibility)
                Texture tex = (_gpuPaintingActive && _renderTexture != null) ? _renderTexture : paintTexture;

                // Skip redundant SetTexture calls — avoids UAV/SRV rebind conflicts
                // that cause flickering when the compute shader writes to the same
                // RenderTexture that the material reads from.
                if (_lastAssignedTexture == tex)
                    return;

                mat.SetTexture(materialPropertyName, tex);
                _lastAssignedTexture = tex;
#if UNITY_EDITOR
                UnityEditor.EditorUtility.SetDirty(mat);
#endif
            }
        }

        public void RestoreMaterial()
        {
            if (_originalTexture == null) return;

            var renderer = GetComponent<MeshRenderer>();
            if (renderer == null) return;

            var mat = renderer.sharedMaterial;
            if (mat != null && mat.HasProperty(materialPropertyName))
                mat.SetTexture(materialPropertyName, _originalTexture);

            _originalTexture = null;
            _lastAssignedTexture = null;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Auto Save
        // ═══════════════════════════════════════════════════════════════════

        public void AutoSaveIfEnabled()
        {
            if (!autoSave || !_isDirty || paintTexture == null) return;
            SaveTextureToAsset();
            _isDirty = false;
        }

        public void SaveTextureToAsset()
        {
            if (paintTexture == null) return;
#if UNITY_EDITOR
            // If the texture is already an asset, just mark it dirty and save
            var existingPath = UnityEditor.AssetDatabase.GetAssetPath(paintTexture);
            if (!string.IsNullOrEmpty(existingPath))
            {
                UnityEditor.EditorUtility.SetDirty(paintTexture);
                UnityEditor.AssetDatabase.SaveAssets();
                return;
            }

            // If we have a saved path and the asset still exists, update it in-place
            if (!string.IsNullOrEmpty(savedAssetPath) &&
                System.IO.File.Exists(System.IO.Path.GetFullPath(savedAssetPath)))
            {
                var existingAsset = UnityEditor.AssetDatabase.LoadAssetAtPath<Texture2D>(savedAssetPath);
                if (existingAsset != null)
                {
                    var pixels = paintTexture.GetPixels();
                    existingAsset.SetPixels(pixels);
                    existingAsset.Apply();
                    UnityEditor.EditorUtility.SetDirty(existingAsset);
                    UnityEditor.AssetDatabase.SaveAssets();
                    return;
                }
            }

            // Create a new asset
            string path;
            if (autoSave)
            {
                if (!System.IO.Directory.Exists(saveFolder))
                    System.IO.Directory.CreateDirectory(saveFolder);
                path = $"{saveFolder}/{gameObject.name}_PaintTexture.asset";
                path = UnityEditor.AssetDatabase.GenerateUniqueAssetPath(path);
                UnityEditor.AssetDatabase.CreateAsset(paintTexture, path);
            }
            else
            {
                path = UnityEditor.EditorUtility.SaveFilePanelInProject(
                    "Save Paint Texture",
                    $"{gameObject.name}_PaintTexture.asset",
                    "asset",
                    "Save paint texture as asset");
                if (string.IsNullOrEmpty(path)) return;
                UnityEditor.AssetDatabase.CreateAsset(paintTexture, path);
            }

            savedAssetPath = path;
            UnityEditor.EditorUtility.SetDirty(paintTexture);
            UnityEditor.AssetDatabase.SaveAssets();
#endif
        }

        // ═══════════════════════════════════════════════════════════════════
        //  RenderTexture Management
        // ═══════════════════════════════════════════════════════════════════

        private void EnsureRenderTexture()
        {
            var tex = GetPaintTexture(); // Ensure Texture2D exists first
            int size = tex.width;

            if (_renderTexture != null && _renderTexture.width == size && _renderTexture.height == size)
                return;

            ReleaseRenderTexture();

            _renderTexture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGBHalf)
            {
                name = $"{gameObject.name}_PaintRT",
                enableRandomWrite = true
            };
            _renderTexture.Create();

            // Initialize from CPU texture
            Graphics.Blit(tex, _renderTexture);
        }

        private void ReleaseRenderTexture()
        {
            if (_renderTexture == null) return;
            _renderTexture.Release();
            _renderTexture = null;
            _lastAssignedTexture = null;
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Internal Helpers
        // ═══════════════════════════════════════════════════════════════════

        private void FillTexture(Texture2D tex, Color color)
        {
            var pixels = new Color[tex.width * tex.height];
            for (int i = 0; i < pixels.Length; i++)
                pixels[i] = color;
            tex.SetPixels(pixels);
        }

        // ═══════════════════════════════════════════════════════════════════
        //  Lifecycle
        // ═══════════════════════════════════════════════════════════════════

        private void OnDestroy()
        {
#if UNITY_EDITOR
            // Auto-save any unsaved dirty data before destruction
            if (_isDirty && autoSave && paintTexture != null)
                SaveTextureToAsset();

            RestoreMaterial();
            ReleaseRenderTexture();

            // Destroy untracked paint texture (not saved as an asset)
            if (paintTexture != null && string.IsNullOrEmpty(UnityEditor.AssetDatabase.GetAssetPath(paintTexture)))
                DestroyImmediate(paintTexture);
#endif
        }
    }
}
