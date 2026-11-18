Shader "HK132/PlaneUnlit"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _Ambient ("Ambient", Color) = (0.38, 0.44, 0.52, 1)
        _Wrap ("Wrap", Range(0, 1)) = 0.45
        _Rim ("Rim", Range(0, 1)) = 0.22
        _Gloss ("Gloss", Range(2, 64)) = 18
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" }
        Cull Back
        ZWrite On
        Fog { Mode Off }

        Pass
        {
            Tags { "LightMode"="ForwardBase" }
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_fwdbase
            #include "UnityCG.cginc"
            #include "Lighting.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            fixed3 _Ambient;
            float _Wrap;
            float _Rim;
            float _Gloss;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 worldN : TEXCOORD1;
                float3 worldV : TEXCOORD2;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.worldN = UnityObjectToWorldNormal(v.normal);
                o.worldV = _WorldSpaceCameraPos - worldPos;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 n = normalize(i.worldN);
                float3 v = normalize(i.worldV);
                float3 l = normalize(_WorldSpaceLightPos0.xyz);
                if (dot(l, l) < 0.01)
                    l = normalize(float3(0.35, 0.72, 0.4));

                fixed4 albedo = tex2D(_MainTex, i.uv) * _Color;
                float ndl = dot(n, l);
                float wrap = saturate(ndl * (1.0 - _Wrap) + _Wrap);
                float3 h = normalize(l + v);
                float spec = pow(saturate(dot(n, h)), _Gloss) * saturate(ndl) * 0.28;
                float rim = pow(1.0 - saturate(dot(n, v)), 2.4) * _Rim;
                float3 lightCol = _LightColor0.rgb;
                if (dot(lightCol, lightCol) < 0.01)
                    lightCol = float3(1.0, 0.97, 0.92);

                float3 lit = albedo.rgb * (_Ambient + lightCol * wrap);
                lit += lightCol * spec;
                lit += float3(0.55, 0.72, 0.95) * rim;
                return fixed4(lit, 1);
            }
            ENDCG
        }
    }
}
