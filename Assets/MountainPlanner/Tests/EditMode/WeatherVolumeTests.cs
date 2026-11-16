using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Weather;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Task 4.06: the weather's screen pass (WeatherVolume) and the mountain's shadow ceiling its god rays read. The ceiling's
    // kernels are pinned against a plain C# reference; what fogs itself (lifts, cables, skiers, the design's lines) draws after
    // every other solid thing, so its stencil mark is the last word on its pixels.
    public sealed class WeatherVolumeTests
    {
        const float Metres = 32;

        // A flat valley at 1000 m with a wall along x = 40, 500 m high, 64 texels square.
        static float[] Ridge(int size = 64, int wall = 40)
        {
            var h = Enumerable.Repeat(1000f, size * size).ToArray();
            for (int y = 0; y < size; y++) h[y * size + wall] = 1500;
            return h;
        }

        // Rolling ground by a keyed hash, so no two texels agree by accident.
        static float[] Rough(int size)
        {
            var h = new float[size * size];
            for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                uint k = (uint)(x * 73856093) ^ (uint)(y * 19349663);
                k ^= k >> 13; k *= 0x5bd1e995; k ^= k >> 15;
                h[y * size + x] = 1500 + 400 * Mathf.Sin(x * 0.21f) * Mathf.Cos(y * 0.17f) + (k & 1023) * 0.2f;
            }
            return h;
        }

        [Test]
        public void ARidgeShadowsTheAirBehindItUpToTheSunsRay()
        {
            const float tan = 0.5f;   // the sun 26.6 degrees up, toward +x: the wall's shadow falls toward -x
            var c = ShadowCeiling.Build(Ridge(), 64, 64, Metres, 1, 0, tan);
            for (int x = 0; x <= 40; x++)
                Assert.That(c[20 * 64 + x], Is.EqualTo(Mathf.Max(1000, 1500 - (40 - x) * Metres * tan)).Within(0.01f), $"texel {x}");
            for (int x = 41; x < 64; x++) Assert.That(c[20 * 64 + x], Is.EqualTo(1000).Within(0.01f), "the sunny side is lit down to the ground");
            // Ten texels behind the wall the ceiling is 1340 m: air under it is dark, air over it lit, easing across the softness.
            float ceiling = c[20 * 64 + 30];
            Assert.That(ShadowCeiling.Lit(1200, ceiling, FarTerrainShadow.CeilingSoftness), Is.EqualTo(0));
            Assert.That(ShadowCeiling.Lit(1400, ceiling, FarTerrainShadow.CeilingSoftness), Is.EqualTo(1));
            Assert.That(ShadowCeiling.Lit(ceiling, ceiling, FarTerrainShadow.CeilingSoftness), Is.EqualTo(0.5f).Within(1e-4f));
        }

        [TestCase(1, 0)]
        [TestCase(-1, 0)]
        [TestCase(0, 1)]
        [TestCase(0, -1)]
        public void DoublingFindsWhatLookingAtEveryDistanceFinds(int lx, int lz)
        {
            var h = Rough(48);
            var fast = ShadowCeiling.Build(h, 48, 48, Metres, lx, lz, 0.3f);
            var slow = ShadowCeiling.BruteForce(h, 48, 48, Metres, lx, lz, 0.3f);
            for (int i = 0; i < h.Length; i++) Assert.That(fast[i], Is.EqualTo(slow[i]).Within(0.01f), $"texel {i}");
        }

        [Test]
        public void TheCeilingIsNeverUnderTheGroundAndFallsAsTheSunClimbs()
        {
            var h = Rough(48);
            var low = ShadowCeiling.Build(h, 48, 48, Metres, 0.6f, 0.8f, 0.15f);
            var high = ShadowCeiling.Build(h, 48, 48, Metres, 0.6f, 0.8f, 0.9f);
            for (int i = 0; i < h.Length; i++)
            {
                Assert.That(low[i], Is.GreaterThanOrEqualTo(h[i]));
                Assert.That(high[i], Is.GreaterThanOrEqualTo(h[i]));
                Assert.That(high[i], Is.LessThanOrEqualTo(low[i] + 0.01f), "a higher sun shadows less air");
            }
        }

        [Test]
        public void TheCoarseMapKeepsEachBlocksHighestGround()
        {
            var h = new float[10 * 6];
            for (int i = 0; i < h.Length; i++) h[i] = i;
            var c = ShadowCeiling.Start(h, 10, 6, 4, out int w, out int d);
            Assert.That((w, d), Is.EqualTo((3, 2)));
            Assert.That(c[0], Is.EqualTo(3 * 10 + 3), "rows 0-3, columns 0-3");
            Assert.That(c[2], Is.EqualTo(3 * 10 + 9), "the last block stops at the map's edge");
            Assert.That(c[1 * 3 + 2], Is.EqualTo(5 * 10 + 9));
        }

        // The kernels the game runs give what the reference gives: exactly along an axis (every tap lands on a texel), and
        // within the sampler's 1/256 weights on a diagonal.
        [TestCase(1f, 0f, 0.4f, 0.05f)]
        [TestCase(0f, -1f, 0.2f, 0.05f)]
        [TestCase(0.6f, 0.8f, 0.3f, 6f)]
        public void TheComputeKernelsMatchTheReference(float lx, float lz, float tan, float within)
        {
            Assume.That(SystemInfo.supportsComputeShaders, "no compute shaders on this device");
            var compute = AssetDatabase.LoadAssetAtPath<ComputeShader>("Assets/MountainPlanner/Art/Shaders/FarShadow.compute");
            Assert.That(compute, Is.Not.Null);
            const int size = 64, scale = FarTerrainShadow.CeilingScale;
            var h = Rough(size);
            var heights = new Texture2D(size, size, TextureFormat.RFloat, false, true) { filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            heights.SetPixelData(h, 0);
            heights.Apply(false);
            var coarse = ShadowCeiling.Start(h, size, size, scale, out int w, out int d);
            var a = Target(w, d);
            var b = Target(w, d);
            try
            {
                int start = compute.FindKernel("CeilingStart"), step = compute.FindKernel("CeilingStep");
                compute.SetVector("_ShadowParams", new Vector4(size, size, 0, 0));
                compute.SetVector("_Light", new Vector4(lx, lz, tan, 0));
                compute.SetInt("_CeilScale", scale);
                compute.SetVector("_CeilParams", new Vector4(w, d, 0, 0));
                compute.SetTexture(start, "_HeightsIn", heights);
                compute.SetTexture(start, "_CeilOut", a);
                compute.Dispatch(start, (w + 7) / 8, (d + 7) / 8, 1);
                Assert.That(Read(a), Is.EqualTo(coarse).Within(0.001f), "the coarse map");
                RenderTexture from = a, to = b;
                float metres = FarTerrainShadow.MetresPerTexel * scale;
                for (int s = 1; s < Mathf.Max(w, d); s *= 2)
                {
                    compute.SetVector("_CeilParams", new Vector4(w, d, s, s * metres * tan));
                    compute.SetTexture(step, "_CeilIn", from);
                    compute.SetTexture(step, "_CeilOut", to);
                    compute.Dispatch(step, (w + 7) / 8, (d + 7) / 8, 1);
                    (from, to) = (to, from);
                }
                Assert.That(Read(from), Is.EqualTo(ShadowCeiling.Build(coarse, w, d, metres, lx, lz, tan)).Within(within));
            }
            finally
            {
                a.Release(); b.Release();
                Object.DestroyImmediate(a); Object.DestroyImmediate(b); Object.DestroyImmediate(heights);
            }
        }

        static RenderTexture Target(int w, int d)
        {
            var rt = new RenderTexture(w, d, 0, RenderTextureFormat.RFloat, RenderTextureReadWrite.Linear)
                { enableRandomWrite = true, filterMode = FilterMode.Bilinear, wrapMode = TextureWrapMode.Clamp };
            rt.Create();
            return rt;
        }

        static float[] Read(RenderTexture rt)
        {
            var tex = new Texture2D(rt.width, rt.height, TextureFormat.RFloat, false, true);
            var active = RenderTexture.active;
            RenderTexture.active = rt;
            tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
            RenderTexture.active = active;
            var data = tex.GetPixelData<float>(0).ToArray();
            Object.DestroyImmediate(tex);
            return data;
        }

        [Test]
        public void EachTierDrawsTheAirItsOwnWay()
        {
            Assert.That(WeatherVolume.ModeFor(QualityPreset.Low), Is.EqualTo(WeatherVolumeMode.Off), "Low keeps the surfaces' fog: no pass");
            Assert.That(WeatherVolume.ModeFor(QualityPreset.Medium), Is.EqualTo(WeatherVolumeMode.Rays));
            Assert.That(WeatherVolume.ModeFor(QualityPreset.High), Is.EqualTo(WeatherVolumeMode.Full));
            Assert.That(WeatherVolume.ModeFor(QualityPreset.Ultra), Is.EqualTo(WeatherVolumeMode.Full));
            Assert.That(WeatherVolume.StepsMedium, Is.LessThan(WeatherVolume.StepsHigh));
            Assert.That(WeatherVolume.DeckStepsHigh, Is.LessThan(WeatherVolume.DeckStepsUltra));
        }

        [TestCase("off", true, WeatherVolumeMode.Off)]
        [TestCase("RAYS", true, WeatherVolumeMode.Rays)]
        [TestCase("Full", true, WeatherVolumeMode.Full)]
        [TestCase("2", false, WeatherVolumeMode.Off)]
        [TestCase("fog", false, WeatherVolumeMode.Off)]
        [TestCase("", false, WeatherVolumeMode.Off)]
        [TestCase("-wxlook", false, WeatherVolumeMode.Off)]
        public void TheSwitchReadsItsThreeWordsOnly(string text, bool ok, WeatherVolumeMode mode)
        {
            Assert.That(WeatherVolume.TryParseMode(text, out var got), Is.EqualTo(ok));
            Assert.That(got, Is.EqualTo(mode));
        }

        // Sixteen neighbours start their steps at sixteen different sixteenths, so the 4x4 smooth averages a full set.
        [Test]
        public void TheStepPatternSpreadsEverySixteenthOverFourByFour()
        {
            var starts = new System.Collections.Generic.List<float>();
            for (int y = 0; y < 4; y++) for (int x = 0; x < 4; x++) starts.Add(WeatherVolume.StepStart(x + 8, y + 4));
            starts.Sort();
            for (int i = 0; i < 16; i++) Assert.That(starts[i], Is.EqualTo((i + 0.5f) / 16).Within(1e-6f));
            Assert.That(WeatherVolume.StepStart(5, 6), Is.EqualTo(WeatherVolume.StepStart(1, 2)), "it repeats every four texels");
        }

        // The pass skips what carries the stencil mark, and a mark outlives anything drawn over it later: so the marked things
        // draw after every other solid thing, and still as solid things (ambient occlusion, depth, front-to-back order).
        [Test]
        public void WhatFogsItselfDrawsAfterEveryOtherSolidThing()
        {
            int Queue(string name)
            {
                var shader = Shader.Find(name);
                Assert.That(shader, Is.Not.Null, name);
                return shader.renderQueue;
            }
            int lastUnmarked = new[] { "MountainPlanner/Terrain", "MountainPlanner/Cliff", "MountainPlanner/DioramaWall", "MountainPlanner/TreeImpostor", "MountainPlanner/TreeInstanced" }.Max(Queue);
            foreach (string kept in new[] { "MountainPlanner/LiftStructure", "MountainPlanner/LiftCable", "MountainPlanner/SkierFigure", "Hidden/MountainPlanner/DesignLine", "MountainPlanner/PendingContours" })
            {
                Assert.That(Queue(kept), Is.GreaterThan(lastUnmarked), kept);
                Assert.That(Queue(kept), Is.LessThanOrEqualTo(2500), kept + " stays in the solid range");
            }
        }

        [Test]
        public void ThePassQueuesNothingUntilSomethingIsInTheAir()
        {
            var volume = new WeatherVolume();
            try
            {
                Assume.That(volume.Available, "the pass's shader isn't available on this device");
                Assert.That(volume.Active, Is.False);
                Assert.That(volume.Queued, Is.EqualTo(0));
            }
            finally { volume.Dispose(); }
        }

        // F8: the shaders see the drift wrapped; the turned tiles take the rest from DriftCorrection, so a point of the field
        // reads the same texel either side of a wrap (it used to jump: the wrap wasn't a whole number of turned tiles).
        [Test]
        public void TheCloudsDriftOnWithoutAJumpAtTheWrap()
        {
            const double a = 0.7986f, b = 0.6018f;
            double Frac(double v) => v - System.Math.Floor(v);
            double Wrap(double v) => v - System.Math.Floor(v / WeatherView.DriftWrap) * WeatherView.DriftWrap;
            float qx = 1234.5f, qy = -876.25f;
            foreach (double total in new[] { WeatherView.DriftWrap - 0.5, WeatherView.DriftWrap + 0.5, 7 * WeatherView.DriftWrap + 3.25, 123456.75 })
            {
                double tx = total, ty = total * 0.37;
                var corr = WeatherView.DriftCorrection(tx, ty);
                // What the shader reads: the point in the wrapped field, turned, plus the correction.
                double cx = qx - Wrap(tx), cy = qy - Wrap(ty);
                double small = Frac((cx * a - cy * b) / WeatherView.SmallTile + corr.x);
                double huge = Frac((cy * a - cx * b) / WeatherView.HugeTile + corr.z);
                // What it should read: the point in the whole drift's field.
                double wx = qx - tx, wy = qy - ty;
                double smallTrue = Frac((wx * a - wy * b) / WeatherView.SmallTile), hugeTrue = Frac((wy * a - wx * b) / WeatherView.HugeTile);
                Assert.That(System.Math.Min(System.Math.Abs(small - smallTrue), 1 - System.Math.Abs(small - smallTrue)), Is.LessThan(2e-5), $"small tile at {total}");
                Assert.That(System.Math.Min(System.Math.Abs(huge - hugeTrue), 1 - System.Math.Abs(huge - hugeTrue)), Is.LessThan(2e-5), $"huge tile at {total}");
            }
            Assert.That(WeatherView.DriftWrap % WeatherView.LargeTile, Is.EqualTo(0), "the large tile repeats at the wrap");
            Assert.That((double)WeatherView.DriftWrap * 0.2 % WeatherView.LargeTile, Is.EqualTo(0).Within(0.01), "and the valley sea's (a fifth of the drift)");
        }

        // F1, F4: inside a cloud the light is the haze's own by day (no darker than the air outside), and at night as bright as
        // the moon and the sky make the snow, not the dark horizon's navy; and it's mostly grey.
        [Test]
        public void TheLightInsideACloudMatchesTheAirOutsideIt()
        {
            static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;
            var noonHorizon = new Color(0.62f, 0.66f, 0.72f);
            var day = WeatherView.CloudLight(noonHorizon, new Color(0.34f, 0.33f, 0.31f), new Color(0.17f, 0.26f, 0.42f), 1);
            Assert.That(Luma(day), Is.EqualTo(Luma(noonHorizon)).Within(0.03f), "as bright as the haze outside");
            Assert.That(day.b - day.r, Is.LessThan(0.03f), "grey, not blue");
            var nightHorizon = new Color(0.006f, 0.008f, 0.012f);
            var night = WeatherView.CloudLight(nightHorizon, new Color(0.03f, 0.04f, 0.08f), new Color(0.011f, 0.021f, 0.068f), 1);
            Assert.That(Luma(night), Is.GreaterThan(4 * Luma(nightHorizon)), "the moon and the sky light it");
        }

        // Stage 2 (fast clock): the far shadows fade between the light's last map and one a step ahead; the step grows with the
        // light's speed, so the builds stay about four a second however fast the clock runs.
        [Test]
        public void TheFarShadowsStepAheadOfAFastSun()
        {
            Assert.That(FarTerrainShadow.StepFor(0), Is.EqualTo(FarTerrainShadow.StepDegrees));
            Assert.That(FarTerrainShadow.StepFor(8), Is.EqualTo(2).Within(1e-4f), "8°/s (8× at night) builds every quarter second");
            Assert.That(FarTerrainShadow.StepFor(100), Is.EqualTo(FarTerrainShadow.MostStepDegrees));
            Assert.That(FarTerrainShadow.CeilingStrength(0), Is.EqualTo(0));
            Assert.That(FarTerrainShadow.CeilingStrength(FarTerrainShadow.CeilingFull), Is.EqualTo(1));
            float mid = FarTerrainShadow.CeilingStrength(0.5f * (FarTerrainShadow.CeilingFrom + FarTerrainShadow.CeilingFull));
            Assert.That(mid, Is.GreaterThan(0.2f).And.LessThan(0.8f), "the rays fade in at sunrise, not switch on");
            Assert.That(SceneLighting.ShadowFade(SceneLighting.LeastShadow), Is.EqualTo(0), "shadows are gone before they switch off");
            Assert.That(SceneLighting.ShadowFade(2 * SceneLighting.LeastShadow), Is.EqualTo(1));
        }

        // Settings › Weather › Clouds: 3D marches the deck on any tier, Flat never does (rays stay), Auto and Off keep the tier's.
        [Test]
        public void TheCloudsSettingPicksHowTheDeckIsDrawn()
        {
            Assert.That(WeatherView.ModeFor(WeatherVolumeMode.Off, CloudLook.ThreeD), Is.EqualTo(WeatherVolumeMode.Full));
            Assert.That(WeatherView.ModeFor(WeatherVolumeMode.Full, CloudLook.Flat), Is.EqualTo(WeatherVolumeMode.Rays));
            Assert.That(WeatherView.ModeFor(WeatherVolumeMode.Off, CloudLook.Flat), Is.EqualTo(WeatherVolumeMode.Off));
            Assert.That(WeatherView.ModeFor(WeatherVolumeMode.Rays, CloudLook.Auto), Is.EqualTo(WeatherVolumeMode.Rays));
            Assert.That(WeatherView.ModeFor(WeatherVolumeMode.Full, CloudLook.Off), Is.EqualTo(WeatherVolumeMode.Full));
        }

        // Settings › Weather › Cloud ceiling: a deck under the floor rises to it; one over it, or no area yet, stays put.
        [Test]
        public void TheCloudCeilingLiftsOnlyALowDeck()
        {
            float floor = 3200 + WeatherOptions.CeilingClearance;
            Assert.That(2600 + WeatherView.CeilingLift(2600, floor), Is.EqualTo(floor).Within(1e-3f));
            Assert.That(WeatherView.CeilingLift(4000, floor), Is.EqualTo(0));
            Assert.That(WeatherView.CeilingLift(2600, float.NegativeInfinity), Is.EqualTo(0));
            Assert.That(WeatherOptions.CeilingClearance, Is.EqualTo(1000 * 0.3048f).Within(1e-3f), "1,000 ft");
        }

        // The viewer made as Unity makes it: its field initializers run inside a MonoBehaviour's constructor, where Unity
        // forbids calls such as GlobalKeyword.Create. The editor and the Development game enforce that and the release game
        // doesn't, so the weather's keywords made there once failed every frame of the Development benchmark and no release
        // run showed it. Any such call logs an error here, which fails the test.
        [Test]
        public void TheViewerIsMadeWithoutForbiddenCalls()
        {
            var go = new GameObject("viewer");
            try { Assert.IsNotNull(go.AddComponent<MountainPlanner.App.MountainViewer>()); }
            finally { Object.DestroyImmediate(go); }
        }
    }
}
