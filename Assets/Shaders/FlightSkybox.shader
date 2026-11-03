Shader "HK132/FlightSkybox"
{
    Properties
    {
        _SkyZenith ("Zenith", Color) = (0.20, 0.48, 0.88, 1)
        _SkyHorizon ("Horizon Sky", Color) = (0.70, 0.82, 0.94, 1)
        _Ground ("Distant Ground", Color) = (0.30, 0.33, 0.32, 1)
        _SunColor ("Sun", Color) = (1, 0.95, 0.82, 1)
        _SunDir ("Sun Dir", Vector) = (0.35, 0.55, 0.76, 0)
        _Exposure ("Exposure", Range(0.2, 3)) = 1.15
    }
    SubShader
    {
        Tags { "Queue"="Background" "RenderType"="Background" "PreviewType"="Skybox" }
        Cull Off
        ZWrite Off
        Fog { Mode Off }

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed3 _SkyZenith;
            fixed3 _SkyHorizon;
            fixed3 _Ground;
            fixed3 _SunColor;
            float4 _SunDir;
            float _Exposure;

            struct appdata
            {
                float4 vertex : POSITION;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 dir : TEXCOORD0;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.dir = v.vertex.xyz;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float3 dir = normalize(i.dir);
                // 地平线略压低，正视时大部分是天空，只有往下看才是地面。
                float elev = dir.y;
                float skyT = saturate(elev * 1.35 + 0.18);
                float3 sky = lerp(_SkyHorizon, _SkyZenith, pow(skyT, 0.55));
                float3 ground = lerp(_SkyHorizon * 0.85, _Ground, saturate((-elev) * 1.8));
                float blend = smoothstep(-0.04, 0.06, elev);
                float3 col = lerp(ground, sky, blend);

                float3 sun = normalize(_SunDir.xyz);
                float sunDot = saturate(dot(dir, sun));
                col += _SunColor * pow(sunDot, 180.0) * 1.4;
                col += _SunColor * pow(sunDot, 8.0) * 0.18;

                return fixed4(col * _Exposure, 1);
            }
            ENDCG
        }
    }
}
