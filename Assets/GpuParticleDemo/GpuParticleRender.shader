Shader "GpuParticleDemo/ParticleRender"
{
    SubShader
    {
        Tags { "RenderType"="Transparent" "Queue"="Transparent+100" }
        Blend SrcAlpha One
        ZWrite Off
        Cull Off

        Pass
        {
            CGPROGRAM
            #pragma target   5.0
            #pragma vertex   vert
            #pragma geometry geom
            #pragma fragment frag

            #include "UnityCG.cginc"

            struct Particle
            {
                float3 position;
                float  life;
                float3 velocity;
                float  seed;
                float4 color;
            };

            StructuredBuffer<Particle> _ParticleBuffer;
            float _PointSize;

            struct v2g
            {
                float3 worldPos : TEXCOORD0;
                float4 color    : COLOR0;
                float  size     : TEXCOORD1;
            };

            struct g2f
            {
                float4 pos   : SV_POSITION;
                float2 uv    : TEXCOORD0;
                float4 color : COLOR0;
            };

            v2g vert(uint id : SV_VertexID)
            {
                v2g o;
                Particle p = _ParticleBuffer[id];
                o.worldPos = p.position;
                o.color    = p.color;

                float dist = length(p.position - _WorldSpaceCameraPos);
                o.size = _PointSize * 0.02 * saturate(8.0 / (dist + 0.1));

                return o;
            }

            [maxvertexcount(4)]
            void geom(point v2g input[1], inout TriangleStream<g2f> stream)
            {
                float3 center = input[0].worldPos;
                float  s      = input[0].size;

                // Build billboard basis from camera vectors
                float3 camDir = normalize(_WorldSpaceCameraPos - center);
                float3 right  = normalize(cross(float3(0, 1, 0), camDir));
                float3 up     = cross(camDir, right);

                // Handle degenerate case when looking straight down/up
                if (length(cross(float3(0, 1, 0), camDir)) < 0.001)
                {
                    right = float3(1, 0, 0);
                    up    = float3(0, 0, 1);
                }

                float3 v[4];
                v[0] = center + (-right - up) * s;
                v[1] = center + ( right - up) * s;
                v[2] = center + (-right + up) * s;
                v[3] = center + ( right + up) * s;

                float2 uv[4] = { float2(0,0), float2(1,0), float2(0,1), float2(1,1) };

                g2f o;
                o.color = input[0].color;
                for (int i = 0; i < 4; i++)
                {
                    // World-space positions → clip space
                    o.pos = mul(UNITY_MATRIX_VP, float4(v[i], 1.0));
                    o.uv  = uv[i];
                    stream.Append(o);
                }
                stream.RestartStrip();
            }

            fixed4 frag(g2f i) : SV_Target
            {
                // Soft circle
                float2 c = i.uv - 0.5;
                float d = dot(c, c) * 4.0;
                float alpha = saturate(1.0 - d);
                alpha *= alpha;

                fixed4 col = i.color;
                col.a *= alpha;
                return col;
            }
            ENDCG
        }
    }
    FallBack Off
}
