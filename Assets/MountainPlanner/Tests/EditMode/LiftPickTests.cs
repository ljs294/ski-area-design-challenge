using System;
using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools.Constraints;
using Is = UnityEngine.TestTools.Constraints.Is;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    // Task 09 (LT23): pointing at any part of a built lift picks it, beside it doesn't, the ground in front hides it, and
    // lighting it marks every one of its renderers for the outline pass.
    public sealed class LiftPickTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;
        const float Slack = 0.004f;   // ~8 px at 1080p and a 60° field of view

        sealed class Slope : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * (y - (North - 900));
        }

        LiftView _view;
        Material _material;

        [SetUp]
        public void Build()
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            var quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var natural = new EditedHeights(new Slope(), new EditLattice(West, North), TerrainEdits.Empty);
            var b = LiftBuild.Build(quad, natural, West + 100, North - 900, West + 100, North - 100, TowerStance.Leaned);
            var lift = new Lift(LiftId.Make(1, 1), "Chair", WorkStage.Built, quad.CatalogId, new PointMm(0, 0), new PointMm(0, 800_000),
                                TowerMode.Automatic, TowerStance.Leaned, b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(),
                                b.Levels);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _view = new LiftView(null, models, new LiftMaterials(_material, _material, _material), _material, "Pick test lift");
            _view.Build(lift, new EditedHeights(natural.Source, natural.Lattice, b.Edits), new LocalFrame(new AlbersPoint(West + 100, North - 900)), new LiftLivery(Color.red, Color.black));
        }

        [TearDown]
        public void Release()
        {
            if (_view?.Root != null) Object.DestroyImmediate(_view.Root);
            if (_material != null) Object.DestroyImmediate(_material);
        }

        static Ray From(Vector3 eye, Vector3 at) => new Ray(eye, (at - eye).normalized);

        [Test]
        public void TheRopeAndItsChairsPick()
        {
            var on = _view.RopePoint(0.25f);
            Assert.That(_view.Pick(From(on + new Vector3(60, 200, -100), on), 10_000, Slack, out float d), Is.True);
            Assert.That(d, Is.EqualTo(Vector3.Distance(on + new Vector3(60, 200, -100), on)).Within(6f));
        }

        [Test]
        public void ATerminalPicks()
        {
            var eye = new Vector3(80, 1500 + 60, -120);
            Assert.That(_view.Pick(From(eye, new Vector3(0, 1503, 0)), 10_000, Slack, out _), Is.True);
        }

        [Test]
        public void BesideTheLiftMisses()
        {
            var on = _view.RopePoint(0.25f);
            var beside = on + new Vector3(40, 0, 0);
            Assert.That(_view.Pick(From(beside + new Vector3(0, 200, 0), beside), 10_000, Slack, out _), Is.False);
        }

        [Test]
        public void TheGroundInFrontHidesIt()
        {
            // The ground hit is closer than the lift (a ridge between): no pick.
            var on = _view.RopePoint(0.25f);
            var eye = on + new Vector3(60, 200, -100);
            float far = Vector3.Distance(eye, on);
            Assert.That(_view.Pick(From(eye, on), far * 0.5f, Slack, out _), Is.False);
        }

        [Test]
        public void PickingAllocatesNothingOnceReady()
        {
            var on = _view.RopePoint(0.25f);
            var ray = From(on + new Vector3(60, 200, -100), on);
            _view.Pick(ray, 10_000, Slack, out _);
            Assert.That(() => { _view.Pick(ray, 10_000, Slack, out _); }, Is.Not.AllocatingGCMemory());
        }

        [Test]
        public void LightingMarksEveryPartAndUnlightingClearsIt()
        {
            var renderers = _view.Root.GetComponentsInChildren<Renderer>();
            Assert.That(renderers.Length, Is.GreaterThan(4), "terminals, towers and the rope");
            _view.SetHighlight(true);
            Assert.That(_view.Highlighted, Is.True);
            Assert.That(renderers.All(r => (r.renderingLayerMask & LiftOutline.Layer) != 0), Is.True);
            _view.SetHighlight(false);
            Assert.That(renderers.All(r => (r.renderingLayerMask & LiftOutline.Layer) == 0), Is.True);
        }
    }
}
