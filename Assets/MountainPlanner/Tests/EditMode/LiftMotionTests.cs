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
    /// Task P3-16, BB2: visible bullwheels spin with the rope, at no per-frame cost; so do the rope's sheaves (owner,
    /// 2026-11-06).
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

        /// <summary>
        /// Owner, 2026-11-06 ("ensure the sheaves on the tower are rotating properly"): every sheave the rope touches turns
        /// with it, its rim at the contact moving the way the loop runs there (up the loaded side, down the other) at rope
        /// speed.
        /// </summary>
        [TestCase(LiftCatalogIds.SesselliftFgq4)]
        [TestCase(LiftCatalogIds.MontaFg4)]
        [TestCase(LiftCatalogIds.SesselliftD6)]
        [TestCase(LiftCatalogIds.MontaTbar)]
        public void TheRopesSheavesTurnWithIt(string catalogId)
        {
            var view = Build(catalogId);
            var type = LiftType.Of(catalogId);
            Assume.That(view.Posed, "posable models");
            Assert.That(view.SheaveCount, Is.GreaterThan(8), "the towers' and terminals' sheaves");
            const int samples = 4000;
            var loop = new Vector3[samples + 1];
            for (int k = 0; k <= samples; k++) loop[k] = view.RopePoint((float)k / samples) + Vector3.up;
            const double dt = 0.01;
            for (int i = 0; i < view.SheaveCount; i++)
            {
                var pivot = view.SheavePivot(i, out var contact);
                _camera.transform.position = pivot.position + new Vector3(5, 2, 0);
                view.DrawChairs(_camera, 1.0);
                var before = pivot.rotation;
                view.DrawChairs(_camera, 1.0 + dt);
                var turn = pivot.rotation * Quaternion.Inverse(before);
                var rim = (turn * (contact - pivot.position) - (contact - pivot.position)) / (float)dt;

                int near = 0;
                for (int k = 1; k < samples; k++)
                    if ((loop[k] - contact).sqrMagnitude < (loop[near] - contact).sqrMagnitude) near = k;
                var way = (loop[Math.Min(near + 3, samples)] - loop[Math.Max(near - 3, 0)]).normalized;
                // The loop's points either side of a contact can lie a sheave or two off it, across a head's bend: the same
                // way, within 60°, and the speed below.
                Assert.That(Vector3.Dot(rim.normalized, way), Is.GreaterThan(0.5f), $"sheave {i} ({pivot.name}) turns the way the rope runs");
                // The groove's bottom moves at rope speed; the rope's centreline (the contact) sits a rope radius further out.
                float r = (contact - pivot.position).magnitude, groove = r - (float)type.RopeDiameterMetres / 2;
                Assert.That(rim.magnitude * groove / r, Is.EqualTo((float)type.SpeedMetresPerSecond).Within(2).Percent, $"sheave {i}'s rim at rope speed");
            }
        }

        /// <summary>
        /// 4.01 (v0.3.2's 40-lift bench, [Chairs] 2.45 ms against 0.81): a lift whose sheaves are all past the spin distance
        /// leaves every one as it was, and the near ones still turn once the eye comes back.
        /// </summary>
        [Test]
        public void SheavesPastTheSpinDistanceAreLeftAsTheyWere()
        {
            var view = Build(LiftCatalogIds.SesselliftFgq4);
            Assume.That(view.Posed, "posable models");
            var pivot = view.SheavePivot(0, out var contact);
            _camera.transform.position = contact + new Vector3(0, 2000, 0);
            var rest = new Quaternion[view.SheaveCount];
            for (int i = 0; i < rest.Length; i++) rest[i] = view.SheavePivot(i, out _).localRotation;
            view.DrawChairs(_camera, 1.0);
            view.DrawChairs(_camera, 1.37);
            for (int i = 0; i < rest.Length; i++)
                Assert.That(Quaternion.Angle(view.SheavePivot(i, out _).localRotation, rest[i]), Is.LessThan(1e-3f), $"sheave {i} far away");
            _camera.transform.position = contact + new Vector3(5, 2, 0);
            var before = pivot.localRotation;
            view.DrawChairs(_camera, 1.37);
            Assert.That(Quaternion.Angle(pivot.localRotation, before), Is.GreaterThan(1f), "the near sheave turns again");
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
