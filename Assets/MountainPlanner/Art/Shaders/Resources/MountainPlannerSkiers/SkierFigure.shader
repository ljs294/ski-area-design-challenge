// Task 4.22: the guests as skiers and snowboarders. Each instance (SkierFigures, one draw per figure and LOD) is a root
// position and heading, two baked frames and a blend, and an outfit. The vertex stage skins the figure from the baked
// bone matrices (_Bones: per frame, per bone, three rows), up to _Influences bones a vertex; the fragment stage lights
// it flat-shaded (the faceted look of the concept art: the normal is the face's own, from screen derivatives), coloured
// by the vertex's material id through the guest's outfit, with the face's keyed tone.
Shader "MountainPlanner/SkierFigure"
{
    Properties { }
    SubShader
    {
        Tags { "RenderType" = "Opaque" "RenderPipeline" = "UniversalPipeline" "Queue" = "Geometry" }

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

        struct Figure { float4 positionYaw; uint frameA; uint frameB; float blend; uint outfit; };
        StructuredBuffer<Figure> _Figures;
        StructuredBuffer<float4> _Bones;
        StructuredBuffer<uint> _Outfits;
        int _BoneCount, _Base, _Influences;

        struct Attributes
        {
            float3 positionOS : POSITION;
            float4 color : COLOR;         // r material id / 255, g the face's tone (0.5 = none)
            uint4 bones : TEXCOORD0;
            float4 weights : TEXCOORD1;   // sum 1
            uint instance : SV_InstanceID;
        };

        float3 Bone(uint frame, uint bone, float3 p)
        {
            uint i = (frame * (uint)_BoneCount + bone) * 3;
            return float3(dot(_Bones[i].xyz, p) + _Bones[i].w, dot(_Bones[i + 1].xyz, p) + _Bones[i + 1].w,
                          dot(_Bones[i + 2].xyz, p) + _Bones[i + 2].w);
        }

        float3 Place(Attributes v, out Figure f)
        {
            f = _Figures[(uint)_Base + v.instance];
            float3 local = 0;
            [loop] for (int k = 0; k < _Influences; k++)
            {
                float w = v.weights[k];
                if (w <= 0) continue;
                uint b = v.bones[k];
                local += lerp(Bone(f.frameA, b, v.positionOS), Bone(f.frameB, b, v.positionOS), f.blend) * w;
            }
            float s, c;
            sincos(f.positionYaw.w, s, c);
            return f.positionYaw.xyz + float3(local.x * c + local.z * s, local.y, -local.x * s + local.z * c);
        }

        float3 FlatNormal(float3 positionRWS)
        {
            return normalize(cross(ddy(positionRWS), ddx(positionRWS)));
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #pragma multi_compile _ _ADDITIONAL_LIGHTS_VERTEX _ADDITIONAL_LIGHTS
            #pragma multi_compile_fragment _ _SHADOWS_SOFT _SHADOWS_SOFT_LOW _SHADOWS_SOFT_MEDIUM _SHADOWS_SOFT_HIGH
            #pragma multi_compile _ _CLUSTER_LIGHT_LOOP
            #pragma multi_compile_fog
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionRWS : TEXCOORD0;
                nointerpolation half3 albedo : TEXCOORD1;
                nointerpolation half2 surface : TEXCOORD2;   // metallic, smoothness
                float fogFactor : TEXCOORD3;
            };

            half3 OutfitColour(uint outfit, uint id)
            {
                uint c = _Outfits[outfit * 16 + id];
                half3 srgb = half3(c & 255, (c >> 8) & 255, (c >> 16) & 255) / 255.0h;
                return SRGBToLinear(srgb);
            }

            Varyings Vert(Attributes v)
            {
                Varyings o;
                Figure f;
                float3 positionWS = Place(v, f);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionRWS = positionWS - _WorldSpaceCameraPos;
                uint id = (uint)round(v.color.r * 255.0);
                o.albedo = OutfitColour(f.outfit, id) * (half)(v.color.g * 2.0);
                // Material ids in the order of skierkit/mesh.py MATS: 4 helmet, 5 lens (a mirror), 9 boot, 10 deck,
                // 11 metal, 12 base take a sheen; cloth and skin are matt (build.py ROUGH).
                half rough = id == 5 ? 0.10h : id == 4 ? 0.35h : (id == 10 || id == 11) ? 0.40h : id == 9 ? 0.45h : id == 12 ? 0.50h : 0.80h;
                o.surface = half2(id == 5 ? 1.0h : 0.0h, 1.0h - rough);
                o.fogFactor = ComputeFogFactor(o.positionCS.z);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                float3 positionWS = i.positionRWS + _WorldSpaceCameraPos;
                InputData input = (InputData)0;
                input.positionWS = positionWS;
                input.positionCS = i.positionCS;
                input.normalWS = FlatNormal(i.positionRWS);
                input.viewDirectionWS = GetWorldSpaceNormalizeViewDir(positionWS);
                input.shadowCoord = TransformWorldToShadowCoord(positionWS);
                input.fogCoord = i.fogFactor;
                input.bakedGI = SampleSH(input.normalWS);
                input.normalizedScreenSpaceUV = GetNormalizedScreenSpaceUV(i.positionCS);
                input.shadowMask = half4(1, 1, 1, 1);
                SurfaceData surface = (SurfaceData)0;
                surface.albedo = i.albedo;
                surface.metallic = i.surface.x;
                surface.smoothness = i.surface.y;
                surface.occlusion = 1;
                surface.alpha = 1;
                half4 colour = UniversalFragmentPBR(input, surface);
                colour.rgb = MixFog(colour.rgb, input.fogCoord);
                colour.a = 1;
                return colour;
            }
            ENDHLSL
        }

        Pass
        {
            Name "ShadowCaster"
            Tags { "LightMode" = "ShadowCaster" }
            ZWrite On
            ColorMask 0
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile_vertex _ _CASTING_PUNCTUAL_LIGHT_SHADOW
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Shadows.hlsl"
            float3 _LightDirection;

            float4 Vert(Attributes v) : SV_POSITION
            {
                Figure f;
                float3 positionWS = Place(v, f);
                // No vertex normals (the figure is flat shaded from derivatives): bias along the light only.
                float4 cs = TransformWorldToHClip(ApplyShadowBias(positionWS, _LightDirection, _LightDirection));
                #if UNITY_REVERSED_Z
                    cs.z = min(cs.z, UNITY_NEAR_CLIP_VALUE);
                #else
                    cs.z = max(cs.z, UNITY_NEAR_CLIP_VALUE);
                #endif
                return cs;
            }

            half4 Frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            float4 Vert(Attributes v) : SV_POSITION
            {
                Figure f;
                return TransformWorldToHClip(Place(v, f));
            }
            half Frag() : SV_Target { return 0; }
            ENDHLSL
        }

        Pass
        {
            Name "DepthNormals"
            Tags { "LightMode" = "DepthNormals" }
            ZWrite On
            Cull Back
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            struct V2F { float4 positionCS : SV_POSITION; float3 positionRWS : TEXCOORD0; };
            V2F Vert(Attributes v)
            {
                V2F o;
                Figure f;
                float3 positionWS = Place(v, f);
                o.positionCS = TransformWorldToHClip(positionWS);
                o.positionRWS = positionWS - _WorldSpaceCameraPos;
                return o;
            }
            half4 Frag(V2F i) : SV_Target { return half4(FlatNormal(i.positionRWS), 0); }
            ENDHLSL
        }
    }
}
