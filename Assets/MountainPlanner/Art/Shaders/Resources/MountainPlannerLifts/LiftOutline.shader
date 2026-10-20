// The hovered lift's glow (task 09, LT23): every part of the lift drawn again as a shell pushed out along its normals by a
// few pixels, white, behind its own surfaces (front faces culled), so only a casing round the silhouette shows; a wider,
// faint shell first makes the soft halo, and a thin dark rim lets the white read on snow. UI-15: white, no new colour. Depth-tested against the scene, so a
// ridge in front hides it as it hides the lift.
Shader "MountainPlanner/LiftOutline"
{
    Properties
    {
        _HaloPixels ("Halo width (px)", Float) = 10
        _RimPixels ("Dark rim width (px)", Float) = 4.5
        _CasingPixels ("Casing width (px)", Float) = 3
    }
    HLSLINCLUDE
    #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
    CBUFFER_START(UnityPerMaterial)
        float _HaloPixels;
        float _RimPixels;
        float _CasingPixels;
    CBUFFER_END
    struct Attributes { float4 positionOS : POSITION; float3 normalOS : NORMAL; UNITY_VERTEX_INPUT_INSTANCE_ID };
    struct Varyings { float4 positionCS : SV_POSITION; };

    Varyings Shell(Attributes v, float pixels)
    {
        Varyings o;
        UNITY_SETUP_INSTANCE_ID(v);
        float4 clip = TransformObjectToHClip(v.positionOS.xyz);
        float3 n = TransformWorldToHClipDir(TransformObjectToWorldNormal(v.normalOS));
        float2 dir = n.xy;
        float len = length(dir);
        dir = len > 1e-5 ? dir / len : float2(0, 0);
        // pixels → clip space: 2 / screen size, times w so the width holds at any distance.
        clip.xy += dir * pixels * 2 / _ScreenParams.xy * clip.w;
        o.positionCS = clip;
        return o;
    }
    ENDHLSL
    SubShader
    {
        Tags { "RenderType" = "Transparent" "RenderPipeline" = "UniversalPipeline" "Queue" = "Transparent" }
        Pass
        {
            Name "Halo"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            Varyings Vert(Attributes v) { return Shell(v, _HaloPixels); }
            half4 Frag() : SV_Target { return half4(1, 1, 1, 0.3); }
            ENDHLSL
        }
        Pass
        {
            // A thin dark edge just outside the white casing, so the casing reads on snow as well as on trees.
            Name "Rim"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            Varyings Vert(Attributes v) { return Shell(v, _RimPixels); }
            half4 Frag() : SV_Target { return half4(0.08, 0.08, 0.1, 0.55); }
            ENDHLSL
        }
        Pass
        {
            Name "Casing"
            Blend SrcAlpha OneMinusSrcAlpha
            ZWrite Off
            ZTest LEqual
            Cull Front
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma multi_compile_instancing
            Varyings Vert(Attributes v) { return Shell(v, _CasingPixels); }
            half4 Frag() : SV_Target { return half4(1, 1, 1, 0.95); }
            ENDHLSL
        }
    }
}
