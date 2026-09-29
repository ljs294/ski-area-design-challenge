// Editor only (TreeImport): renders a tree into impostor frames and LOD fidelity checks.
//   pass 0  albedo: texture colour, covered pixels opaque (MSAA and supersampling give soft coverage)
//   pass 1  data: octahedral normal (tree space), occlusion, snow capacity on the side the view sees
//           (with the runtime's snow pattern on foliage, TreeCommon.hlsl)
// Which side of a card the view sees comes from the card's face normal (UV3.zyw) and the frame's view
// direction (_BakeViewDir), not the GPU's front-face flag, so it doesn't depend on matrix conventions.
Shader "Hidden/MountainPlanner/ImpostorBake"
{
    Properties
    {
        _BaseMap ("Albedo", 2D) = "white" {}
        _Cutoff ("Alpha cutoff", Float) = 0.4
        _Foliage ("Foliage", Float) = 0
    }
    SubShader
    {
        Tags { "RenderPipeline" = "UniversalPipeline" }
        Cull Off
        ZWrite On
        ZTest LEqual

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "TreeCommon.hlsl"
        TEXTURE2D(_BaseMap); SAMPLER(sampler_BaseMap);
        float _Cutoff;
        float _Foliage;
        float3 _BakeViewDir;   // towards the viewer, tree space

        struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; float2 uv : TEXCOORD0; float2 uv1 : TEXCOORD1; float2 uv2 : TEXCOORD2; float4 uv3 : TEXCOORD3; };
        struct Varyings { float4 positionCS : SV_POSITION; float2 uv : TEXCOORD0; float3 normalOS : TEXCOORD1; float3 shade : TEXCOORD2; float3 face : TEXCOORD3; };

        Varyings Vert(Attributes v)
        {
            Varyings o;
            o.positionCS = TransformObjectToHClip(v.positionOS.xyz);
            o.uv = v.uv;
            o.normalOS = v.normalOS;
            o.shade = float3(v.uv1.x, v.uv3.x, v.uv2.x);
            o.face = float3(v.uv3.z, v.uv3.y, v.uv3.w);
            return o;
        }

        half4 Albedo(Varyings i)
        {
            half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
            if (_Cutoff > 0) clip(c.a - _Cutoff);
            return half4(c.rgb, 1);
        }

        half4 Data(Varyings i)
        {
            half4 c = SAMPLE_TEXTURE2D(_BaseMap, sampler_BaseMap, i.uv);
            if (_Cutoff > 0) clip(c.a - _Cutoff);
            float3 n = normalize(i.normalOS);
            float3 face = dot(i.face, _BakeViewDir) >= 0 ? i.face : -i.face;
            float snow = saturate(i.shade.x * saturate(face.y * 1.6 + 0.1) * (_Foliage > 0.5 ? SnowPattern(i.uv, i.shade.z) : 1));
            return half4(OctEncode(n), i.shade.y, snow);
        }
        ENDHLSL

        Pass
        {
            Name "Albedo"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target { return Albedo(i); }
            ENDHLSL
        }

        Pass
        {
            Name "Data"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            half4 Frag(Varyings i) : SV_Target { return Data(i); }
            ENDHLSL
        }
    }
}
