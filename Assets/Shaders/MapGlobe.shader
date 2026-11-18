Shader "HK132/MapGlobe"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _EdgeColor ("Edge Color", Color) = (0.16, 0.22, 0.14, 1)
        _EdgeSoft ("Edge Soft", Range(0.001, 0.25)) = 0.06
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "IgnoreProjector"="True" }
        Cull Back
        ZWrite On
        Lighting Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed4 _EdgeColor;
            float _EdgeSoft;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float soft = max(0.001, _EdgeSoft);
                float2 fade = smoothstep(0.0, soft, uv) * smoothstep(0.0, soft, 1.0 - uv);
                float cov = fade.x * fade.y;
                fixed4 tex = tex2D(_MainTex, saturate(uv)) * _Color;
                return lerp(_EdgeColor, tex, cov);
            }
            ENDCG
        }
    }
}
