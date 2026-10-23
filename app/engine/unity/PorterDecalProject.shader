Shader "Porter/DecalProject"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        [Toggle] _Additive ("Additive", Float) = 0
        _EdgeFade ("Fade at the grazing angles", Range(0,1)) = 0.5
    }
    SubShader
    {
        Tags { "Queue"="Transparent-10" "RenderType"="Transparent" "IgnoreProjector"="True" "ForceNoShadowCasting"="True" }
        Pass
        {
            ZWrite Off
            ZTest Always
            Cull Front
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            sampler2D_float _CameraDepthTexture;
            fixed4 _Color;
            float _Additive;
            float _EdgeFade;

            struct appdata
            {
                float4 vertex : POSITION;
                float3 ax : NORMAL;
                float4 ay : TANGENT;
                float4 az : TEXCOORD0;
                float4 col : TEXCOORD1;
                float4 ctr : TEXCOORD2;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float4 sp : TEXCOORD0;
                float3 wray : TEXCOORD1;
                nointerpolation float3 ax : TEXCOORD2;
                nointerpolation float3 ay : TEXCOORD3;
                nointerpolation float3 az : TEXCOORD4;
                nointerpolation float3 ctr : TEXCOORD5;
                nointerpolation float4 col : TEXCOORD6;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.sp = ComputeScreenPos(o.pos);
                o.wray = mul(unity_ObjectToWorld, v.vertex).xyz - _WorldSpaceCameraPos;
                o.ax = v.ax;
                o.ay = v.ay.xyz;
                o.az = v.az.xyz;
                o.ctr = v.ctr.xyz;
                o.col = v.col;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                float2 uv = i.sp.xy / i.sp.w;
                float z = LinearEyeDepth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, uv));
                float3 camFwd = -UNITY_MATRIX_V[2].xyz;
                float3 world = _WorldSpaceCameraPos + i.wray * (z / dot(i.wray, camFwd));
                float3 d = mul(unity_WorldToObject, float4(world, 1)).xyz - i.ctr;
                float3 l = float3(dot(i.ax, d), dot(i.ay, d), dot(i.az, d));
                clip(1.0 - abs(l));
                float2 tuv = l.xz * 0.5 + 0.5;
                fixed4 c = tex2D(_MainTex, TRANSFORM_TEX(tuv, _MainTex)) * _Color * i.col;
                c.a *= saturate((1.0 - abs(l.y)) / max(1.0 - _EdgeFade, 1e-3));
                if (_Additive > 0.5) c.rgb *= c.a;
                return c;
            }
            ENDCG
        }
    }
    Fallback Off
}
