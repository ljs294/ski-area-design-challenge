using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MountainPlanner.Presentation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.08 G1 (owner 2026-11-25: the groomed texture noticeable from further away): the corduroy's light. The shader's
    /// numbers are the C# twin's; the light falls the way it does on groomed runs in photos (darker toward the sun and in a
    /// raking sun, a little brighter with the sun behind the eye, never black, never far above smooth snow); every pass is
    /// the same, with no line drawn where two meet. What it looks like on screen is the
    /// gate stills' (measured steps), not this.
    /// </summary>
    public sealed class CorduroyLightTests
    {
        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        static readonly Vector3 Up = Vector3.up, Across = Vector3.right;

        /// <summary>A direction <paramref name="elevation"/> degrees above the ground, <paramref name="fromRidges"/> degrees
        /// round from the ridges' own line (0 along them, 90 across them toward +x).</summary>
        static Vector3 Dir(float elevation, float fromRidges)
        {
            float e = elevation * Mathf.Deg2Rad, a = fromRidges * Mathf.Deg2Rad;
            return new Vector3(Mathf.Cos(e) * Mathf.Sin(a), Mathf.Sin(e), Mathf.Cos(e) * Mathf.Cos(a));
        }

        static float Sun(float sunUp, float sunRound, float eyeUp, float eyeRound) =>
            CorduroyLight.Sun(Up, Across, Dir(sunUp, sunRound), Dir(eyeUp, eyeRound), CorduroyLight.Slope(1));

        [Test]
        public void TheShadersNumbersAreTheCSharpOnes()
        {
            string text = File.ReadAllText(Path.Combine(Application.dataPath, "MountainPlanner/Art/Shaders/SnowLighting.hlsl"));
            float Shader(string name)
            {
                var m = Regex.Match(text, @"\b" + name + @"\s*=\s*(-?\d+(?:\.\d+)?)");
                Assert.That(m.Success, Is.True, $"SnowLighting.hlsl declares {name}");
                return float.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            }
            Assert.That(Shader("CordSpacingMetres"), Is.EqualTo(CorduroyLight.SpacingMetres).Within(1e-6f));
            Assert.That(Shader("CordDepthMetres"), Is.EqualTo(CorduroyLight.DepthMetres).Within(1e-6f));
            Assert.That(Shader("CordBounce"), Is.EqualTo(CorduroyLight.Bounce).Within(1e-6f));
            Assert.That(Shader("CordSunMax"), Is.EqualTo(CorduroyLight.SunMax).Within(1e-6f));
            Assert.That(Shader("CordSkyLoss"), Is.EqualTo(CorduroyLight.SkyLoss).Within(1e-6f));
            string terrain = File.ReadAllText(Path.Combine(Application.dataPath, "MountainPlanner/Art/Shaders/MountainTerrain.shader"));
            // O-G1-1 (owner 2026-11-25, still 05: "the big white lines that break up the corduroy"; "remove the line entirely"):
            // no join strip is drawn where two passes meet.
            Assert.That(terrain, Does.Not.Contain("CordJoin").And.Not.Contain("StripCover"), "no line where two passes meet");
            // O-G1-2 (owner 2026-11-25: "I don't like the color difference between the passes, please make them the same").
            Assert.That(terrain, Does.Not.Contain("CorduroyPassSun").And.Not.Contain("_TrailParams.w"), "every pass the same");
            Assert.That(text, Does.Contain("lerp(1, sun, 1 - smoothstep(0.5, 0.9, cover))"), "CorduroyUnderCloud is UnderCloud's twin");
            Assert.That(terrain, Does.Contain("CorduroyUnderCloud(cordSun, _WxCloud.x"), "the terrain fades the groove light under cloud");
        }

        [Test]
        public void UnderAClosedDeckTheGroovesShadeNoSun()
        {
            // The gate's storm still (2026-11-25): the groove light darkened a groomed run 8 L under a closed deck, where real
            // corduroy all but vanishes. The grooves shade only the direct sun, which goes as the sun disc does.
            float toward = 0.55f;
            Assert.That(CorduroyLight.UnderCloud(toward, 0), Is.EqualTo(toward).Within(1e-6f), "a clear sky: all of it");
            Assert.That(CorduroyLight.UnderCloud(toward, 0.5f), Is.EqualTo(toward).Within(1e-6f), "broken cloud: still all of it");
            Assert.That(CorduroyLight.UnderCloud(toward, 0.9f), Is.EqualTo(1).Within(1e-6f), "a closed deck: none");
            Assert.That(CorduroyLight.UnderCloud(1.1f, 1), Is.EqualTo(1).Within(1e-6f), "nor any brightening");
            float half = CorduroyLight.UnderCloud(toward, 0.7f);
            Assert.That(half, Is.GreaterThan(toward).And.LessThan(1), "in between, part of it");
        }

        [Test]
        public void TheLightFallsAsOnGroomedRunsInPhotos()
        {
            // An eye 35 degrees above the run (a chairlift's), a winter sun at 15, 25 and 40 degrees.
            foreach (float sunUp in new[] { 15f, 25f, 40f })
            {
                float behind = Sun(sunUp, 90, 35, 90), toward = Sun(sunUp, 90, 35, -90), crossLook = Sun(sunUp, 90, 35, 0),
                      along = Sun(sunUp, 0, 35, 90);
                Say(FormattableString($"[cord] sun {sunUp}: behind the eye {behind:F2}, toward the eye {toward:F2}, across the ridges seen along them {crossLook:F2}, along the ridges {along:F2}"));
                Assert.That(behind, Is.GreaterThanOrEqualTo(1f), $"sun {sunUp}: with the sun behind, corduroy is no darker than smooth snow");
                Assert.That(toward, Is.LessThan(0.7f), $"sun {sunUp}: looking toward the sun, the shaded faces show");
                Assert.That(toward, Is.LessThan(crossLook), $"sun {sunUp}: toward the sun darker than from the side");
                Assert.That(along, Is.InRange(0.85f, 1f), $"sun {sunUp}: a sun along the ridges leaves only the faces' slope");
            }
            // Toward the sun, a raking sun shades more than a high one. (Seen from the side the shade and the sunlit faces
            // balance: the same light falls on the ridges as on smooth snow, and only the faces' slope is left.)
            Assert.That(Sun(15, 90, 35, -90), Is.LessThan(Sun(60, 90, 35, -90)));
        }

        [Test]
        public void NeverBlackNorFarAboveSmoothSnow()
        {
            float lo = float.MaxValue, hi = float.MinValue;
            for (float sunUp = 5; sunUp < 90; sunUp += 10)
                for (float sunRound = 0; sunRound < 360; sunRound += 30)
                    for (float eyeUp = 10; eyeUp < 90; eyeUp += 10)
                        for (float eyeRound = 0; eyeRound < 360; eyeRound += 45)
                        {
                            float f = Sun(sunUp, sunRound, eyeUp, eyeRound);
                            lo = Mathf.Min(lo, f); hi = Mathf.Max(hi, f);
                        }
            Say($"[cord] over every sun and eye: {lo:F2} to {hi:F2}");
            Assert.That(lo, Is.GreaterThanOrEqualTo(CorduroyLight.Bounce - 1e-4f), "the bounce keeps the grooves from going black");
            Assert.That(hi, Is.LessThanOrEqualTo(CorduroyLight.SunMax + 1e-4f));
        }

        [Test]
        public void CloseRidgesShadeTheShareTheFarLightDoes()
        {
            float k = CorduroyLight.Slope(1);
            foreach (float sunUp in new[] { 10f, 20f, 30f })
            {
                Vector3 sun = Dir(sunUp, 90);
                float ts = Mathf.Tan(sunUp * Mathf.Deg2Rad), mean = 0;
                const int Steps = 2000;
                for (int s = 0; s < Steps; s++) mean += CorduroyLight.Shade((s + 0.5f) / Steps, 0.001f, Up, Across, sun, k) / Steps;
                float want = ts >= k ? 0 : k / (k + ts);
                Say($"[cord] close ridges, sun {sunUp} across: shaded {mean:F3} of each ridge (the far light's {want:F3})");
                Assert.That(mean, Is.EqualTo(want).Within(0.01f));
                // Still the same share as the ridges shrink toward the hand-over (half a ridge a pixel): no ring there.
                float wide = 0;
                for (int s = 0; s < Steps; s++) wide += CorduroyLight.Shade((s + 0.5f) / Steps, 0.4f, Up, Across, sun, k) / Steps;
                Assert.That(wide, Is.EqualTo(want).Within(0.03f), $"sun {sunUp}: the softened edges keep the mean");
            }
            Assert.That(CorduroyLight.Shade(0.3f, 0.01f, Up, Across, Dir(60, 90), k), Is.EqualTo(0), "a high sun shades no groove");
        }

        static string FormattableString(System.FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
    }
}
