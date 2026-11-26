using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using MountainPlanner.Presentation;
using MountainPlanner.Simulation;
using NUnit.Framework;
using UnityEngine;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.08 PR B (SN-13 v3): the look table. The shader's rows are the C# rows; every tint stays snow (p4-08-findings R1,
    /// R15); shine and sparkle are two things (ice shines and doesn't sparkle, corn sparkles and doesn't shine); skis mark
    /// powder most and ice least. What the snow looks like once lit is SnowLitTests'.
    /// </summary>
    public sealed class SnowLooksTests
    {
        static void Say(string line) { TestContext.Progress.WriteLine(line); TestContext.Out.WriteLine(line); }

        static float[] ShaderArray(string text, string name)
        {
            var m = Regex.Match(text, @"static const float4? " + name + @"\[12\]\s*=\s*\{(.*?)\};", RegexOptions.Singleline);
            Assert.That(m.Success, Is.True, $"Snow.hlsl declares {name}");
            string body = Regex.Replace(m.Groups[1].Value, @"//[^\n]*", "");
            var numbers = Regex.Matches(body, @"-?\d+\.\d+|-?\d+(?=[,\s)])");
            var values = new float[numbers.Count];
            for (int k = 0; k < values.Length; k++) values[k] = float.Parse(numbers[k].Value, CultureInfo.InvariantCulture);
            return values;
        }

        [Test]
        public void TheShadersTableIsTheCSharpTable()
        {
            string text = File.ReadAllText(Path.Combine(Application.dataPath, "MountainPlanner/Art/Shaders/Snow.hlsl"));
            float[] tint = ShaderArray(text, "SnowLookTint"), shine = ShaderArray(text, "SnowLookShine"),
                    sparkle = ShaderArray(text, "SnowLookSparkle"), tracks = ShaderArray(text, "SnowLookTracks");
            Assert.That(tint.Length, Is.EqualTo(48)); Assert.That(shine.Length, Is.EqualTo(48));
            Assert.That(sparkle.Length, Is.EqualTo(48)); Assert.That(tracks.Length, Is.EqualTo(12));
            Assert.That(SnowLooks.Rows.Length, Is.EqualTo(12), "a row for every SnowType");
            for (int k = 0; k < 12; k++)
            {
                var r = SnowLooks.Rows[k];
                float[] want = { r.Tint.x, r.Tint.y, r.Tint.z, r.Soft, r.Sheen, r.Gloss, r.Patch, r.Mottle, r.Sparkle, r.Density, r.Grain, r.Tracks,
                                 r.Fresh ? 1 : 0 };
                float[] got = { tint[k * 4], tint[k * 4 + 1], tint[k * 4 + 2], tint[k * 4 + 3], shine[k * 4], shine[k * 4 + 1], shine[k * 4 + 2], shine[k * 4 + 3],
                                sparkle[k * 4], sparkle[k * 4 + 1], sparkle[k * 4 + 2], tracks[k], sparkle[k * 4 + 3] };
                for (int i = 0; i < want.Length; i++) Assert.That(got[i], Is.EqualTo(want[i]).Within(1e-5f), $"{(SnowType)k} value {i}");
            }
            var age = Regex.Match(text, @"SnowLookAgeHours\s*=\s*(\d+)");
            Assert.That(float.Parse(age.Groups[1].Value, CultureInfo.InvariantCulture), Is.EqualTo(SnowLooks.AgeHours));
            var fresh = Regex.Match(text, @"SnowFreshHours\s*=\s*(\d+)");
            Assert.That(float.Parse(fresh.Groups[1].Value, CultureInfo.InvariantCulture), Is.EqualTo(SnowLooks.FreshHours));
        }

        [Test]
        public void EveryTintStaysSnow()
        {
            // R1, R15, O10: a little colour tells the family, never a stain. No channel above white, within a fifth of plain
            // snow's brightness (lit snow sits near the top of the picture's range, where a tint shows as far less), and little
            // hue (red and blue within 0.08 of each other). SnowLitTests holds the colour steps on the lit result.
            for (int k = 0; k < 12; k++)
            {
                var t = SnowLooks.Rows[k].Tint;
                float lum = 0.2126f * t.x + 0.7152f * t.y + 0.0722f * t.z;
                Assert.That(Mathf.Max(t.x, Mathf.Max(t.y, t.z)), Is.LessThanOrEqualTo(1f), $"{(SnowType)k}: never above white");
                Assert.That(lum, Is.GreaterThanOrEqualTo(0.82f), $"{(SnowType)k}: within a fifth of plain snow");
                Assert.That(Mathf.Abs(t.x - t.z), Is.LessThanOrEqualTo(0.08f), $"{(SnowType)k}: little hue");
            }
        }

        [Test]
        public void ShineAndSparkleAreTwoThings()
        {
            // Owner, 2026-11-24: sparkle is a twinkle across the snow; shiny is a reflective, glassy sheet, like ice.
            SnowLooks.Row R(SnowType t) => SnowLooks.Rows[(int)t];
            Assert.That(R(SnowType.IS).Sheen, Is.EqualTo(1f), "ice shines in full");
            Assert.That(R(SnowType.IS).Sparkle, Is.Zero, "and doesn't sparkle");
            Assert.That(R(SnowType.CO).Sheen, Is.Zero, "corn never shines");
            Assert.That(R(SnowType.CO).Sparkle, Is.GreaterThan(R(SnowType.P).Sparkle), "and its twinkle is the boldest");
            Assert.That(R(SnowType.CO).Grain, Is.GreaterThan(R(SnowType.P).Grain * 5), "from coarse crystals");
            Assert.That(R(SnowType.P).Sheen, Is.Zero, "powder never shines");
            Assert.That(R(SnowType.P).Density, Is.GreaterThanOrEqualTo(R(SnowType.CO).Density * 2), "its twinkle is the densest");
            for (int k = 1; k < 12; k++)
            {
                if (k == (int)SnowType.IS) continue;
                Assert.That(R(SnowType.IS).Sheen * R(SnowType.IS).Gloss, Is.GreaterThan(SnowLooks.Rows[k].Sheen * SnowLooks.Rows[k].Gloss), $"ice is glassier than {(SnowType)k}");
            }
            foreach (var wet in new[] { SnowType.WG, SnowType.WP })
            {
                Assert.That(R(wet).Sparkle, Is.Zero, $"{wet}: wet snow doesn't sparkle");
                Assert.That(R(wet).Sheen, Is.GreaterThan(0), $"{wet}: it has a wet gloss");
            }
        }

        [Test]
        public void SkisMarkPowderMostAndIceLeast()
        {
            float T(SnowType t) => SnowLooks.Rows[(int)t].Tracks;
            Assert.That(T(SnowType.P), Is.GreaterThan(T(SnowType.MM)));
            Assert.That(T(SnowType.MM), Is.GreaterThanOrEqualTo(T(SnowType.WP)));
            foreach (var g in new[] { SnowType.CO, SnowType.LG, SnowType.WG })
            {
                Assert.That(T(SnowType.WP), Is.GreaterThan(T(g)), $"wet powder over {g}");
                Assert.That(T(g), Is.GreaterThan(T(SnowType.PP)), $"{g} over packed powder");
            }
            Assert.That(T(SnowType.PP), Is.GreaterThan(T(SnowType.MG)));
            Assert.That(T(SnowType.MG), Is.GreaterThan(T(SnowType.HP)));
            Assert.That(T(SnowType.HP), Is.GreaterThan(T(SnowType.FG)));
            Assert.That(T(SnowType.FG), Is.GreaterThan(T(SnowType.IS)));
            Assert.That(T(SnowType.IS), Is.GreaterThan(0).And.LessThan(0.05f), "ice nearly unmarked");
        }

        [Test]
        public void TheSnowBeforeTheFieldIsPlain()
        {
            var none = SnowLooks.Rows[0];
            Assert.That(none.Tint, Is.EqualTo(Vector3.one), "no tint");
            Assert.That(none.Sheen, Is.Zero, "no shine");
            Assert.That(none.Tracks, Is.Zero);
            Say($"[looks] plain snow: soft {none.Soft}, sparkle {none.Sparkle} at density {none.Density}");
        }
    }
}
