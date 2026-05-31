Shader "Hidden/MaskBrushDemo_Preview"
{
    Properties
    {
        _MainTex ("Mask (R channel)", 2D) = "white" {}
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" }
        LOD 100

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float2 uv : TEXCOORD0;
                float4 vertex : SV_POSITION;
            };

            sampler2D _MainTex;
            float4 _MainTex_ST;

            v2f vert (appdata v)
            {
                v2f o;
                o.vertex = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag (v2f i) : SV_Target
            {
                float mask = tex2D(_MainTex, i.uv).r;

                // 热力图着色: 0=深蓝 → 0.5=绿 → 1=红
                float r = saturate(mask * 2.0 - 0.5);
                float g = saturate(1.0 - abs(mask * 2.0 - 1.0));
                float b = saturate(1.0 - mask * 2.0);

                // 叠加微弱网格线便于辨识
                float2 grid = abs(frac(i.uv * 8.0) - 0.5);
                float gridLine = 1.0 - smoothstep(0.45, 0.48, max(grid.x, grid.y)) * 0.15;

                return float4(float3(r, g, b) * gridLine, 1.0);
            }
            ENDCG
        }
    }
}
