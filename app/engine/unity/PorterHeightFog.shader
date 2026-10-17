Shader "Hidden/PorterHeightFog"
{
    Properties
    {
        _MainTex ("Texture", 2D) = "white" {}
    }
    SubShader
    {
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "UnityCG.cginc"

            sampler2D _MainTex;
            float4 _MainTex_TexelSize;
            sampler2D_float _CameraDepthTexture;
            float4x4 _FogCorners;
            float4 _FogCamPos;
            float4 _FogColor;
            float4 _FogDirColor;
            float4 _FogSunDir;
            float4 _FogParams;
            float4 _FogParams2;
            float4 _FogParams3;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv : TEXCOORD0;
                float2 uvDepth : TEXCOORD1;
                float3 ray : TEXCOORD2;
            };

            // The same highlight roll-off the sky shaders use, so a bright fog colour does not clip.
            float3 Shoulder(float3 c)
            {
                const float k = 0.55;
                const float s = 1.0 - k;
                float3 rolled = k + s * (1.0 - exp(-max(c - k, 0.0) / s));
                return lerp(c, rolled, step(k, c));
            }

            // Drawn with CustomBlit: each corner of the quad carries its frustum corner index in vertex.z (the Unity Standard Assets fog recipe),
            // so the camera-to-pixel ray is right on every graphics API, in the Game view and in the Scene view.
            v2f vert(appdata_img v)
            {
                v2f o;
                float index = v.vertex.z;
                v.vertex.z = 0.1;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.uv = v.texcoord.xy;
                o.uvDepth = v.texcoord.xy;
                #if UNITY_UV_STARTS_AT_TOP
                if (_MainTex_TexelSize.y < 0.0) o.uv.y = 1.0 - o.uv.y;
                #endif
                o.ray = _FogCorners[(int)index].xyz;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float4 src = tex2D(_MainTex, i.uv);
                float depth01 = Linear01Depth(SAMPLE_DEPTH_TEXTURE(_CameraDepthTexture, i.uvDepth));
                // the sky (nothing drawn at the far plane) is not fogged
                if (depth01 >= 0.99999) return src;
                float3 delta = depth01 * i.ray;
                float dist = length(delta);
                if (dist < 0.0001 || dist > _FogParams2.z) return src;
                float3 rd = delta / dist;

                float density = _FogParams.x;
                float falloff = _FogParams.y;
                float height = _FogParams.z;
                float start = _FogParams.w;
                float rdy = rd.y;
                float3 rdDir = rd;

                float travel = max(dist - start, 0.0);
                float h0 = _FogCamPos.y + rdy * start;
                float d0 = density * exp2(-falloff * (h0 - height));
                float t = clamp(falloff * rdy * travel, -40.0, 40.0);
                float shape = abs(t) < 0.0001 ? 1.0 : (1.0 - exp2(-t)) / (t * 0.693147);
                float optical = d0 * travel * shape;
                float dB0 = _FogParams3.x * exp2(-_FogParams3.y * (h0 - _FogParams3.z));
                float tB = clamp(_FogParams3.y * rdy * travel, -40.0, 40.0);
                float shapeB = abs(tB) < 0.0001 ? 1.0 : (1.0 - exp2(-tB)) / (tB * 0.693147);
                optical += dB0 * travel * shapeB;
                float fog = min(1.0 - exp(-max(optical, 0.0)), _FogParams2.y);

                float dirStart = max(_FogParams2.w, start);
                float travelDir = max(dist - dirStart, 0.0);
                float h1 = _FogCamPos.y + rdy * dirStart;
                float d1 = density * exp2(-falloff * (h1 - height));
                float tDir = clamp(falloff * rdy * travelDir, -40.0, 40.0);
                float shapeDir = abs(tDir) < 0.0001 ? 1.0 : (1.0 - exp2(-tDir)) / (tDir * 0.693147);
                float opticalDir = d1 * travelDir * shapeDir;
                float dB1 = _FogParams3.x * exp2(-_FogParams3.y * (h1 - _FogParams3.z));
                float tBd = clamp(_FogParams3.y * rdy * travelDir, -40.0, 40.0);
                float shapeBd = abs(tBd) < 0.0001 ? 1.0 : (1.0 - exp2(-tBd)) / (tBd * 0.693147);
                opticalDir += dB1 * travelDir * shapeBd;
                float fogDir = min(1.0 - exp(-max(opticalDir, 0.0)), _FogParams2.y);

                float dirAmt = pow(saturate(dot(rdDir, _FogSunDir.xyz)), max(_FogParams2.x, 0.01));
                float3 inscatter = _FogColor.rgb * fog + _FogDirColor.rgb * (dirAmt * fogDir);
                float3 outCol = src.rgb * (1.0 - fog) + inscatter;
                return float4(outCol, src.a);
            }
            ENDCG
        }
    }
    Fallback Off
}
