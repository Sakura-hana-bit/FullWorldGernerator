using UnityEngine;

namespace GpuParticleDemo
{
    /// <summary>
    /// GPU-driven particle system demo.
    /// All particle data lives on the GPU via ComputeBuffer — zero CPU round-trips.
    /// Simulation runs on a ComputeShader; rendering via Graphics.DrawProcedural.
    /// </summary>
    public class GpuParticleDemo : MonoBehaviour
    {
        public struct Particle
        {
            public Vector3 position;
            public float   life;
            public Vector3 velocity;
            public float   seed;
            public Vector4 color;
        }

        [Header("Simulation")]
        [SerializeField] private int            _particleCount = 65536;
        [SerializeField] private ComputeShader  _computeShader;

        [Header("Rendering")]
        [SerializeField] private Material  _material;
        [SerializeField, Range(1f, 30f)] private float _pointSize = 10f;

        private ComputeBuffer _particleBuffer;
        private int _kernelInit;
        private int _kernelUpdate;
        private bool _initialized;

        private static readonly int
            ID_Particles      = Shader.PropertyToID("_Particles"),
            ID_ParticleCount  = Shader.PropertyToID("_ParticleCount"),
            ID_Time           = Shader.PropertyToID("_Time"),
            ID_DeltaTime      = Shader.PropertyToID("_DeltaTime"),
            ID_PointSize      = Shader.PropertyToID("_PointSize"),
            ID_ParticleBuffer = Shader.PropertyToID("_ParticleBuffer");

        private void Start()
        {
            Init();
        }

        private void Init()
        {
            if (_initialized) return;
            if (_computeShader == null || _material == null)
            {
                Debug.LogError("[GpuParticleDemo] Assign ComputeShader and Material in the Inspector.");
                return;
            }

            int stride = System.Runtime.InteropServices.Marshal.SizeOf<Particle>();
            _particleBuffer = new ComputeBuffer(_particleCount, stride);

            _kernelInit   = _computeShader.FindKernel("CSInit");
            _kernelUpdate = _computeShader.FindKernel("CSUpdate");

            _computeShader.SetBuffer(_kernelInit, ID_Particles, _particleBuffer);
            _computeShader.SetInt(ID_ParticleCount, _particleCount);
            _computeShader.SetFloat(ID_Time, Time.time);
            _computeShader.SetFloat(ID_DeltaTime, 0f);

            int groups = Mathf.CeilToInt(_particleCount / 256f);
            _computeShader.Dispatch(_kernelInit, groups, 1, 1);

            _material.SetBuffer(ID_ParticleBuffer, _particleBuffer);

            _initialized = true;
            Debug.Log($"[GpuParticleDemo] Initialized: {_particleCount} particles, stride={stride}");
        }

        private void Update()
        {
            if (!_initialized) return;

            // GPU simulation step
            _computeShader.SetBuffer(_kernelUpdate, ID_Particles, _particleBuffer);
            _computeShader.SetInt(ID_ParticleCount, _particleCount);
            _computeShader.SetFloat(ID_Time, Time.time);
            _computeShader.SetFloat(ID_DeltaTime, Time.deltaTime);

            int groups = Mathf.CeilToInt(_particleCount / 256f);
            _computeShader.Dispatch(_kernelUpdate, groups, 1, 1);

            // Bind buffer to material every frame (safety)
            _material.SetBuffer(ID_ParticleBuffer, _particleBuffer);
            _material.SetFloat(ID_PointSize, _pointSize);

            // Draw — no MaterialPropertyBlock to avoid overriding the buffer
            var bounds = new Bounds(transform.position, Vector3.one * 50f);
            Graphics.DrawProcedural(_material, bounds, MeshTopology.Points, _particleCount, 1);
        }

        private void OnDestroy()
        {
            _particleBuffer?.Release();
            _particleBuffer = null;
            _initialized = false;
        }
    }
}
