Shader "Porter/ParticleFallback"
{
    // Unlit stand-in for a particle material whose Unreal shader could not be ported. It draws the base texture times the
    // tint times the particle's colour, so the effect shows up instead of pink.
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
        _Color ("Tint", Color) = (1,1,1,1)
        _Intensity ("Intensity", Float) = 1
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        [HideInInspector] _PorterParticleStreams ("Replay streams", Float) = 0
        [Enum(UnityEngine.Rendering.BlendMode)] _DstBlend ("Blend: One = additive, OneMinusSrcAlpha = alpha", Float) = 10
    }
    SubShader
    {
        Tags { "Queue"="Transparent" "RenderType"="Transparent" "IgnoreProjector"="True" "ForceNoShadowCasting"="True" }
        Pass
        {
            ZWrite Off
            Cull [_Cull]
            Blend SrcAlpha [_DstBlend]
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #pragma multi_compile_particles
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_ST;
            fixed4 _Color;
            float _Intensity;
            float _PorterParticleStreams;

            struct appdata
            {
                float4 vertex : POSITION;
                float4 uv : TEXCOORD0;
                float4 streamColor : TEXCOORD1;   // the replay player puts the particle's colour here
                fixed4 color : COLOR;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                fixed4 col : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = TRANSFORM_TEX(v.uv.xy, _MainTex);
                fixed4 c = _PorterParticleStreams > 0.5 ? (fixed4)v.streamColor : v.color;
                o.col = c * _Color;
                return o;
            }

            fixed4 frag(v2f i) : SV_Target
            {
                fixed4 t = tex2D(_MainTex, i.uv);
                fixed4 c = t * i.col;
                c.rgb *= _Intensity;
                return c;
            }
            ENDCG
        }
    }
}
