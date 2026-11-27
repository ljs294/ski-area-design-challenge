// Task 4.50 slice 3 part 2, tests only (LampLightTests): a surface showing nothing but the building lamps' light on it
// (LampLight.hlsl), so a test can read a lamp's pool straight off a render. Not in any build (nothing references it).
Shader "Hidden/MountainPlanner/Tests/LampLightProbe"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        Pass
        {
            Tags { "LightMode" = "UniversalForward" }
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
            #include "Assets/MountainPlanner/Art/Shaders/LampLight.hlsl"

            struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; };
            struct Varyings { float4 positionCS : SV_POSITION; float3 positionWS : TEXCOORD0; float3 normalWS : TEXCOORD1; };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                o.positionWS = TransformObjectToWorld(v.positionOS.xyz);
                o.positionCS = TransformWorldToHClip(o.positionWS);
                o.normalWS = TransformObjectToWorldNormal(v.normalOS);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                return half4(LampLight(i.positionWS, normalize(i.normalWS)), 1);
            }
            ENDHLSL
        }
    }
}
