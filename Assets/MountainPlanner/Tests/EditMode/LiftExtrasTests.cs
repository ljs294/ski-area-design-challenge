using System.Linq;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.50 slice 2 (owner 2026-11-25): built lifts ship with their operator huts, beside the load or unload point on
    /// the loaded chairs' side, clear of the chairs, the door's end away from the line and never buried; a fixed grip's top
    /// (drive or return) also gets its stop gate and pedestal, and its unload ramp only when it stands 5 ft or more over the
    /// ground (the Monta drive's wide ramp, its hut outboard of the A-frame's feet).
    /// </summary>
    public sealed class LiftExtrasTests
    {
        const string SetPath = "Assets/MountainPlanner/Art/Lifts/LiftModels.asset";
        const double West = 500_000.25, North = 4_200_000.75;

        sealed class Slope : IHeightSource
        {
            public double At(double x, double y) => 1500 + 0.25 * (y - (North - 900));
        }

        LiftView _view;
        Material _material;
        /// <summary>The last <see cref="Build"/>'s earthwork.</summary>
        LiftBuild.Result Built;

        [TearDown]
        public void Release()
        {
            if (_view?.Root != null) Object.DestroyImmediate(_view.Root);
            if (_material != null) Object.DestroyImmediate(_material);
            _view = null;
        }

        /// <summary>Builds a lift of <paramref name="catalogId"/> up the slope, its top raised by <paramref name="topRaiseMm"/>.</summary>
        LiftView Build(string catalogId, int topRaiseMm = 0, LiftDrive? drive = null)
        {
            var models = AssetDatabase.LoadAssetAtPath<LiftModelSet>(SetPath);
            if (models == null) Assert.Ignore("The lift library isn't imported.");
            if (LiftView.Buildings == null) Assert.Ignore("The buildings aren't imported (Mountain Planner ▸ Import Buildings).");
            var type = LiftType.Of(catalogId, drive);
            var natural = new EditedHeights(new Slope(), new EditLattice(West, North), TerrainEdits.Empty);
            // The top set higher by hand, as Page Up does (LF-12 v6: 5 ft or more over the deck's ground, no pad).
            var b = LiftBuild.Build(type, natural, West + 100, North - 900, West + 100, North - 100, TowerStance.Leaned, null, 0, topRaiseMm / 1000.0);
            Built = b;
            var levels = b.Levels;
            var lift = new Lift(LiftId.Make(1, 1), "Chair", WorkStage.Built, catalogId, new PointMm(0, 0), new PointMm(0, 800_000),
                                TowerMode.Automatic, TowerStance.Leaned, b.Towers.Select((t, i) => new Tower(TowerId.Make(1, i), t.AlongMm, t.HeightMm)).ToList(),
                                levels, drive);
            _material = new Material(Shader.Find("Universal Render Pipeline/Lit"));
            _view = new LiftView(null, models, new LiftMaterials(_material, _material, _material), _material, "Extras test lift");
            _view.Build(lift, new EditedHeights(natural.Source, natural.Lattice, b.Edits), new LocalFrame(new AlbersPoint(West + 100, North - 900)),
                        new LiftLivery(Color.red, Color.black));
            return _view;
        }

        static void HutBeside(LiftView view, LiftView.EndPieces end, bool top, string what)
        {
            Assert.That(end?.Hut, Is.Not.Null, what + ": a hut");
            Assert.That(end.Hut.name, Does.StartWith(end.Extras.HutModel), what);
            var tr = end.Terminal.transform;
            var local = tr.InverseTransformPoint(end.Hut.transform.position);
            // The loaded chairs' side: the bottom terminal's +X, the top's -X.
            Assert.That(top ? local.x < 0 : local.x > 0, what + $": on the loaded side (x {local.x:F2})");
            // Clear of the loaded chairs (the rope at half the gauge, plus the chair's half width).
            var type = view.Rope.Type;
            float chairEdge = (float)(type.GaugeMetres / 2 + type.ChairHalfWidthMetres);
            foreach (var r in end.Hut.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.name.EndsWith("_LOD0")))
            {
                var mf = r.GetComponent<MeshFilter>();
                var mb = mf.sharedMesh.bounds;
                for (int k = 0; k < 8; k++)
                {
                    var corner = r.transform.TransformPoint(new Vector3((k & 1) != 0 ? mb.max.x : mb.min.x, mb.center.y, (k & 4) != 0 ? mb.max.z : mb.min.z));
                    Assert.That(Mathf.Abs(tr.InverseTransformPoint(corner).x), Is.GreaterThan(chairEdge + 0.3f), what + ": clear of the chairs");
                }
            }
            // The door's end away from the other terminal (-Z in the terminal's frame).
            var door = end.Hut.GetComponentsInChildren<Transform>(true).First(t => t.name.EndsWith("_socket_door"));
            Assert.That(tr.InverseTransformPoint(door.position).z, Is.LessThan(local.z), what + ": the door's end away from the line");
            // Never below the terminal's level.
            Assert.That(end.Hut.transform.position.y, Is.GreaterThanOrEqualTo(tr.position.y - 0.25f), what + ": not buried");
        }

        [Test]
        public void AFixedGripsHutsStandBesideTheLoadedChairs()
        {
            var v = Build(LiftCatalogIds.SesselliftFgq4, 0, LiftDrive.Bottom);
            Assert.That(v.BottomExtras.Extras.Hut, Is.EqualTo(HutKind.Small), "a bottom drive: the small hut");
            Assert.That(v.TopExtras.Extras.Hut, Is.EqualTo(HutKind.Mini));
            HutBeside(v, v.BottomExtras, false, "the bottom drive");
            HutBeside(v, v.TopExtras, true, "the top return");
            Assert.That(v.BottomExtras.Gate, Is.Null, "no gate at the bottom");
        }

        [Test]
        public void AFixedGripsTopDriveGetsItsRampToo()
        {
            // Owner 2026-11-25: the Sessellift drive on the standard ramp, its own gate further out (its wheel 3.23 m past).
            var s = Build(LiftCatalogIds.SesselliftFgq4, 2000, LiftDrive.Top);
            Assert.That(s.TopExtras.Extras.Drive, Is.True);
            Assert.That(s.TopExtras.Ramp?.name, Does.StartWith("bldg_ramp_quad").And.Not.Contains("wide"));
            Assert.That(s.TopExtras.Gate?.name, Does.StartWith("bldg_stop_gate_sessellift_drive"));
            Assert.That(s.TopExtras.Pedestal, Is.Not.Null);
            HutBeside(s, s.TopExtras, true, "the Sessellift top drive on its ramp");
            Release();

            // The Monta drive on its wide ramp: the hut outboard of the A-frame's feet (4.17-5.48 m from the centreline).
            var m = Build(LiftCatalogIds.MontaFg4, 2000, LiftDrive.Top);
            Assert.That(m.TopExtras.Ramp?.name, Does.StartWith("bldg_ramp_quad_wide"));
            Assert.That(m.TopExtras.Gate?.name, Does.StartWith("bldg_stop_gate_monta_drive"));
            HutBeside(m, m.TopExtras, true, "the Monta top drive on its wide ramp");
            var tr = m.TopExtras.Terminal.transform;
            foreach (var r in m.TopExtras.Hut.GetComponentsInChildren<Renderer>().Where(r => r.enabled && r.name.EndsWith("_LOD0")))
            {
                var mb = r.GetComponent<MeshFilter>().sharedMesh.bounds;
                for (int k = 0; k < 4; k++)
                {
                    var corner = r.transform.TransformPoint(new Vector3((k & 1) != 0 ? mb.max.x : mb.min.x, mb.center.y, (k & 2) != 0 ? mb.max.z : mb.min.z));
                    Assert.That(Mathf.Abs(tr.InverseTransformPoint(corner).x), Is.GreaterThan(5.48f + 0.3f), "the hut outboard of the A-frame's foot");
                }
            }
            Assert.That(Mathf.Abs(tr.InverseTransformPoint(m.TopExtras.Pedestal.transform.position).x), Is.GreaterThan(5.48f + 0.3f),
                        "the pedestal outboard of the foot");
        }

        [Test]
        public void ADetachablesHutsFollowItsDrive()
        {
            var v = Build(LiftCatalogIds.SesselliftD6);
            Assert.That(v.BottomExtras.Hut.name, Does.StartWith("bldg_hut_small"));
            Assert.That(v.TopExtras.Hut.name, Does.StartWith("bldg_hut_large"));
            HutBeside(v, v.BottomExtras, false, "the return");
            HutBeside(v, v.TopExtras, true, "the drive");
        }

        [Test]
        public void AFixedGripsTopReturnHasItsGateAndPedestalAndARampFromFiveFeet()
        {
            var level = Build(LiftCatalogIds.SesselliftStern4);
            Assert.That(level.TopExtras.Hut.name, Does.StartWith("bldg_hut_mini"));
            Assert.That(level.TopExtras.Gate.name, Does.StartWith("bldg_stop_gate_stern"));
            Assert.That(level.TopExtras.Pedestal, Is.Not.Null);
            Assert.That(level.TopExtras.Raise, Is.LessThan(TerminalExtras.RampRaiseMetres));
            Assert.That(level.TopExtras.Ramp, Is.Null, "on its pad under 5 ft");
            HutBeside(level, level.TopExtras, true, "the top return");
            Release();

            var raised = Build(LiftCatalogIds.SesselliftStern4, 2000);
            // 2 m over its origin's ground; the deck reaches downhill of it, so over the deck's mean ground (25 %) it's more.
            Assert.That(raised.TopExtras.Raise, Is.GreaterThan(2.0).And.LessThan(3.5));
            Assert.That(raised.TopExtras.Ramp, Is.Not.Null, "2 m up: the ramp");
            Assert.That(raised.TopExtras.Ramp.name, Does.StartWith("bldg_ramp_quad"));
            Assert.That(Built.TopPad.CutCubicMetres + Built.TopPad.FillCubicMetres, Is.EqualTo(0), "LF-12 v6: no pad under the ramp");
            var tr = raised.TopExtras.Terminal.transform;
            // The deck at the unload level, the ski-off past the bullwheel (away from the line: -Z), the gate on the centreline.
            Assert.That(raised.TopExtras.Ramp.transform.position.y, Is.EqualTo(tr.position.y).Within(1e-3));
            var end = raised.TopExtras.Ramp.GetComponentsInChildren<Transform>(true).First(t => t.name.EndsWith("_socket_ramp_end"));
            Assert.That(tr.InverseTransformPoint(end.position).z, Is.LessThan(-10f), "the ski-off runs past the wheel");
            Assert.That(tr.InverseTransformPoint(raised.TopExtras.Gate.transform.position).x, Is.EqualTo(0).Within(0.01), "the gate on the centreline");
            Assert.That(raised.TopExtras.Hut.GetComponentsInChildren<Renderer>(true).Where(r => r.name.Contains("_legs_")).All(r => !r.enabled),
                        "in the hut bay the hut's skids are hidden");
            HutBeside(raised, raised.TopExtras, true, "the top return on its ramp");
        }

        [Test]
        public void AHutGlowsWhileItsLiftRunsAndItsLampsSitOnTheirSockets()
        {
            // Task 4.50 slice 3: a hut's lit and dark material copies swap with its lift's running, and each lamp's halo
            // stands on its light socket's lens, a mirrored hut's too.
            var v = Build(LiftCatalogIds.SesselliftFgq4, 0, LiftDrive.Bottom);
            int lamp = Shader.PropertyToID("_LampGlow"), window = Shader.PropertyToID("_WindowGlow");
            bool mirroredSeen = false;
            foreach (var end in new[] { v.BottomExtras, v.TopExtras })
            {
                var state = end.Hut.GetComponent<Presentation.Buildings.BuildingGlowState>();
                Assert.That(state, Is.Not.Null, end.Extras.HutModel + ": its lights' state");
                Assert.That(state.InUse, Is.False, "a new lift: not running yet");
                var mats = end.Hut.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m.HasProperty(lamp)).ToList();
                Assert.That(mats, Is.Not.Empty);
                Assert.That(mats.All(m => m.GetFloat(lamp) == 0 && m.GetFloat(window) == 0), "dark while the lift is closed");
                var sockets = end.Hut.GetComponentsInChildren<Transform>(true);
                for (int i = 0; i < state.Lights.Lamps.Length; i++)
                {
                    var l = state.Lights.Lamps[i];
                    var socket = sockets.First(t => t.name.EndsWith("_socket_" + l.Name));
                    Assert.That((state.LampAt(i) - socket.position).magnitude, Is.LessThan(0.06f), $"{end.Extras.HutModel} {l.Name}: on its lens");
                }
                mirroredSeen |= end.Mirrored;
            }
            Assert.That(mirroredSeen, Is.True, "one of the two huts is the mirrored hand");
            v.HutsInUse = true;
            foreach (var end in new[] { v.BottomExtras, v.TopExtras })
            {
                Assert.That(end.Hut.GetComponent<Presentation.Buildings.BuildingGlowState>().InUse, Is.True);
                var mats = end.Hut.GetComponentsInChildren<Renderer>(true).SelectMany(r => r.sharedMaterials).Where(m => m.HasProperty(lamp)).ToList();
                Assert.That(mats.All(m => m.GetFloat(lamp) == LiftMaterials.LampGlow && m.GetFloat(window) == LiftMaterials.WindowGlow), "lit while it runs");
            }
            // The lift's own materials never glow.
            var liftMats = v.Root.GetComponentsInChildren<Renderer>(true).Where(r => r.GetComponentInParent<World.Buildings.BuildingLights>() == null)
                .SelectMany(r => r.sharedMaterials).Where(m => m != null && m.HasProperty(lamp));
            Assert.That(liftMats.All(m => m.GetFloat(lamp) == 0 && m.GetFloat(window) == 0), "no lift material glows");
        }

        [Test]
        public void ATowsHutStandsAtItsBottom()
        {
            var v = Build(LiftCatalogIds.MontaTbar);
            Assert.That(v.BottomExtras.Hut, Is.Not.Null);
            Assert.That(v.BottomExtras.Hut.name, Does.StartWith("monta_tbar_hut"));
            Assert.That(v.TopExtras.Hut, Is.Null);
        }
    }
}
