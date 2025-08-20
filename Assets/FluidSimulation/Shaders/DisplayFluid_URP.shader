Shader "Custom/DisplayFluidURP"
{
    Properties
    {
        _DyeTex("Dye", 2D) = "black" {}
        _Background("Background", Color) = (0,0,0,1)
    }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Cull Off ZWrite Off ZTest Always
        Pass
        {
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma target 3.0
            #include "UnityCG.cginc"

            // Use explicit HLSL texture + sampler for better SRP/URP compatibility
            Texture2D<float4> _DyeTex;
            // Unity expects the sampler to be named 'sampler_<TextureName>' so it can bind correctly
            SamplerState sampler_DyeTex;

            float4 _Background;
            float2 _TexelSize;
            int _ApplyShading;

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 uv  : TEXCOORD0;
            };

            v2f vert(uint id : SV_VertexID)
            {
                v2f o;
                float2 verts[3] = {
                    float2(-1,-1),
                    float2(-1, 3),
                    float2( 3,-1)
                };
                o.pos = float4(verts[id], 0, 1);
                o.uv = 0.5 * (verts[id] + 1);
                return o;
            }

            static float3 SampleColor(Texture2D<float4> tex, SamplerState samp, float2 uv)
            {
                // Sample with provided sampler state (linear filtering)
                return tex.Sample(samp, uv).rgb;
            }

            float4 frag(v2f i) : SV_Target
            {
                float3 c = SampleColor(_DyeTex, sampler_DyeTex, i.uv);

                if (_ApplyShading == 1)
                {
                    float3 lc = SampleColor(_DyeTex, sampler_DyeTex, i.uv + float2(-_TexelSize.x, 0));
                    float3 rc = SampleColor(_DyeTex, sampler_DyeTex, i.uv + float2(_TexelSize.x, 0));
                    float3 tc = SampleColor(_DyeTex, sampler_DyeTex, i.uv + float2(0, _TexelSize.y));
                    float3 bc = SampleColor(_DyeTex, sampler_DyeTex, i.uv + float2(0, -_TexelSize.y));

                    float dx = length(rc) - length(lc);
                    float dy = length(tc) - length(bc);

                    float3 n = normalize(float3(dx, dy, max(0.0001, length(_TexelSize))));
                    float3 l = float3(0.0, 0.0, 1.0);

                    float diffuse = clamp(dot(n, l) + 0.7, 0.7, 1.0);
                    c *= diffuse;
                }

                float a = max(c.r, max(c.g, c.b));
                // gamma-correct output
                c = pow(saturate(c), 1.0/2.2);
                return float4(lerp(_Background.rgb, c, a), 1);
            }
            ENDHLSL
        }
    }
}
