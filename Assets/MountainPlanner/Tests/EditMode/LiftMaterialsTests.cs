using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace MountainPlanner.Tests
{
    // The lift glasses (owner C16, C17): LiftGlass (75 % face-on) and the carpet gallery's clearer LiftGlassClear (45 %)
    // as LiftImport makes them, and LiftMaterials' runtime copies: one per glass and accent colour, the accent set on
    // each, shared by lifts in the same accent; the structure copies as before.
    public sealed class LiftMaterialsTests
    {
        const string Materials = "Assets/MountainPlanner/Art/Lifts/Materials";
        static readonly int GlassAlpha = Shader.PropertyToID("_GlassAlpha");
        static readonly int Accent = Shader.PropertyToID("_AccentColor");
        static readonly int Livery = Shader.PropertyToID("_LiveryColor");

        Material _structure, _glass, _clear, _chair;
        LiftMaterials _materials;

        [SetUp]
        public void SetUp()
        {
            _structure = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftStructure.mat");
            _glass = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftGlass.mat");
            _clear = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftGlassClear.mat");
            _chair = AssetDatabase.LoadAssetAtPath<Material>($"{Materials}/LiftChair.mat");
            Assert.That(_structure, Is.Not.Null);
            Assert.That(_glass, Is.Not.Null);
            Assert.That(_clear, Is.Not.Null, "LiftImport makes LiftGlassClear.mat");
            _materials = new LiftMaterials(_structure, _glass, _chair);
        }

        [TearDown]
        public void TearDown() => _materials?.Dispose();

        [Test]
        public void TheImportedGlassesHaveTheirOpacity()
        {
            foreach (var (m, alpha) in new[] { (_glass, 0.75f), (_clear, 0.45f) })
            {
                Assert.That(m.GetFloat(GlassAlpha), Is.EqualTo(alpha).Within(1e-4f), m.name);
                Assert.That(m.IsKeywordEnabled("_LIFT_GLASS"), Is.True, m.name);
                Assert.That(m.renderQueue, Is.EqualTo((int)UnityEngine.Rendering.RenderQueue.Transparent), m.name);
                Assert.That(m.shader, Is.EqualTo(_structure.shader), m.name);
            }
        }

        [Test]
        public void EachGlassGetsACopyPerAccent()
        {
            var red = new Color(0.72f, 0.12f, 0.09f);
            var blue = new Color(0.61f, 0.78f, 0.91f);
            var clearRed = _materials.GlassFor(_clear, red);
            Assert.That(clearRed, Is.Not.Null.And.Not.EqualTo(_clear));
            Assert.That(_materials.GlassFor(_clear, red), Is.SameAs(clearRed), "lifts in one accent share a copy");
            var clearBlue = _materials.GlassFor(_clear, blue);
            Assert.That(clearBlue, Is.Not.SameAs(clearRed));
            Assert.That((Color32)clearRed.GetColor(Accent), Is.EqualTo((Color32)red));
            Assert.That((Color32)clearBlue.GetColor(Accent), Is.EqualTo((Color32)blue));
            Assert.That(clearRed.GetFloat(GlassAlpha), Is.EqualTo(0.45f).Within(1e-4f), "the clear glass keeps its opacity");

            var hoodRed = _materials.GlassFor(_glass, red);
            Assert.That(hoodRed, Is.Not.SameAs(clearRed));
            Assert.That(hoodRed.GetFloat(GlassAlpha), Is.EqualTo(0.75f).Within(1e-4f), "the hood glass keeps its opacity");
            Assert.That(_materials.GlassFor(_structure, red), Is.Null, "the structure is not glass");
            Assert.That(_materials.GlassFor(clearRed, blue), Is.Null, "a runtime copy is never copied again");
        }

        [Test]
        public void ApplyPutsEachGlassInTheLiftsAccent()
        {
            var go = new GameObject("glass test");
            try
            {
                var r = go.AddComponent<MeshRenderer>();
                r.sharedMaterials = new[] { _structure, _glass, _clear };
                var livery = new LiftLivery(new Color(0.2f, 0.4f, 0.8f), new Color(0.72f, 0.12f, 0.09f));
                _materials.Apply(go, livery);
                var m = r.sharedMaterials;
                Assert.That(m[0], Is.SameAs(_materials.StructureFor(livery)));
                Assert.That((Color32)m[0].GetColor(Livery), Is.EqualTo((Color32)livery.Primary));
                Assert.That(m[1], Is.SameAs(_materials.GlassFor(_glass, livery.Accent)));
                Assert.That(m[2], Is.SameAs(_materials.GlassFor(_clear, livery.Accent)));
                Assert.That((Color32)m[2].GetColor(Accent), Is.EqualTo((Color32)livery.Accent));
            }
            finally
            {
                Object.DestroyImmediate(go);
            }
        }
    }
}
