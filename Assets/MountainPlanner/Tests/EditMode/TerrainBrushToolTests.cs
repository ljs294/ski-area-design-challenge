using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The Terrain tab's brushes (task 15, TB-01 to TB-14) through the real tool host, over a fake ground: the right button,
    /// the live ground and its catch-up, the pending session, the review folded through the earthwork, Confirm as one undo
    /// step, and the contours while armed (BR13). The local frame's origin is the lattice's site centre.
    /// </summary>
    public sealed class TerrainBrushToolTests
    {
        const double West = 500_000, North = 4_200_000, OriginX = West + 1500, OriginY = North - 1500;

        sealed class Plane : IHeightSource
        {
            public double At(double x, double y) => 2000 + 0.1 * (y - OriginY);
        }

        sealed class Surface : ITerrainSurface
        {
            public Rect Footprint => new Rect(-1400, -1400, 2800, 2800);
            public float HeightAt(float x, float z) => (float)(2000 + 0.1 * z);
        }

        sealed class Preview : IToolPreview
        {
            public event Action Cleared;
            public void Line(int slot, Vector2[] xz, int count, PreviewInk ink, bool closed = false) { }
            public void Ring(int slot, Vector2 centre, float radiusMetres, PreviewInk ink) { }
            public void Dot(int slot, Vector2 at, PreviewInk ink) { }
            public void Hide(int slot) { }
            public void HideFrom(int slot) { }
            public void Clear() => Cleared?.Invoke();
        }

        /// <summary>The ground host: the earthwork over the plane, and a live ground that records what it was asked.</summary>
        sealed class Ground : IEarthworkHost, ILiveGround
        {
            public readonly DesignHistory History;
            public Ground(DesignHistory history, DesignEarthwork earthwork) { History = history; Earthwork = earthwork; }
            public void Start() { }
            public bool Ready => true;
            public DesignEarthwork Earthwork { get; }
            public EditedHeights Heights => new EditedHeights(new Plane(), Earthwork.Site.Lattice, Shown ?? History.Terrain);
            public void ShowPending(List<double> segments) { }
            public void ClearPending() { }
            public double ContourInterval => 12.192;
            public int LiveCalls, ShowCalls;
            public TerrainEdits Shown;
            public bool? Contours;
            public void Live(TerrainEdits preview, PointRect dirty) { LiveCalls++; }
            public void Show(TerrainEdits edits) { ShowCalls++; Shown = edits; }
            public void ContoursWhileArmed(bool armed) => Contours = armed;
            public double LastLiveMs => 0;
        }

        DesignHistory _history;
        Ground _ground;
        ToolHost _host;

        [SetUp]
        public void SetUp()
        {
            _history = new DesignHistory(DesignDocument.Empty(21));
            var site = new EarthworkSite(new EditLattice(West, North), OriginX, OriginY);
            var earthwork = new DesignEarthwork(new Plane(), site);
            earthwork.Prime(_history.Document, TerrainEdits.Empty);
            _ground = new Ground(_history, earthwork);
            _host = new ToolHost(_ => new ToolContext(_history, new Preview(), new Surface(), new Rect(-1400, -1400, 2800, 2800))
            {
                Earthwork = _ground, LiveGround = _ground,
                ToGround = p => (OriginX + p.XMetres, OriginY + p.YMetres),
            }, _ => { });
        }

        [TearDown]
        public void TearDown()
        {
            _host.Close();
            KeyBindings.ClearClaims();
            ViewCamera.RightButtonClaimed = false;
        }

        static ToolInput At(float x, float z, bool left = false, bool right = false, bool click = false, bool release = false, ToolKeys keys = ToolKeys.None)
        {
            var g = new Vector3(x, 2000 + 0.1f * z, z);
            return new ToolInput
            {
                HasGround = true, Ground = g, Point = DesignFrame.ToDesign(g), InCore = true, MetresPerPixel = 0.5f, DeltaSeconds = 1f / 30,
                Click = left && click, Drag = left && !release, Release = left && release,
                RightClick = right && click, RightDrag = right && !release, RightRelease = right && release, Keys = keys,
            };
        }

        void Stroke(float x0, float x1, float z, bool right, int hold = 10)
        {
            _host.Tick(At(x0, z, left: !right, right: right, click: true));
            for (int t = 1; t <= 20 + hold; t++) _host.Tick(At(Mathf.Lerp(x0, x1, Mathf.Min(1, t / 20f)), z, left: !right, right: right));
            _host.Tick(At(x1, z, left: !right, right: right, release: true));
        }

        int DeltaAt(TerrainEdits e, float x, float z) =>
            e.DeltaMmAt((int)Math.Round(OriginX + x - West), (int)Math.Round(North - (OriginY + z)));

        [Test]
        public void TB01_ABrushOwnsTheRightButtonAndTurnsContoursOnUntilItCloses()
        {
            Assert.That(_host.Choose(TerrainIds.Raise), Is.True);
            Assert.That(ViewCamera.RightButtonClaimed, Is.True, "BR4: right-drag no longer pans");
            Assert.That(_ground.Contours, Is.True, "BR13: contours on while brushing");
            _host.Close();
            Assert.That(ViewCamera.RightButtonClaimed, Is.False);
            Assert.That(_ground.Contours, Is.False, "the player's contours setting comes back");
        }

        [Test]
        public void TB04_LeftRaisesRightLowersLiveThenPending()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            Assert.That(_ground.LiveCalls, Is.GreaterThan(10), "BR5: the ground moves every tick while held");
            Assert.That(tool.Pending, Is.EqualTo(1));
            Assert.That(_ground.ShowCalls, Is.EqualTo(1), "trees, cover and snow catch up when the stroke ends");
            Assert.That(DeltaAt(tool.PendingGround, 0, 0), Is.GreaterThan(0), "left raised");
            Stroke(-20, 20, 60, right: true);
            Assert.That(DeltaAt(tool.PendingGround, 0, 60), Is.LessThan(0), "right lowered");
            Assert.That(_history.Document.Strokes, Is.Empty, "nothing goes in before Confirm (BR7)");
        }

        [Test]
        public void BR14_CutAndFillCountUpWhileYouDrag()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Assert.That(tool.Bill.Rows, Is.Empty, "no bill before the first stroke");
            _host.Tick(At(0, 0, left: true, click: true));
            var seen = new List<string>();
            for (int t = 0; t < 45; t++)
            {
                _host.Tick(At(0, 0, left: true));
                string v = tool.Bill.Rows.Count > 0 ? tool.Bill.Rows[0].Value : null;
                if (v != null && (seen.Count == 0 || seen[seen.Count - 1] != v)) seen.Add(v);
            }
            Assert.That(tool.Pending, Is.EqualTo(0), "still held: nothing pending yet");
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(3), "the cut · fill figure moves several times mid-drag: " + string.Join(", ", seen));
            _host.Tick(At(0, 0, left: true, release: true));
            Assert.That(tool.Pending, Is.EqualTo(1));
            Assert.That(tool.Bill.Rows[0].Label, Is.EqualTo("Cut · fill"), "the bill stays once the stroke is pending");
        }

        [Test]
        public void BR15_SwitchingBrushesKeepsThePendingStrokes()
        {
            string notice = null;
            _host.Notice += s => notice = s;
            _host.Choose(TerrainIds.Raise);
            Stroke(-20, 20, 0, right: false);
            var raised = ((TerrainBrushTool)_host.Active).PendingGround;
            _host.Choose(TerrainIds.Smooth);
            var smooth = (TerrainBrushTool)_host.Active;
            Assert.That(notice, Is.Null, "nothing discarded");
            Assert.That(smooth.Pending, Is.EqualTo(1), "the Raise stroke carries on");
            Assert.That(smooth.PendingGround, Is.SameAs(raised), "and so does the ground it made");
            Assert.That(DeltaAt(_ground.Shown, 0, 0), Is.GreaterThan(0), "the raised ground stays on the map");
            Stroke(-20, 20, 0, right: false);
            Assert.That(smooth.Pending, Is.EqualTo(2));
            Assert.That(smooth.Bill.Rows[0].Label, Is.EqualTo("Cut · fill"), "one bill for both");
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            Assert.That(smooth.Pending, Is.EqualTo(1), "Ctrl+Z takes back across brushes");
            _host.Choose(NewLiftTool.ToolId);
            Assert.That(notice, Does.Contain("discarded"), "another kind of tool still discards (P3-06 Q8)");
        }

        [Test]
        public void BR16_SlopeWaitsForATargetThenRampsBetweenTheTwoPoints()
        {
            _host.Choose(TerrainIds.Slope);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            Assert.That(tool.Pending, Is.EqualTo(0), "no hidden target: nothing paints until the right click");
            // The target 30 ft above the ground 60 m away: the middle of the ramp comes up about half that.
            _host.Tick(At(60, 0, right: true, click: true));
            _host.Tick(At(60, 0, right: true, release: true));
            var up = At(60, 0);
            up.Keys = ToolKeys.TargetUp;
            up.Shift = true;
            for (int i = 0; i < 3; i++) _host.Tick(up);
            tool.SetBrush(60, 100);
            for (int pass = 0; pass < 4; pass++) Stroke(0, 60, 0, right: false, hold: 30);   // going over it again keeps the same ramp
            Assert.That(tool.Pending, Is.EqualTo(4));
            int mid = DeltaAt(tool.PendingGround, 30, 0), end = DeltaAt(tool.PendingGround, 60, 0);
            Assert.That(end, Is.GreaterThan(5000), "the far end rises toward the target");
            Assert.That(mid, Is.EqualTo(end / 2).Within(end / 5), "the ramp is even between the two points");
        }

        [Test]
        public void TB08_CtrlZTakesBackTheLastPendingStroke()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            var one = tool.PendingGround;
            Stroke(-20, 20, 0, right: false);
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            Assert.That(tool.Pending, Is.EqualTo(1));
            Assert.That(TerrainEdits.Changed(one, tool.PendingGround), Is.Empty, "the same bytes as before the second stroke");
        }

        [UnityTest]
        public IEnumerator TB08_ConfirmPutsTheSessionInAsOneStepWithTheGroundThePlayerSaw()
        {
            _host.Choose(TerrainIds.Flatten);
            var tool = (TerrainBrushTool)_host.Active;
            _host.Tick(At(0, 50, right: true, click: true));   // the target: the ground 50 m north
            _host.Tick(At(0, 50, right: true, release: true));
            Stroke(-30, 30, 0, right: false, hold: 40);
            Stroke(-30, 30, 20, right: false, hold: 40);
            var seen = tool.PendingGround;
            _host.Tick(At(0, 0, keys: ToolKeys.Next));
            var started = System.Diagnostics.Stopwatch.StartNew();   // the review works off the main thread
            while (!tool.ReviewReady && started.Elapsed.TotalSeconds < 60) { _host.Tick(default); yield return null; }
            Assert.That(tool.ReviewReady, Is.True, "the review worked out");
            long revision = _history.Revision;
            var result = _host.Confirm(WorkStage.Built);
            Assert.That(result != null && result.Accepted, Is.True, result?.Reason);
            Assert.That(_history.Document.Strokes.Count, Is.EqualTo(2));
            Assert.That(TerrainEdits.Changed(seen, _history.Terrain), Is.Empty, "TB-14: confirmed is what was seen");
            Assert.That(_history.Undo(), Is.True);
            Assert.That(_history.Document.Strokes, Is.Empty, "one undo takes the whole session back");
            Assert.That(_history.Terrain.IsEmpty, Is.True);
            Assert.That(revision, Is.LessThan(_history.Revision));
        }

        [Test]
        public void CF06_ClosingWithStrokesPendingPutsTheCommittedGroundBack()
        {
            _host.Choose(TerrainIds.Raise);
            Stroke(-20, 20, 0, right: false);
            int shows = _ground.ShowCalls;
            _host.Close();
            Assert.That(_ground.ShowCalls, Is.EqualTo(shows + 1));
            Assert.That(_ground.Shown, Is.SameAs(_history.Terrain), "back to the committed ground");
        }
    }
}
