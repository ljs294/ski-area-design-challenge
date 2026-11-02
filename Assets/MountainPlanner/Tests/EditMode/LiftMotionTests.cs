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
    /// <summary>
    /// Task P3-16, BB2: visible bullwheels spin with the rope, at no per-frame cost.
    /// </summary>
    public sealed class LiftMotionTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;

        /// <summary>A slope that steepens up the hill, so the line has towers.</summary>
        sealed class Steepening : IHeightSource
        {
            public double At(double x, double y)
            {
                double s = y - (North - 900);
                return 1500 + 0.15 * s + 0.0002 * s * s;
            }
        }

        LiftView _view;
        Material _material;
        Camera _camera;

        LiftView Build(string catalogId)
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            var type = LiftType.Of(catalogId);
            var natural = new EditedHeights(new Steepening(), new EditLattice(West, North), TerrainEdits.Empty);
            double bx = West + 100, by = North - 900, tx = bx, ty = by + 800;
            var b = LiftBuild.Build(type, natural, bx, by, tx, ty, TowerStance.Plumb);
            var lift = new Lift(LiftId.Make(1, 1), "Chair", WorkStage.Built, type.CatalogId, new PointMm(0, 0), PointMm.FromMetres(0, 800),
                                TowerMode.Automatic, TowerStance.Plumb, b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(), b.Levels);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _view = new LiftView(null, models, new LiftMaterials(_material, _material, _material), _material, "Motion test lift");
            _view.Build(lift, new EditedHeights(natural.Source, natural.Lattice, b.Edits), new LocalFrame(new AlbersPoint(bx, by)), LiftLivery.For(type.CatalogId));
            return _view;
        }

        [SetUp]
        public void Remember()
        {
            _camera = new GameObject("Motion test camera").AddComponent<Camera>();
        }

        [TearDown]
        public void Release()
        {
            if (_view?.Root != null) Object.DestroyImmediate(_view.Root);
            if (_material != null) Object.DestroyImmediate(_material);
            if (_camera != null) Object.DestroyImmediate(_camera.gameObject);
        }

        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.MontaFg4)]
        public void BB2_VisibleBullwheelsTurnWithTheRope(string catalogId)
        {
            var view = Build(catalogId);
            var type = LiftType.Of(catalogId);
            Assert.That(view.WheelCount, Is.EqualTo(2), "both terminals' wheels");
            double turn = 2 * Math.PI * (type.GaugeMetres / 2) / type.SpeedMetresPerSecond;   // one turn: its rim's length at rope speed
            for (int i = 0; i < 2; i++)
            {
                var pivot = view.WheelPivot(i);
                view.DrawChairs(_camera, 0);
                var rest = pivot.rotation;
                var spoke = rest * Vector3.forward;
                view.DrawChairs(_camera, turn / 4);
                Assert.That(Quaternion.Angle(rest, pivot.rotation), Is.EqualTo(90).Within(0.5f), "a quarter turn in a quarter of the time");
                view.DrawChairs(_camera, turn);
                Assert.That(Quaternion.Angle(rest, pivot.rotation), Is.LessThan(0.5f), "a whole turn brings it back");

                // The rim moves with the rope: from where the rope arrives at the wheel round to where it leaves.
                var rig = pivot.GetComponentInParent<LiftRig>();
                var from = rig.Socket("rope_left_bw").position;
                var to = rig.Socket("rope_right_bw").position;
                var centre = (from + to) / 2;
                var back = -rig.transform.forward;
                float ropeWay = Mathf.Sign(Vector3.SignedAngle(from - centre, back, Vector3.up));
                view.DrawChairs(_camera, 0.5);
                float wheelWay = Mathf.Sign(Vector3.SignedAngle(spoke, pivot.rotation * Vector3.forward, Vector3.up));
                Assert.That(wheelWay, Is.EqualTo(ropeWay), $"wheel {i} turns the way the rope goes round it");
            }
        }

        [Test]
        public void BB2_TheSixPacksWheelsInItsTubStayStill()
        {
            var view = Build(LiftCatalogIds.SesselliftD6);
            Assert.That(view.WheelCount, Is.Zero);
            Assert.That(LiftType.Of(LiftCatalogIds.SesselliftD6).BullwheelVisible, Is.False);
        }

        [Test]
        public void DrawingTheMovingLiftAllocatesNothing()
        {
            var view = Build(LiftCatalogIds.SesselliftFgq4);
            _camera.transform.position = view.RopePoint(0.1f) + new Vector3(10, 5, 0);
            view.DrawChairs(_camera, 3);
            Assert.That(() => { view.DrawChairs(_camera, 3.5); }, Is.Not.AllocatingGCMemory());
        }
    }
}
