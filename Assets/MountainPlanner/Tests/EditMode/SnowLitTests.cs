using System;
using System.IO;
using System.Text.RegularExpressions;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.08 PR B (SN-13 v3): the snow as lit, through the real SnowLighting.hlsl (Shaders/SnowLitProbe.shader), not the
    /// table's numbers (p4-08-findings R20). No type is told by its colour, in sun or in shade (R1, R2); ice mirrors the sky
    /// more the lower the eye and has a broad glare (R12); corn sparkles and never shines, and nothing sparkles in shade
    /// (R13); the sparkle holds still under a small camera move; and the include takes no screen derivatives (R17).
    /// </summary>
    public sealed class SnowLitTests
    {
        const int Size = 128, Wide = 512;   // Wide: enough snow to count sparkling points on
        const float PixelAngle = 0.00107f;   // 1080p at the default 60 degree view
        static readonly int Rect = Shader.PropertyToID("_ProbeRect"), Look = Shader.PropertyToID("_ProbeLook"), Normal = Shader.PropertyToID("_ProbeNormal"),
                            View = Shader.PropertyToID("_ProbeView"), Sun = Shader.PropertyToID("_ProbeSun"), SunLight = Shader.PropertyToID("_ProbeSunLight"),
                            Ambient = Shader.PropertyToID("_ProbeAmbient"), Albedo = Shader.PropertyToID("_ProbeAlbedo"),
                            Zenith = Shader.PropertyToID("_SnowSkyZenith"), Horizon = Shader.PropertyToID("_SnowSkyHorizon"), Sparkle = Shader.PropertyToID("_SnowSparkle");
        // Noon's own light (LightingPreset.Noon with the Bluebird grade), linear.
        static readonly Vector3 NoonSun = new Vector3(1.55f, 1.43f, 1.28f), NoonAmbient = new Vector3(0.115f, 0.216f, 0.42f) * 0.9f,
                                SnowAlbedo = new Vector3(0.88f, 0.91f, 0.95f);
        static readonly Vector4 ClearZenith = new Vector4(0.077f, 0.274f, 0.659f, 1), ClearHorizon = new Vector4(0.716f, 0.831f, 0.930f, 1);

        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        sealed class Shot
        {
            public SnowType Type;
            public float Age, Distance = 30, SunElevation = 25, SunAzimuth = 90, EyeElevation = 40, EyeAzimuth = 0, West, South;
            public Vector3 SunLight = NoonSun, Ambient = NoonAmbient;
            public bool Sparkles = true;
            public Vector4 SkyZenith = ClearZenith, SkyHorizon = ClearHorizon;
        }

        /// <summary>A unit vector at an elevation above the snow and an azimuth round it (degrees; azimuth 0 is +z).</summary>
        static Vector3 Toward(float elevation, float azimuth)
        {
            float e = elevation * Mathf.Deg2Rad, a = azimuth * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(e) * Mathf.Sin(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Cos(a));
        }

        [SetUp]
        public void NeedsAGraphicsDevice()
        {
            if (SystemInfo.graphicsDeviceType == GraphicsDeviceType.Null) Assert.Ignore("needs a graphics device (not -nographics)");
        }

        [TearDown]
        public void LeaveNoSparkleBehind() => Shader.SetGlobalVector(Sparkle, Vector4.zero);

        static Color[] Draw(int pass, Shot s, int Size = Size)
        {
            var shader = Shader.Find("Hidden/MountainPlanner/Tests/SnowLitProbe");
            Assert.That(shader, Is.Not.Null, "the lit probe shader");
            var material = new Material(shader);
            var rt = RenderTexture.GetTemporary(Size, Size, 0, RenderTextureFormat.ARGBFloat, RenderTextureReadWrite.Linear);
            var read = new Texture2D(Size, Size, TextureFormat.RGBAFloat, false, true);
            var was = RenderTexture.active;
            try
            {
                float px = s.Distance * PixelAngle;
                Shader.SetGlobalVector(Zenith, s.SkyZenith);
                Shader.SetGlobalVector(Horizon, s.SkyHorizon);
                Shader.SetGlobalVector(Sparkle, new Vector4(s.Sparkles ? 1 : 0, WeatherViewReach, PixelAngle, 0));
                material.SetVector(Rect, new Vector4(s.West, s.South, Size * px, Size * px));
                material.SetVector(Look, new Vector4((int)s.Type, s.Age, s.Distance, px));
                material.SetVector(Normal, Vector3.up);
                material.SetVector(View, Toward(s.EyeElevation, s.EyeAzimuth));
                material.SetVector(Sun, Toward(s.SunElevation, s.SunAzimuth));
                material.SetVector(SunLight, s.SunLight);
                material.SetVector(Ambient, s.Ambient);
                material.SetVector(Albedo, new Vector4(SnowAlbedo.x, SnowAlbedo.y, SnowAlbedo.z, 1));
                Graphics.Blit(null, rt, material, pass);
                RenderTexture.active = rt;
                read.ReadPixels(new Rect(0, 0, Size, Size), 0, 0);
                read.Apply();
                return read.GetPixels();
            }
            finally
            {
                RenderTexture.active = was;
                RenderTexture.ReleaseTemporary(rt);
                UnityEngine.Object.DestroyImmediate(read);
                UnityEngine.Object.DestroyImmediate(material);
            }
        }

        const float WeatherViewReach = 2500;   // WeatherView.GlintReach
        // The families' mean colour steps from powder, in the lit model's own steps (Lab below): packed and groomed barely off
        // it; the two coloured families clearly but gently, in the sun and in shade; ice in the sun as its glassy patches
        // were accepted in B1a; and the two coloured families this far apart in hue (Lab's blue-yellow axis).
        const float PackedSunMax = 3.5f, PackedShadeMax = 1.5f, ColouredSunMin = 2.5f, ColouredSunMax = 5.5f,
                    ColouredShadeMin = 0.5f, ColouredShadeMax = 4, IceSunMax = 9, FamilyHue = 1;

        static Vector3 Mean(Color[] px)
        {
            double r = 0, g = 0, b = 0;
            foreach (var c in px) { r += c.r; g += c.g; b += c.b; }
            return new Vector3((float)(r / px.Length), (float)(g / px.Length), (float)(b / px.Length));
        }

        static float Lum(Vector3 c) => 0.2126f * c.x + 0.7152f * c.y + 0.0722f * c.z;
        static float Lum(Color c) => 0.2126f * c.r + 0.7152f * c.g + 0.0722f * c.b;

        /// <summary>About what the game shows for a linear colour: noon's grade (SceneLighting: +0.3 EV, contrast in LogC as URP
        /// applies it, the preset's and the look style's together about 1.25), URP's Neutral tone map, then CIE Lab. The real
        /// picture's steps are measured in the built game: on 2026-11-25 its stills showed about 1.35 times this model's step
        /// for the tinted types (the grade's saturation and the view are not modelled). An ACES curve here, as before, put lit
        /// snow on a shoulder the game doesn't have and read 2.5 times too little.</summary>
        static Vector3 Lab(Vector3 linear)
        {
            float Curve(float x) => (x * (0.2f * x + 0.24f * 0.29f) + 0.272f * 0.02f) / (x * (0.2f * x + 0.29f) + 0.272f * 0.3f) - 0.02f / 0.3f;
            float Tone(float x)
            {
                x = Mathf.Max(0, x * 1.23f);
                float log = Mathf.Log10(5.555556f * x + 0.047996f) * 0.244161f + 0.386036f;
                log = (log - 0.4135884f) * 1.25f + 0.4135884f;
                x = Mathf.Max(0, (Mathf.Pow(10, (log - 0.386036f) / 0.244161f) - 0.047996f) / 5.555556f);
                float white = 1 / Curve(5.3f);
                return Mathf.Clamp01(Curve(x * white) * white);
            }
            float r = Tone(linear.x), g = Tone(linear.y), b = Tone(linear.z);
            float X = (0.4124f * r + 0.3576f * g + 0.1805f * b) / 0.9505f, Y = 0.2126f * r + 0.7152f * g + 0.0722f * b, Z = (0.0193f * r + 0.1192f * g + 0.9505f * b) / 1.089f;
            float F(float t) => t > 0.008856f ? Mathf.Pow(t, 1f / 3) : 7.787f * t + 16f / 116;
            return new Vector3(116 * F(Y) - 16, 500 * (F(X) - F(Y)), 200 * (F(Y) - F(Z)));
        }

        static float Step(Vector3 a, Vector3 b) => (Lab(a) - Lab(b)).magnitude;

        [Test]
        public void ALittleColourTellsTheFamilies()
        {
            // O10 (owner, 2026-11-25): colour and reflectivity tell the snow apart, colour at about a third of round 1's strength
            // (its strongest step was 12.7 on a still, so about 4 to 5 there). Four families, each a mean colour step from powder,
            // away from the sun's glare, close and from far away: packed and groomed barely off powder; hard and icy, and spring
            // and wet, clearly but gently, and those two apart in hue (hard and icy bluer). The limits are this model's (Lab):
            // the built game showed about 1.35 times its step for the tinted types. Part of a type's step in the sun is how
            // softly the sun wraps it (powder the most), so the sun's limits sit above the shade's. Ice keeps its glassy
            // patches' darker, cooler snow from B1a, or a sheet seen from above would not show. The eye looks across the sun,
            // 40 degrees down; the numbers are means over the glassy patches and the snow between them.
            var families = new (string Name, SnowType[] Types, bool Coloured)[]
            {
                ("packed and groomed", new[] { SnowType.PP, SnowType.MG, SnowType.MM }, false),
                ("hard and icy", new[] { SnowType.HP, SnowType.FG, SnowType.IS }, true),
                ("spring and wet", new[] { SnowType.CO, SnowType.LG, SnowType.WP, SnowType.WG }, true),
            };
            var outside = new System.Collections.Generic.List<string>();
            foreach (float distance in new[] { 8f, 600f })
                for (int lit = 0; lit < 2; lit++)
                {
                    Vector3 light = lit == 0 ? NoonSun : Vector3.zero;
                    string where = $"{(lit == 0 ? "in the sun" : "in shade")} at {distance} m";
                    var powder = Mean(Draw(0, new Shot { Type = SnowType.P, Sparkles = false, SunLight = light, Distance = distance }));
                    var report = new System.Text.StringBuilder($"[lit] colour step from powder, {where}:");
                    var linear = new System.Text.StringBuilder($"[lit] linear means, {where}: P {powder.x:F4} {powder.y:F4} {powder.z:F4}");
                    var blue = new float[families.Length];
                    for (int f = 0; f < families.Length; f++)
                        foreach (var type in families[f].Types)
                        {
                            var c = Mean(Draw(0, new Shot { Type = type, Sparkles = false, SunLight = light, Distance = distance }));
                            float s = Step(c, powder);
                            report.Append($" {type} {s:F1}");
                            linear.Append($" {type} {c.x:F4} {c.y:F4} {c.z:F4}");
                            blue[f] += (Lab(powder).z - Lab(c).z) / families[f].Types.Length;   // how far toward blue
                            bool ice = type == SnowType.IS;
                            float min = !families[f].Coloured ? 0 : lit == 0 ? ColouredSunMin : ColouredShadeMin;
                            float max = !families[f].Coloured ? (lit == 0 ? PackedSunMax : PackedShadeMax)
                                      : lit == 0 ? (ice ? IceSunMax : ColouredSunMax) : ColouredShadeMax;
                            if (s < min || s > max) outside.Add($"{type} ({families[f].Name}) {where}: {s:F2}, not {min} to {max}");
                        }
                    Say(report.ToString());
                    Say(linear.ToString());
                    Say($"[lit] toward blue, {where}: hard and icy {blue[1]:F2}, spring and wet {blue[2]:F2}");
                    if (blue[1] <= blue[2] + FamilyHue) outside.Add($"hard and icy is not bluer than spring and wet by {FamilyHue} {where}");
                }
            Assert.That(outside, Is.Empty, "every type inside its family's colour step, the two coloured families apart in hue");
        }

        [Test]
        public void IceMirrorsTheSkyMoreTheLowerTheEye()
        {
            // R12: a sheen, not a hot spot. In shade (no sun at all) ice still mirrors the sky, more and more as the eye
            // drops: blue under a clear sky, grey under cloud.
            float[] elevations = { 64, 30, 14.5f, 5.7f };   // the eye 0.9, 0.5, 0.25 and 0.1 of straight down
            var shine = new float[elevations.Length];
            Vector3 low = default;
            for (int k = 0; k < elevations.Length; k++)
            {
                var c = Mean(Draw(1, new Shot { Type = SnowType.IS, EyeElevation = elevations[k], SunLight = Vector3.zero }));
                shine[k] = Lum(c);
                if (k == 2) low = c;
                if (k > 0) Assert.That(shine[k], Is.GreaterThan(shine[k - 1]), $"more mirrored sky at {elevations[k]} degrees than at {elevations[k - 1]}");
            }
            Say($"[lit] ice's mirrored sky by the eye's height: {shine[0]:F4} {shine[1]:F4} {shine[2]:F4} {shine[3]:F4}; at 14.5 degrees {low}");
            Assert.That(shine[2], Is.GreaterThanOrEqualTo(4 * shine[0]), "four times the sheen at a low angle");
            Assert.That(low.z, Is.GreaterThan(low.x * 1.15f), "blue under a clear sky: the sky's own colour");
            var grey = new Vector4(0.42f, 0.44f, 0.46f, 1);
            var cloud = Mean(Draw(1, new Shot { Type = SnowType.IS, EyeElevation = 14.5f, SunLight = Vector3.zero, SkyZenith = grey, SkyHorizon = grey }));
            Assert.That((cloud.z - cloud.x) / cloud.z, Is.LessThan(0.1f), "a grey sheet under cloud");
            var corn = Mean(Draw(1, new Shot { Type = SnowType.CO, EyeElevation = 5.7f }));
            Assert.That(Lum(corn), Is.Zero, "corn never shines, at any angle");
            var powder = Mean(Draw(1, new Shot { Type = SnowType.P, EyeElevation = 5.7f }));
            Assert.That(Lum(powder), Is.Zero, "nor powder");
        }

        [Test]
        public void IcesGlareTowardTheSunIsBroad()
        {
            // R12: round 1's highlight was 2.5 degrees wide. Looking toward a 25 degree sun, the eye sweeps through the
            // mirror angle: the glare stays above half its peak over 12 degrees or more, and ice's is the strongest.
            float Glare(SnowType type, float eye) => Lum(Mean(Draw(1, new Shot { Type = type, EyeElevation = eye, EyeAzimuth = 270, SkyZenith = Vector4.zero, SkyHorizon = Vector4.zero })));
            float peak = 0, sum = 0, width = 0;
            const float StepDegrees = 1;
            var values = new System.Collections.Generic.List<float>();
            for (float eye = 5; eye <= 60; eye += StepDegrees) { float g = Glare(SnowType.IS, eye); values.Add(g); peak = Mathf.Max(peak, g); sum += g; }
            foreach (float g in values) if (g >= peak * 0.5f) width += StepDegrees;
            Say($"[lit] ice's glare: peak {peak:F2}, {width:F0} degrees above half of it, sum {sum:F1}");
            Assert.That(peak, Is.GreaterThan(1f), "a glare brighter than lit snow");
            Assert.That(width, Is.GreaterThanOrEqualTo(12f), "broad, not a hot spot");
            for (int k = 1; k < 12; k++)
            {
                if (k == (int)SnowType.IS) continue;
                float other = 0;
                for (float eye = 5; eye <= 60; eye += 5) other += Glare((SnowType)k, eye);
                Assert.That(sum * StepDegrees / 5, Is.GreaterThan(other), $"ice's glare over {(SnowType)k}'s");
            }
        }

        static float Lit(Color[] px, float above)
        {
            int n = 0;
            foreach (var c in px) if (Lum(c) > above) n++;
            return n / (float)px.Length;
        }

        [Test]
        public void CornSparklesAndNothingSparklesInShade()
        {
            // R13 and the owner's rule: sparkle is a twinkle across the snow, not a reflection. Corn has bright points in the
            // sun and none in shade; powder has many more, finer ones; ice and wet snow have none.
            var corn = Draw(2, new Shot { Type = SnowType.CO, EyeElevation = 30, EyeAzimuth = 270 }, Wide);
            var powder = Draw(2, new Shot { Type = SnowType.P, EyeElevation = 30, EyeAzimuth = 270 }, Wide);
            float cornLit = Lit(corn, 0.5f), powderLit = Lit(powder, 0.5f);
            Say($"[lit] sparkling pixels toward the sun at 30 m: corn {cornLit:P2}, powder {powderLit:P2}; brightest corn {MaxLum(corn):F1}, powder {MaxLum(powder):F1}");
            Assert.That(cornLit, Is.InRange(0.00005f, 0.02f), "corn: a few bright points");
            Assert.That(powderLit, Is.GreaterThanOrEqualTo(2 * cornLit), "powder: at least twice as much of the snow twinkles");
            Assert.That(MaxLum(corn), Is.GreaterThan(3f), "each point far brighter than lit snow");   // how bold each is: the table's (SnowLooksTests)
            var shade = Draw(2, new Shot { Type = SnowType.CO, EyeElevation = 30, EyeAzimuth = 270, SunLight = Vector3.zero }, Wide);
            Assert.That(MaxLum(shade), Is.Zero, "no sparkle without the sun");
            foreach (var none in new[] { SnowType.IS, SnowType.WG, SnowType.WP })
                Assert.That(MaxLum(Draw(2, new Shot { Type = none, EyeElevation = 30, EyeAzimuth = 270 }, Wide)), Is.Zero, $"{none} doesn't sparkle");
            var old = Draw(2, new Shot { Type = SnowType.P, Age = 96, EyeElevation = 30, EyeAzimuth = 270 }, Wide);
            Assert.That(MaxLum(old), Is.LessThan(MaxLum(powder) * 0.3f), "four-day-old snow has lost most of its sparkle");
        }

        static float MaxLum(Color[] px) { float m = 0; foreach (var c in px) m = Mathf.Max(m, Lum(c)); return m; }

        [Test]
        public void TheSparkleHoldsStillUnderASmallMove()
        {
            // A third of a pixel's shift moves the picture, not the amount of light: the points are a pixel wide or more.
            foreach (float distance in new[] { 8f, 30f, 150f, 600f })
            {
                float px = distance * PixelAngle;
                double a = 0, b = 0;
                foreach (var c in Draw(2, new Shot { Type = SnowType.P, Distance = distance, EyeElevation = 30, EyeAzimuth = 270 }, Wide)) a += Lum(c);
                foreach (var c in Draw(2, new Shot { Type = SnowType.P, Distance = distance, EyeElevation = 30, EyeAzimuth = 270, West = 0.37f * px, South = 0.21f * px }, Wide)) b += Lum(c);
                Say($"[lit] powder's sparkle at {distance} m: {a:F1} and {b:F1} after a third of a pixel");
                Assert.That(a, Is.GreaterThan(0), $"sparkle at {distance} m");
                Assert.That(Math.Abs(a - b) / a, Is.LessThan(0.1), $"within a tenth at {distance} m");
            }
        }

        [Test]
        public void TheSnowsLightTakesNoScreenDerivatives()
        {
            // R17: the terrain calls it inside branches, and the probe draws any distance by passing a footprint.
            string text = File.ReadAllText(Path.Combine(Application.dataPath, "MountainPlanner/Art/Shaders/SnowLighting.hlsl"));
            string code = Regex.Replace(text, @"//[^\n]*", "");
            foreach (string banned in new[] { @"\bddx\b", @"\bddy\b", @"\bfwidth\b", @"SAMPLE_TEXTURE2D\(", @"SAMPLE_TEXTURE2D_ARRAY\(", @"\.Sample\(" })
                Assert.That(Regex.IsMatch(code, banned), Is.False, $"SnowLighting.hlsl uses {banned}");
        }
    }
}
