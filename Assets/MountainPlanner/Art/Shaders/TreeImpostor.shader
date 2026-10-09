// Far trees (TR2, replacing crossed cards): one camera-facing quad per tree showing the tree as baked from
// the nearest of 8 × 8 hemi-octahedral view directions (TreeImport bakes them from the full LOD0). The
// four nearest views are blended by the camera's direction, each projected through its own frame, so
// orbiting doesn't pop. Baked normals, occlusion and snow capacity let the impostor be lit like the mesh
// LODs (TreeCommon.hlsl), so a tree doesn't change when it switches to its impostor.
//   _ImpAlbedo  rgb albedo (unpremultiplied, dilated), a coverage
//   _ImpData    rg octahedral normal (tree space), b occlusion, a snow capacity
Shader "MountainPlanner/TreeImpostor"
{
    Properties
    {
        _ImpAlbedo ("Albedo atlas", 2D) = "white" {}
        _ImpData ("Normal, occlusion, snow atlas", 2D) = "gray" {}
        _ImpFrames ("Frames per side", Float) = 8
        _ImpFrameSize ("Texels per frame", Float) = 96
        _ImpCenter ("Crown centre (tree space)", Vector) = (0, 10, 0, 0)
        _ImpSize ("Half-width, height", Vector) = (3, 20, 0, 0)
        _Cutoff ("Coverage cutoff", Range(0, 1)) = 0.35
        _SnowLoad ("Snow load", Range(0, 1)) = 1
        _SnowColor ("Snow colour", Color) = (0.93, 0.95, 0.98, 1)
        _Translucency ("Back-light glow", Range(0, 1)) = 0.6
        _Brightness ("LOD brightness correction", Float) = 1
        _SnowScale ("LOD snow correction", Float) = 1
        _ImpSharpen ("Mip levels sharper than the screen", Range(0, 2)) = 1
        _ImpRelief ("Fleck relief (needle gaps)", Range(0, 1)) = 0.45
    }
    SubShader
    {
        Tags { "RenderType" = "TransparentCutout" "Queue" = "AlphaTest" "RenderPipeline" = "UniversalPipeline" }
        Cull Off

        HLSLINCLUDE
        #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"
        #include "TreeCommon.hlsl"

        CBUFFER_START(UnityPerMaterial)
            float4 _ImpAlbedo_TexelSize;
            float _ImpFrames;
            float _ImpFrameSize;
            float4 _ImpCenter;
            float4 _ImpSize;
            float _Cutoff;
            float _SnowLoad;
            float4 _SnowColor;
            float _Translucency;
            float _Brightness;
            float _SnowScale;
            float _ImpSharpen;
            float _ImpRelief;
        CBUFFER_END
        TEXTURE2D(_ImpAlbedo); SAMPLER(sampler_ImpAlbedo);
        TEXTURE2D(_ImpData);

        struct ImpostorVertex
        {
            float3 positionWS;
            float4 uvA;          // frames 0 and 1: position within each frame (0-1)
            float4 uvB;          // frames 2 and 3
            float4 cellsA;       // frame cells (column, row) of frames 0 and 1
            float4 cellsB;
            float4 weights;
            float fade;
            float2 rotation;     // sin, cos
            float3 scale;
        };

        float2 FrameExtent(float3 d)
        {
            // The quad must hold the tree's projection: a crown cylinder of half-width Rh and height H,
            // seen from elevation asin(d.y).
            float s = d.y, c = sqrt(saturate(1 - s * s));
            return float2(2 * _ImpSize.x, _ImpSize.y * c + 2 * _ImpSize.x * s) * 1.04;
        }

        float2 FrameUV(float3 p, float2 cell)
        {
            float n = _ImpFrames - 1;
            float3 d = HemiOctDecode(cell / n), right, up;
            ViewBasis(d, right, up);
            float2 e = FrameExtent(d);
            float3 q = p - _ImpCenter.xyz;
            return float2(dot(q, right) / e.x, dot(q, up) / e.y) + 0.5;
        }

        ImpostorVertex BuildImpostor(uint index, float2 corner)
        {
            ImpostorVertex o;
            Tree t = _Trees[index];
            float2 sc = TreeRotation(t);
            o.rotation = sc;
            o.fade = TreeFade(t.position);
            float w = t.widthScale * (1 + _TreeFade.w * o.fade);
            o.scale = float3(w, t.heightScale, w);

            // The camera's direction in the tree's own (unrotated, unscaled) space, kept above the horizon.
            float3 local = UnrotateY(_WorldSpaceCameraPos - t.position, sc) / o.scale;
            float3 d = local - _ImpCenter.xyz;
            d.y = max(d.y, 0.0);
            d = normalize(d + float3(0, 1e-4, 0));

            float n = _ImpFrames - 1;
            float2 g = HemiOctEncode(d) * n;
            float2 g0 = min(floor(g), n - 1);
            float2 f = saturate(g - g0);
            o.cellsA = float4(g0, g0 + float2(1, 0));
            o.cellsB = float4(g0 + float2(0, 1), g0 + 1);
            o.weights = float4((1 - f.x) * (1 - f.y), f.x * (1 - f.y), (1 - f.x) * f.y, f.x * f.y);

            float3 right, up;
            ViewBasis(d, right, up);
            float2 e = FrameExtent(d);
            float3 p = _ImpCenter.xyz + right * (corner.x * e.x) + up * (corner.y * e.y);
            o.positionWS = t.position + RotateY(p * o.scale, sc);
            o.uvA = float4(FrameUV(p, o.cellsA.xy), FrameUV(p, o.cellsA.zw));
            o.uvB = float4(FrameUV(p, o.cellsB.xy), FrameUV(p, o.cellsB.zw));
            return o;
        }

        // Snow and needles as flecks (P2-09): the atlas's mips average a crown's white snow clumps and dark needles
        // into one even grey, so far trees read as grey round blobs from above. Each frame texel cell instead
        // keeps all or none of its snow, so the mean stays and the contrast comes back; cells are anchored to the
        // frame's image (no shimmer while orbiting) and stay about a screen pixel wide. y: a cell's relief, 0-1.
        float2 Flecks(float2 texel, float2 cell, half snow, float level)
        {
            float l = max(level, 1), l0 = floor(l);
            float2 m = 0;
            [unroll] for (int k = 0; k < 2; k++)
            {
                float size = exp2(l0 + k);
                float2 c = floor(texel / size) + cell * 97 + size * 17;
                float h = Hash21(c), r = Hash21(c + 53.7);
                m += (k == 0 ? 1 - (l - l0) : l - l0) * float2(saturate((snow - h) / 0.15 + 0.5), r);
            }
            return m;
        }

        // Blends the four frames: colour weighted by coverage, so transparent texels never darken.
        void SampleImpostor(float4 uvA, float4 uvB, float4 cellsA, float4 cellsB, float4 weights,
                            out half4 albedo, out half4 data, out half2 flecks)
        {
            float2 uvs[4] = { uvA.xy, uvA.zw, uvB.xy, uvB.zw };
            float2 cells[4] = { cellsA.xy, cellsA.zw, cellsB.xy, cellsB.zw };
            float w4[4] = { weights.x, weights.y, weights.z, weights.w };
            // Mip level from the frame's own texel density, a level sharper than the screen asks (alpha to
            // coverage and the flecks keep it from sparkling), and capped so neighbouring frames don't bleed in.
            float2 dx = ddx(uvA.xy) * _ImpFrameSize, dy = ddy(uvA.xy) * _ImpFrameSize;
            float level = 0.5 * log2(max(dot(dx, dx), dot(dy, dy)));
            float lod = clamp(level - _ImpSharpen, 0, 3);
            half3 colour = 0;
            half4 dat = 0;
            half coverage = 0;
            // The flecks follow the frame with the most weight (one frame's cells; four would cost four times as much).
            float2 topUV = uvA.xy, topCell = cellsA.xy;
            float topW = weights.x;
            if (weights.y > topW) { topW = weights.y; topUV = uvA.zw; topCell = cellsA.zw; }
            if (weights.z > topW) { topW = weights.z; topUV = uvB.xy; topCell = cellsB.xy; }
            if (weights.w > topW) { topUV = uvB.zw; topCell = cellsB.zw; }
            [unroll] for (int k = 0; k < 4; k++)
            {
                float2 uv = uvs[k];
                if (w4[k] <= 0.001 || any(uv < 0) || any(uv > 1)) continue;
                float2 atlas = (cells[k] + uv) / _ImpFrames;
                half4 c = SAMPLE_TEXTURE2D_LOD(_ImpAlbedo, sampler_ImpAlbedo, atlas, lod);
                half4 d = SAMPLE_TEXTURE2D_LOD(_ImpData, sampler_ImpAlbedo, atlas, lod);
                half a = c.a * w4[k];
                colour += c.rgb * a;
                dat += d * a;
                coverage += a;
            }
            half inv = 1 / max(coverage, 1e-4);
            albedo = half4(colour * inv, coverage);
            data = dat * inv;
            flecks = Flecks(saturate(topUV) * _ImpFrameSize, topCell, data.a, level);
        }
        ENDHLSL

        Pass
        {
            Name "ForwardLit"
            Tags { "LightMode" = "UniversalForward" }
            AlphaToMask On
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            #pragma multi_compile _ _MAIN_LIGHT_SHADOWS _MAIN_LIGHT_SHADOWS_CASCADE _MAIN_LIGHT_SHADOWS_SCREEN
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"
            #include "TreeLighting.hlsl"
            #include "Haze.hlsl"

            struct Attributes { float4 positionOS : POSITION; uint instance : SV_InstanceID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float3 positionWS : TEXCOORD0;
                float4 uvA : TEXCOORD1;
                float4 uvB : TEXCOORD2;
                nointerpolation float4 cellsA : TEXCOORD3;
                nointerpolation float4 cellsB : TEXCOORD4;
                nointerpolation float4 weights : TEXCOORD5;
                nointerpolation float4 rotationFade : TEXCOORD6;   // sin, cos, fade
                nointerpolation float3 scale : TEXCOORD7;
                nointerpolation half3 tint : TEXCOORD8;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                uint index = TreeIndex(v.instance);
                ImpostorVertex iv = BuildImpostor(index, v.positionOS.xy);
                o.positionWS = iv.positionWS;
                o.positionCS = TransformWorldToHClip(iv.positionWS);
                o.uvA = iv.uvA; o.uvB = iv.uvB; o.cellsA = iv.cellsA; o.cellsB = iv.cellsB; o.weights = iv.weights;
                o.rotationFade = float4(iv.rotation, iv.fade, 0);
                o.scale = iv.scale;
                o.tint = TreeTint(index);
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half4 albedo, data;
                half2 flecks;
                SampleImpostor(i.uvA, i.uvB, i.cellsA, i.cellsB, i.weights, albedo, data, flecks);
                half coverage = saturate((albedo.a - _Cutoff) / max(fwidth(albedo.a), 0.0001) + 0.5);
                clip(coverage - 0.01);
                float3 nLocal = OctDecode(data.rg);
                float3 n = normalize(RotateY(nLocal / i.scale, i.rotationFade.xy));
                // The flecks hold the snow's share (white clumps on dark needles); load, LOD and distance scale it.
                float snow = saturate(flecks.x * _SnowLoad * _SnowScale * lerp(1, _TreeFade.z, i.rotationFade.z));
                half3 colour = lerp(albedo.rgb * i.tint * _Brightness, _SnowColor.rgb, snow);
                // Needle cells sit at different depths in the crown: some catch light, some are gaps.
                half ao = data.b * lerp(1 - _ImpRelief, 1, flecks.y);
                ao = lerp(ao, 1, snow * 0.3);
                half3 lit = TreeLight(colour, SnowNormal(n, snow), ao, i.positionWS, _Translucency * (1 - snow));
                return half4(ApplyHaze(lit, i.positionWS), coverage);
            }
            ENDHLSL
        }

        Pass
        {
            Name "DepthOnly"
            Tags { "LightMode" = "DepthOnly" }
            ZWrite On
            ColorMask R
            HLSLPROGRAM
            #pragma vertex Vert
            #pragma fragment Frag
            #pragma target 4.5
            struct Attributes { float4 positionOS : POSITION; uint instance : SV_InstanceID; };
            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                float4 uvA : TEXCOORD1;
                float4 uvB : TEXCOORD2;
                nointerpolation float4 cellsA : TEXCOORD3;
                nointerpolation float4 cellsB : TEXCOORD4;
                nointerpolation float4 weights : TEXCOORD5;
            };

            Varyings Vert(Attributes v)
            {
                Varyings o;
                ImpostorVertex iv = BuildImpostor(TreeIndex(v.instance), v.positionOS.xy);
                o.positionCS = TransformWorldToHClip(iv.positionWS);
                o.uvA = iv.uvA; o.uvB = iv.uvB; o.cellsA = iv.cellsA; o.cellsB = iv.cellsB; o.weights = iv.weights;
                return o;
            }

            half4 Frag(Varyings i) : SV_Target
            {
                half4 albedo, data;
                half2 flecks;
                SampleImpostor(i.uvA, i.uvB, i.cellsA, i.cellsB, i.weights, albedo, data, flecks);
                clip(albedo.a - _Cutoff);
                return 0;
            }
            ENDHLSL
        }
    }
}
