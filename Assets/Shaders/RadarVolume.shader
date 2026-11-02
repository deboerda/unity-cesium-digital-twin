Shader "HK132/RadarVolume"
{
    Properties
    {
        _Color ("Color", Color) = (0.3, 0.85, 1, 0.18)
        _EdgeColor ("Edge Color", Color) = (0.6, 1, 1, 0.95)
        _EdgeStart ("Edge Start", Range(0, 1)) = 0.28
        _EdgeEnd ("Edge End", Range(0, 1)) = 0.02
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" }
        Cull Back
        Lighting Off
        ZWrite Off
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            fixed4 _Color;
            fixed4 _EdgeColor;
            float _EdgeStart;
            float _EdgeEnd;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 normal : NORMAL;
                float4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float3 worldNormal : TEXCOORD0;
                float3 viewDir : TEXCOORD1;
                float4 color : COLOR;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                float3 worldPos = mul(unity_ObjectToWorld, v.vertex).xyz;
                o.worldNormal = UnityObjectToWorldNormal(v.normal);
                o.viewDir = _WorldSpaceCameraPos - worldPos;
                o.color = v.color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                return fixed4(_Color.rgb, saturate(_Color.a));
            }
            ENDCG
        }
    }
}
