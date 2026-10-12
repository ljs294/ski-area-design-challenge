using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-03.1: the brand's lockups and marks (written by tools/brand/export.py) are white single-fill vectors that
    /// import as UI Toolkit vector images keeping their whole viewBox, and every one the flow's stylesheet names exists.
    /// </summary>
    public sealed class BrandTests
    {
        const string Folder = "Assets/MountainPlanner/Art/UI/Brand/Resources/Brand";
        const string FlowUss = "Assets/MountainPlanner/Art/UI/Flow/Resources/MountainPlannerFlow/Flow.uss";

        static string[] Svgs() => Directory.GetFiles(Folder, "*.svg").Select(p => p.Replace('\\', '/')).ToArray();

        [Test]
        public void TheLockupsAndMarksAreThere()
        {
            var names = Svgs().Select(Path.GetFileNameWithoutExtension).ToArray();
            Assert.That(names, Is.SupersetOf(new[] { "game-lockup", "game-mark", "studio-lockup", "studio-mark" }));
        }

        /// <summary>One white fill and nothing else, so USS tints the whole shape (no strokes, opacity, gradients or text).</summary>
        [Test]
        public void EachIsOneWhiteFill()
        {
            foreach (var path in Svgs())
            {
                string svg = File.ReadAllText(path);
                var fills = Regex.Matches(svg, "fill=\"([^\"]+)\"").Cast<Match>().Select(m => m.Groups[1].Value.ToLowerInvariant()).Distinct().ToArray();
                Assert.That(fills, Is.EqualTo(new[] { "#ffffff" }), path);
                Assert.That(Regex.IsMatch(svg, "stroke|opacity|Gradient|<text|<image|<filter|<mask"), Is.False, path);
            }
        }

        /// <summary>They import as vector images the size of their viewBox (HudIconImport keeps the viewport), so a USS
        /// box with the viewBox's proportions shows the whole lockup with no stray margin.</summary>
        [Test]
        public void EachImportsAsAVectorImageTheSizeOfItsViewBox()
        {
            foreach (var path in Svgs())
            {
                var image = AssetDatabase.LoadAssetAtPath<VectorImage>(path);
                Assert.That(image, Is.Not.Null, path);
                var box = Regex.Match(File.ReadAllText(path), "viewBox=\"([^\"]+)\"").Groups[1].Value.Split(' ')
                    .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                Assert.That(image.width / image.height, Is.EqualTo(box[2] / box[3]).Within(0.01f), path);
            }
        }

        /// <summary>Every resource("Brand/…") in the flow's stylesheet names a file here, and its box keeps the file's
        /// proportions (within 3%), so scale-to-fit leaves no visible gap.</summary>
        [Test]
        public void TheStylesheetNamesOnlyBrandFilesThatExist()
        {
            string uss = File.ReadAllText(FlowUss);
            var rules = Regex.Matches(uss, @"\.([a-z-]+)\s*\{[^}]*resource\(""Brand/([a-z0-9-]+)""\)[^}]*\}").Cast<Match>().ToArray();
            Assert.That(rules.Length, Is.GreaterThanOrEqualTo(4));
            foreach (var rule in rules)
            {
                string file = $"{Folder}/{rule.Groups[2].Value}.svg";
                Assert.That(File.Exists(file), Is.True, file);
                float w = float.Parse(Regex.Match(rule.Value, @"\bwidth:\s*([\d.]+)px").Groups[1].Value, CultureInfo.InvariantCulture);
                float h = float.Parse(Regex.Match(rule.Value, @"\bheight:\s*([\d.]+)px").Groups[1].Value, CultureInfo.InvariantCulture);
                var box = Regex.Match(File.ReadAllText(file), "viewBox=\"([^\"]+)\"").Groups[1].Value.Split(' ')
                    .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
                Assert.That(w / h, Is.EqualTo(box[2] / box[3]).Within(box[2] / box[3] * 0.03f), $".{rule.Groups[1].Value}");
            }
        }
    }
}
