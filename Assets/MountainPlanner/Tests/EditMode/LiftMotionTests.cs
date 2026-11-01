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
    /// Task P3-16: visible bullwheels spin with the rope (BB2), and the chairs swing along the line where the rope changes
    /// direction over a sheave train, and sway side to side in gusts (BB3), at no per-frame cost.
    /// </summary>
    public sealed class LiftMotionTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;

        /// <summary>A slope that steepens up the hill, so the line crests over towers and the rope bends at each.</summary>
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
        ChairSwing.Look _look;

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
            _look = ChairSwing.Current;
            _camera = new GameObject("Motion test camera").AddComponent<Camera>();
        }

        [TearDown]
        public void Release()
        {
            ChairSwing.Current = _look;
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
        public void BB3_TheRopeBendingOverTowersKicksTheChairs()
        {
            var view = Build(LiftCatalogIds.SesselliftFgq4);
            var swing = view.Swing;
            Assert.That(view.Rope.Towers.Count, Is.GreaterThan(2));
            Assert.That(swing.KickCount, Is.GreaterThanOrEqualTo(view.Rope.Towers.Count), "a kick at each tower's sheaves, both ropes");

            var look = new ChairSwing.Look { Name = "test", Gain = 4, Sway = ChairSwing.SwayDriver.Off };
            var kick = swing.Kicks.First(k => !k.Lean && k.Start > 1);
            float peak = 0;
            for (float age = 0; age < 3; age += 0.05f) peak = Mathf.Max(peak, Mathf.Abs(swing.Pitch(kick.Start + age, 0, 0, look)));
            Assert.That(peak, Is.GreaterThan(0.05f), "the chair swings after the tower");
            Assert.That(Mathf.Abs(swing.Pitch(kick.Start - 0.001f, 0, 0, look) - swing.Pitch(kick.Start + 0.001f, 0, 0, look)), Is.LessThan(0.05f),
                        "no jump as it passes");
            for (float t = 0; t < swing.LoopTime; t += 0.25f)
                Assert.That(Mathf.Abs(swing.Pitch(t, t, 3, ChairSwing.Presets[2])), Is.LessThanOrEqualTo(ChairSwing.MaxDegrees));
            Assert.That(swing.Pitch(kick.Start + 1, 7, 2, ChairSwing.Presets[0]), Is.Zero, "Off is off");
            Assert.That(swing.Pitch(kick.Start + 1, 7, 2, look), Is.EqualTo(swing.Pitch(kick.Start + 1, 7, 2, look)), "the same moment, the same swing");
        }

        [Test]
        public void BB3_ABiggerBendKicksHarder()
        {
            var look = new ChairSwing.Look { Name = "test", Gain = 1, Sway = ChairSwing.SwayDriver.Off };
            float Peak(float bendDegrees)
            {
                // Straight up at 20°, then on at 20° − bend, sampled every half metre.
                var points = new System.Collections.Generic.List<Vector3>();
                var p = Vector3.zero;
                for (int i = 0; i < 200; i++)
                {
                    float a = (i < 100 ? 20 : 20 - bendDegrees) * Mathf.Deg2Rad;
                    points.Add(p);
                    p += new Vector3(0, Mathf.Sin(a), Mathf.Cos(a)) * 0.5f;
                }
                var swing = ChairSwing.For(CarrierTrack.Uniform(points.ToArray(), 2.286f), 2.66f);
                if (swing.KickCount == 0) return 0;
                float peak = 0, at = swing.Kicks[swing.Kicks.Count - 1].Start;
                for (float age = 0; age < 3; age += 0.02f) peak = Mathf.Max(peak, Mathf.Abs(swing.Pitch(at + age, 0, 0, look)));
                return peak;
            }
            Assert.That(Peak(0), Is.Zero, "a straight rope doesn't kick");
            Assert.That(Peak(8), Is.GreaterThan(Peak(3)));
        }

        [Test]
        public void BB3_ChairsSwayOnlyInGustsAndWind()
        {
            var view = Build(LiftCatalogIds.SesselliftFgq4);
            var wind = new ChairSwing.Look { Name = "test", SwayDegrees = 1.5f, Sway = ChairSwing.SwayDriver.Wind };
            Assert.That(view.Swing.Roll(10, 0, 0, 0, 1, wind), Is.Zero, "calm: still");
            Assert.That(view.Swing.Roll(10, 0, 0, 1, 0, wind), Is.Zero, "wind along the line: no sway");
            float most = 0, still = 0;
            for (double t = 0; t < 3600; t += 0.5)
            {
                float r = Mathf.Abs(view.Swing.Roll(t, 0, 0, 1, 1, wind));
                most = Mathf.Max(most, r);
                if (r < 0.01f) still++;
            }
            Assert.That(most, Is.GreaterThan(0.5f).And.LessThanOrEqualTo(1.5f));
            Assert.That(still / 7200, Is.GreaterThan(0.4f), "every so often, not all the time");
            var timer = wind;
            timer.Sway = ChairSwing.SwayDriver.Timer;
            Assert.That(Enumerable.Range(0, 1200).Any(i => view.Swing.Roll(i * 0.5, 0, 0, 0, 0, timer) != 0), Is.True, "the timer ignores the wind");
        }

        [Test]
        public void DrawingTheMovingLiftAllocatesNothing()
        {
            var view = Build(LiftCatalogIds.SesselliftFgq4);
            _camera.transform.position = view.RopePoint(0.1f) + new Vector3(10, 5, 0);
            ChairSwing.Current = ChairSwing.Presets[2];
            view.DrawChairs(_camera, 3, 1, Vector2.right);
            Assert.That(() => { view.DrawChairs(_camera, 3.5, 1, Vector2.right); }, Is.Not.AllocatingGCMemory());
        }
    }
}
