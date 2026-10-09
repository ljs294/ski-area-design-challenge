// Photo mode's depth of field (task P2-07, S10): Natural (a long lens: the far slopes soften) and Miniature (tilt-shift:
// a sharp band, blur in front and behind). URP's own Bokeh can't blur at mountain distances (its circle of confusion is
// physical: under a pixel at 3 km even at 300 mm f/1), so this pass draws a look rather than a lens.
//
// The blur's size is a fraction of the picture's height, so a 2x photo looks like the screen. Pass 0 gathers a disc
// at half resolution; pass 1 mixes it back over the sharp picture by each pixel's blur. Runs before URP's post
// processing, in HDR, so the grade and tonemapping apply after it.
Shader "Hidden/MountainPlanner/PhotoFocus"
{
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" }
        ZWrite Off ZTest Always Cull Off Blend Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "Packages/com.unity.render-pipelines.core/Runtime/Utilities/Blit.hlsl"
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/DeclareDepthTexture.hlsl"

        // x: focus distance (metres), y: strength (blur per unit of 1 - focus/depth), z: largest blur radius as a
        // fraction of the picture height, w: height / width (the disc stays round on wide screens).
        float4 _PhotoFocus;
        TEXTURE2D_X(_PhotoFocusBlur);

        #define SAMPLES 48
        #define GOLDEN 2.39996323

        // The signed blur at a point: negative in front of the focus, positive behind, -1..1.
        float CoC(float2 uv)
        {
            float d = LinearEyeDepth(SampleSceneDepth(uv), _ZBufferParams);
            return clamp(_PhotoFocus.y * (1.0 - _PhotoFocus.x / max(d, 0.01)), -1.0, 1.0);
        }

        // Pass 0: a disc gather at half resolution. A sample counts when its own blur reaches the centre; a sample
        // behind the centre reaches no further than the centre's blur, so a sharp ridge doesn't pick up the blurred
        // valley behind it, while a blurred foreground still spreads over what's behind.
        half4 FragBlur(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            float cc = CoC(uv);
            float maxR = _PhotoFocus.z;
            float reachC = abs(cc) * maxR;
            float soft = 2.0 / _ScreenParams.y;   // about two pixels of feathering, in height units
            float3 sum = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, uv, 0).rgb;
            float weight = 1.0;
            UNITY_LOOP
            for (int i = 0; i < SAMPLES; i++)
            {
                float r = sqrt((i + 0.5) / SAMPLES) * maxR;
                float a = i * GOLDEN;
                float2 o = float2(cos(a) * _PhotoFocus.w, sin(a)) * r;
                float2 suv = uv + o;
                float cs = CoC(suv);
                float reach = abs(cs) * maxR;
                if (cs > cc) reach = min(reach, reachC);   // behind the centre
                float w = saturate((reach - r) / soft + 1.0);
                sum += SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_LinearClamp, suv, 0).rgb * w;
                weight += w;
            }
            return half4(sum / weight, 1.0);
        }

        // Pass 1: the sharp picture where the blur is under a pixel, the blurred one where it's larger.
        half4 FragMix(Varyings input) : SV_Target
        {
            UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
            float2 uv = input.texcoord;
            half4 sharp = SAMPLE_TEXTURE2D_X_LOD(_BlitTexture, sampler_PointClamp, uv, 0);
            half3 blurred = SAMPLE_TEXTURE2D_X_LOD(_PhotoFocusBlur, sampler_LinearClamp, uv, 0).rgb;
            float px = abs(CoC(uv)) * _PhotoFocus.z * _ScreenParams.y;
            return half4(lerp(sharp.rgb, blurred, smoothstep(0.5, 2.0, px)), sharp.a);
        }
        ENDHLSL

        Pass
        {
            Name "Photo focus blur"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragBlur
            ENDHLSL
        }

        Pass
        {
            Name "Photo focus mix"
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment FragMix
            ENDHLSL
        }
    }
}
