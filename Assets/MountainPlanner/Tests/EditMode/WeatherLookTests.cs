using MountainPlanner.Domain.Weather;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Weather;
using MountainPlanner.Simulation;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // Task 4.06: the weather's look from the snapshot (WeatherLook). The arcs follow the events and the hour, the look eases in
    // face time with a real-time limit and snaps after a skip, the overview is veiled but never blind, and the snow line
    // reads from the summit down.
    public sealed class WeatherLookTests
    {
        static WeatherSample Sample(int feet, int snow, int liquid = 0) =>
            new WeatherSample { Feet = feet, SnowPermille = snow, Liquid = liquid, Temp = 250, Wind = 100 };

        static WeatherHour Hour(int state, int liquid = 0, int cloud = 500) =>
            new WeatherHour { State = state, Liquid = liquid, Cloud = cloud, Clearness = 600, CloudBaseFeet = 3000, GustFactor = 1300, Direction = 250 };

        static int Bit(WeatherEventKind e) => 1 << (int)e;

        // Weather.hlsl rides on Haze.hlsl and FarShadow.hlsl: every shader that includes them must still compile.
        [TestCase("MountainPlanner/Sky")]
        [TestCase("MountainPlanner/Terrain")]
        [TestCase("MountainPlanner/Cliff")]
        [TestCase("MountainPlanner/DioramaWall")]
        [TestCase("MountainPlanner/TreeImpostor")]
        [TestCase("MountainPlanner/TreeInstanced")]
        [TestCase("MountainPlanner/LiftStructure")]
        [TestCase("MountainPlanner/LiftCable")]
        [TestCase("MountainPlanner/SkierFigure")]
        [TestCase("MountainPlanner/Precipitation")]
        [TestCase("MountainPlanner/WindRibbon")]
        public void TheShadersTheWeatherTouchesCompile(string name)
        {
            var shader = Shader.Find(name);
            Assert.That(shader, Is.Not.Null, name);
            var errors = ShaderUtil.GetShaderMessages(shader).Where(m => m.severity == UnityEditor.Rendering.ShaderCompilerMessageSeverity.Error).ToList();
            Assert.That(errors.Select(e => $"{e.message} ({e.file}:{e.line})"), Is.Empty);
            Assert.That(ShaderUtil.ShaderHasError(shader), Is.False);
        }

        [Test]
        public void TheCloudFieldIsEvenSoCoverIsTheCloudedShare()
        {
            var v = CloudField.Values(64);
            foreach (float cover in new[] { 0.2f, 0.5f, 0.8f })
                Assert.That(v.Count(x => x > 1 - cover) / (float)v.Length, Is.EqualTo(cover).Within(0.01f));
        }

        static float Luma(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        // The review's first finding: a storm must read as a storm, not a greyer noon. Darker by day, a sky ceiling darker than
        // its horizon, no cast shadows, contrast kept.
        [Test]
        public void AStormIsDarkNotAGreyerNoon()
        {
            var noon = LightingPreset.All[LightingPreset.Noon];
            var storm = new WeatherGrade { Overcast = 1, Storm = 1 }.Apply(noon, 0);
            Assert.That(storm.Exposure, Is.LessThanOrEqualTo(noon.Exposure - 0.5f));
            Assert.That(Luma(storm.Zenith), Is.LessThan(Luma(storm.Horizon)), "the sky is a ceiling, darker overhead");
            Assert.That(storm.ShadowStrength, Is.LessThan(0.05f));
            Assert.That(storm.Contrast, Is.GreaterThanOrEqualTo(noon.Contrast - 5));
            var night = new WeatherGrade { Overcast = 1, Storm = 1 }.Apply(LightingPreset.All[LightingPreset.Night], 1);
            Assert.That(night.Exposure, Is.GreaterThan(LightingPreset.All[LightingPreset.Night].Exposure), "at night the snow holds the light");
        }

        // Every arc's own look differs from every other's, so the player tells them apart at a glance (cold snap is not bluebird).
        [Test]
        public void EveryArcHasALookOfItsOwn()
        {
            var noon = LightingPreset.All[LightingPreset.Noon];
            var looks = new (string, WeatherGrade)[]
            {
                ("storm", new WeatherGrade { Overcast = 1, Storm = 0.8f }),
                ("bigstorm", new WeatherGrade { Overcast = 1, Storm = 1, BigStorm = 1 }),
                ("rain", new WeatherGrade { Overcast = 1, Storm = 0.8f, Rain = 1 }),
                ("building", new WeatherGrade { Building = 1 }),
                ("clearing", new WeatherGrade { Clearing = 1 }),
                ("bluebird", new WeatherGrade { Bluebird = 1 }),
                ("coldsnap", new WeatherGrade { ColdSnap = 1 }),
                ("thaw", new WeatherGrade { Thaw = 1 }),
            };
            float Distance(LightingPreset a, LightingPreset b) =>
                Mathf.Abs(a.Exposure - b.Exposure) * 10 + Mathf.Abs(a.Temperature - b.Temperature) + Mathf.Abs(a.Saturation - b.Saturation) +
                Mathf.Abs(a.Contrast - b.Contrast) + Vector4.Distance(a.Zenith, b.Zenith) * 20 + Vector4.Distance(a.SplitShadows, b.SplitShadows) * 20;
            for (int i = 0; i < looks.Length; i++)
                for (int j = i + 1; j < looks.Length; j++)
                    Assert.That(Distance(looks[i].Item2.Apply(noon, 0), looks[j].Item2.Apply(noon, 0)), Is.GreaterThan(4f),
                                $"{looks[i].Item1} vs {looks[j].Item1}");
        }

        [Test]
        public void DuskEasesTheNightInsteadOfSwitching()
        {
            Assert.That(SceneLighting.NightWeight(5), Is.EqualTo(0));
            Assert.That(SceneLighting.NightWeight(-8), Is.EqualTo(1));
            float a = SceneLighting.NightWeight(-3), b = SceneLighting.NightWeight(-4), c = SceneLighting.NightWeight(-5);
            Assert.That(a, Is.InRange(0.05f, 0.95f));
            Assert.That(b, Is.GreaterThan(a));
            Assert.That(c, Is.GreaterThan(b));
        }

        [Test]
        public void ArcsFollowTheEventsAndTheHour()
        {
            var snowTop = Sample(10000, 1000);
            var rainTop = Sample(10000, 0);
            Assert.That(WeatherLook.ArcOf(Hour(2, 20), snowTop, Bit(WeatherEventKind.Storm)), Is.EqualTo(WeatherLook.Arc.Storm));
            Assert.That(WeatherLook.ArcOf(Hour(2, 20), snowTop, Bit(WeatherEventKind.BigStorm)), Is.EqualTo(WeatherLook.Arc.BigStorm));
            Assert.That(WeatherLook.ArcOf(Hour(2, 20), rainTop, 0), Is.EqualTo(WeatherLook.Arc.Rain));
            Assert.That(WeatherLook.ArcOf(Hour(2), snowTop, 0), Is.EqualTo(WeatherLook.Arc.Storm), "a storm between bursts is still a storm");
            Assert.That(WeatherLook.ArcOf(Hour(1), snowTop, 0), Is.EqualTo(WeatherLook.Arc.Building));
            Assert.That(WeatherLook.ArcOf(Hour(3), snowTop, 0), Is.EqualTo(WeatherLook.Arc.Clearing));
            Assert.That(WeatherLook.ArcOf(Hour(0), snowTop, Bit(WeatherEventKind.Bluebird)), Is.EqualTo(WeatherLook.Arc.Bluebird));
            Assert.That(WeatherLook.ArcOf(Hour(0), snowTop, Bit(WeatherEventKind.Thaw)), Is.EqualTo(WeatherLook.Arc.Thaw));
            Assert.That(WeatherLook.ArcOf(Hour(0), snowTop, Bit(WeatherEventKind.ColdSnap)), Is.EqualTo(WeatherLook.Arc.ColdSnap));
            var pooled = Hour(0); pooled.Fog = true; pooled.Pool = 60;
            Assert.That(WeatherLook.ArcOf(pooled, snowTop, 0), Is.EqualTo(WeatherLook.Arc.Inversion));
            Assert.That(WeatherLook.ArcOf(Hour(0), snowTop, 0), Is.EqualTo(WeatherLook.Arc.Fair));
        }

        [Test]
        public void SnowLineReadsFromTheSummitDown()
        {
            Assert.That(WeatherLook.SnowLineFeet(Sample(6000, 0), Sample(8000, 1000), Sample(10000, 1000)), Is.EqualTo(7000).Within(1));
            Assert.That(WeatherLook.SnowLineFeet(Sample(6000, 1000), Sample(8000, 1000), Sample(10000, 1000)), Is.LessThan(6000));
            Assert.That(WeatherLook.SnowLineFeet(Sample(6000, 0), Sample(8000, 0), Sample(10000, 0)), Is.GreaterThan(10000));
            // A cold pool snows at the base under rain at mid: the line is above mid, where the snow starts from the top.
            float pooled = WeatherLook.SnowLineFeet(Sample(6000, 1000), Sample(8000, 0), Sample(10000, 1000));
            Assert.That(pooled, Is.InRange(8000, 10000));
        }

        [Test]
        public void SnowBlindsMoreThanRainAndHeavierBlindsMore()
        {
            float light = WeatherLook.Visibility(1, 0.5f, 1), heavy = WeatherLook.Visibility(1, 4, 1), rain = WeatherLook.Visibility(1, 4, 0);
            Assert.That(heavy, Is.LessThan(light));
            Assert.That(rain, Is.GreaterThan(heavy * 3));
            Assert.That(heavy, Is.GreaterThanOrEqualTo(WeatherLook.Tune.LeastVisibility));
            Assert.That(WeatherLook.Visibility(0, 0, 1), Is.EqualTo(WeatherLook.Tune.ClearVisibility));
            Assert.That(WeatherLook.Visibility(1, 0, 1), Is.LessThan(WeatherLook.Tune.ClearVisibility));
        }

        [Test]
        public void TheOverviewIsVeiledNeverBlind()
        {
            float storm = WeatherLook.Visibility(1, 6, 1);
            foreach (float d in new[] { 1500f, 3500f, 6000f })
            {
                float v = WeatherLook.ViewVisibility(storm, d);
                Assert.That(WeatherLook.FogAt(d, v), Is.LessThanOrEqualTo(WeatherLook.Tune.VeilMost + 1e-3f), $"at {d} m");
            }
            Assert.That(WeatherLook.ViewVisibility(storm, 100), Is.EqualTo(storm), "near the ground the storm is the storm");
            Assert.That(WeatherLook.ViewVisibility(WeatherLook.Tune.ClearVisibility, 3500), Is.EqualTo(WeatherLook.Tune.ClearVisibility));
        }

        static WeatherLook.State Look(float cover, float vis) => new WeatherLook.State { Cover = cover, Visibility = vis, Direction = 250 };

        [Test]
        public void EasesInFaceTimeWithARealTimeLimitAndSnapsAfterASkip()
        {
            var e = new WeatherLook.Easer();
            e.Step(Look(0, 60000), 100, 0, 0, false);
            Assert.That(e.Shown.Cover, Is.EqualTo(0));

            // The next hour arrives: it starts from what was shown and reaches the hour by EaseShare of the face hour.
            e.Step(Look(1, 500), 101, 0, 0.1f, false);
            Assert.That(e.Shown.Cover, Is.EqualTo(0).Within(1e-4));
            e.Step(Look(1, 500), 101, 1_800_000, 0.1f, false);
            Assert.That(e.Shown.Cover, Is.GreaterThan(0).And.LessThan(1), "the real-time limit holds a fast hour back");
            for (int i = 0; i < 60; i++) e.Step(Look(1, 500), 101, 2_400_000, 0.1f, false);
            Assert.That(e.Shown.Cover, Is.EqualTo(1).Within(1e-4));
            Assert.That(e.Shown.Visibility, Is.EqualTo(500).Within(1));

            // At 8× a night hour is about 1.9 s: the face says done, the real-time limit keeps it blending.
            e.Step(Look(0, 60000), 102, 3_000_000, 1.9f, false);
            Assert.That(e.Blend, Is.LessThan(1));

            // A skip, or a jump over an hour, snaps.
            e.Step(Look(0.5f, 9000), 103, 0, 0.01f, true);
            Assert.That(e.Shown.Cover, Is.EqualTo(0.5f));
            e.Step(Look(0.2f, 9000), 110, 0, 0.01f, false);
            Assert.That(e.Shown.Cover, Is.EqualTo(0.2f));
        }

        [Test]
        public void FromReadsTheCellUnderTheTargetAndTheDeckInSeaLevelMetres()
        {
            var h = Hour(2, 30, 950);
            var cell = new WeatherCell { Known = true, Feet = 7000, HvfFeet = 400, Liquid = 30, SnowPermille = 1000, Wind = 250, Direction = 280 };
            var s = WeatherLook.From(h, Sample(6000, 1000), Sample(8000, 1000), Sample(10000, 1000), cell, 6500);
            Assert.That(s.DeckBase, Is.EqualTo((6500 + 3000) * 0.3048f).Within(0.1));
            Assert.That(s.Deck, Is.GreaterThan(0.9f));
            Assert.That(s.Rate, Is.EqualTo(30 * 0.254f).Within(1e-3));
            Assert.That(s.Wind, Is.EqualTo(25));
            Assert.That(s.Direction, Is.EqualTo(280));
            Assert.That(s.Visibility, Is.LessThan(2000));
        }

        // The per-frame path from the snapshot (the hour, the cell under the target, the arc) through the ease, across hour
        // boundaries, allocates nothing. Unity's runtime has no per-thread counter, so this measures the whole heap (FlowTests).
        [Test]
        public void TheHourEaseAllocatesNothing()
        {
            var e = new WeatherLook.Easer();
            var cell = new WeatherCell { Known = true, Feet = 7000, HvfFeet = 400, Liquid = 30, SnowPermille = 1000, Wind = 250, Direction = 280 };
            WeatherSample b = Sample(6000, 1000), m = Sample(8000, 1000), t = Sample(10000, 1000);
            WeatherHour storm = Hour(2, 30, 950), clear = Hour(0, 0, 200);
            int events = Bit(WeatherEventKind.BigStorm);
            float shown = 0;
            void Frames(int from, int hours)
            {
                for (int hour = from; hour < from + hours; hour++)
                    for (long ms = 0; ms < 3_600_000; ms += 30_000)
                    {
                        var h = hour % 2 == 0 ? storm : clear;
                        var look = WeatherLook.From(h, b, m, t, cell, 6500);
                        if (WeatherLook.ArcOf(h, t, events) == WeatherLook.Arc.BigStorm) shown += 0.001f;
                        e.Step(look, hour, ms, 0.02f, snap: false);
                        shown += e.Shown.Cover;
                    }
            }
            Frames(0, 2);   // warm
            long before = System.GC.GetTotalMemory(false);
            Frames(2, 8);   // 960 frames over 8 hour boundaries
            long grew = System.GC.GetTotalMemory(false) - before;
            var probe = new byte[64 * 1024];
            long probed = System.GC.GetTotalMemory(false) - before;
            System.GC.KeepAlive(probe);
            Assert.That(shown, Is.GreaterThan(0));
            Assert.That(probed, Is.GreaterThanOrEqualTo(64 * 1024), "the measure sees an allocation");
            Assert.That(grew, Is.LessThanOrEqualTo(0), "easing the look across hours allocates nothing");
        }

        // Spindrift streams off the area's most exposed high ground: the crest of a ridge, never its flanks; the same every time.
        [Test]
        public void SpindriftComesOffTheCrestAndIsTheSameEveryTime()
        {
            const int w = 300, d = 300;
            var ridge = new int[w * d];
            for (int j = 0; j < d; j++)
                for (int i = 0; i < w; i++)
                    ridge[j * w + i] = 9000 - System.Math.Abs(i - w / 2) * 16;   // a north-south ridge, 9,000 ft on its crest
            var t = WeatherTerrain.Build(w, d, ridge, -w * 4000, -d * 4000, (0, 0, w, d), d / 2 * w + w / 2, SimWeather.TerrainTuning(Pacing.Default));
            var a = Spindrift.Points(t);
            Assert.That(a.Length, Is.GreaterThan(0).And.LessThanOrEqualTo(Spindrift.Most));
            Assert.That(Spindrift.Points(t), Is.EqualTo(a), "the same area, the same points");
            float relief = 150 * 16;
            foreach (var p in a)
            {
                Assert.That(Mathf.Abs(p.x), Is.LessThan(Spindrift.TopShare * relief / 16 * 8 + 8), "on the crest, not down its flanks");
                Assert.That(p.y, Is.GreaterThan((9000 - Spindrift.TopShare * relief - 40) * 0.3048f), "high up");
            }
            var e = Spindrift.Emitters(a);
            Assert.That(e.Length, Is.EqualTo(a.Length));
            Assert.That(e[0].Strength, Is.EqualTo(1f), "the highest the strongest");
            Assert.That(WindRibbons.ClockWrap % e[0].Life, Is.EqualTo(0).Within(1e-4), "its life divides the clock's wrap");
        }
    }
}
