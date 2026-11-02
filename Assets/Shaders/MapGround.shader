Shader "HK132/MapGround"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Color", Color) = (1,1,1,1)
        _FogColor ("Fog Color", Color) = (0.70, 0.82, 0.90, 1)
        _FogDensity ("Fog Density", Float) = 0.002
        _EdgeSoft ("Edge Soft", Range(0.001, 0.08)) = 0.018
        _Center ("Center", Vector) = (0,0,0,0)
        _Curve ("Earth Curve", Float) = 0.00004
    }
    SubShader
    {
        Tags { "Queue"="Geometry" "RenderType"="Opaque" "IgnoreProjector"="True" }
        Cull Off
        ZWrite On
        ZTest LEqual
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
            fixed4 _FogColor;
            float _FogDensity;
            float _EdgeSoft;
            float4 _Center;
            float _Curve;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 uv : TEXCOORD0;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float viewDist : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                float3 world = mul(unity_ObjectToWorld, v.vertex).xyz;
                float2 d = world.xz - _Center.xz;
                // 飞行模拟常用的地球曲率：远处地面沉到地平线以下，天空自然接上。
                world.y -= _Curve * dot(d, d);
                o.pos = UnityWorldToClipPos(float4(world, 1));
                o.uv = TRANSFORM_TEX(v.uv, _MainTex);
                o.viewDist = length(_WorldSpaceCameraPos - world);
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.uv;
                float soft = max(0.001, _EdgeSoft);
                float2 inside = smoothstep(float2(0, 0), float2(soft, soft), uv)
                    * smoothstep(float2(0, 0), float2(soft, soft), 1.0 - uv);
                float coverage = inside.x * inside.y;

                float2 sampleUv = clamp(uv, 0.001, 0.999);
                fixed4 tex = tex2D(_MainTex, sampleUv) * _Color;
                fixed4 ground = lerp(_FogColor, tex, coverage);

                // 指数距离雾（Exponential fog），和天空同色，不按贴图矩形淡出。
                float fog = 1.0 - exp(-i.viewDist * max(0.00001, _FogDensity));
                return lerp(ground, _FogColor, saturate(fog));
            }
            ENDCG
        }
    }
    FallBack "Unlit/Texture"
}
