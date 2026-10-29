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
using UnityEngine.UIElements;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The Terrain tab's brushes (task 15, TB-01 to TB-14, BR17 to BR19) through the real tool host, over a fake ground: the
    /// right button, the live ground and its catch-up, the session with Ctrl+Z, Confirm folded through the earthwork as one
    /// undo step and charged, Revert, the close ask, and the contours while armed (BR13). The local frame's origin is the
    /// lattice's site centre.
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
            public int LiveCalls, ShowCalls, CatchUps, GroundShows;
            public TerrainEdits Shown, Clearing;
            public bool? Contours;
            public void Live(IPointDeltas ground, PointRect dirty) { LiveCalls++; }
            public void Show(TerrainEdits edits) { ShowCalls++; Shown = edits; Clearing = edits; }
            public System.Threading.Tasks.Task CatchUp(TerrainEdits ground, TerrainEdits clearing) { CatchUps++; Shown = ground; Clearing = clearing; return System.Threading.Tasks.Task.CompletedTask; }
            public System.Threading.Tasks.Task ShowGround(TerrainEdits ground, TerrainEdits clearing) { GroundShows++; Shown = ground; Clearing = clearing; return System.Threading.Tasks.Task.CompletedTask; }
            public void ContoursWhileArmed(bool armed) => Contours = armed;
            public double LastLiveMs => 0;
            public bool? Held;
            public void Painting(bool held) => Held = held;
            public int Settles;
            public bool Settle() { Settles++; return false; }
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
            Assert.That(_ground.ShowCalls, Is.EqualTo(0), "BR21: no full apply when the stroke ends");
            Assert.That(_ground.CatchUps, Is.GreaterThan(1), "the cover, trees' footing and snow caught up while it was held");
            Assert.That(_ground.Shown, Is.SameAs(tool.PendingGround), "and once more to the ground let go, so nothing changes after");
            Assert.That(_ground.Clearing, Is.SameAs(_history.Terrain), "trees stay in until Confirm (BR21)");
            Assert.That(DeltaAt(tool.PendingGround, 0, 0), Is.GreaterThan(0), "left raised");
            Stroke(-20, 20, 60, right: true);
            Assert.That(DeltaAt(tool.PendingGround, 0, 60), Is.LessThan(0), "right lowered");
            Assert.That(_history.Document.Strokes, Is.Empty, "nothing goes in before Confirm (BR17)");
        }

        [Test]
        public void BR14_CutAndFillCountUpWhileYouDrag()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Assert.That(tool.Bill.Rows, Is.Empty, "no bill before the first stroke");
            Assert.That(tool.Bill.CanBreakGround, Is.False, "Confirm waits for a stroke");
            _host.Tick(At(0, 0, left: true, click: true));
            var seen = new List<string>();
            for (int t = 0; t < 45; t++)
            {
                _host.Tick(At(0, 0, left: true));
                string v = tool.Bill.Rows.Count > 0 ? tool.Bill.Rows[0].Value : null;
                if (v != null && (seen.Count == 0 || seen[seen.Count - 1] != v)) seen.Add(v);
            }
            Assert.That(tool.Pending, Is.EqualTo(0), "still held: nothing in the session yet");
            Assert.That(seen.Count, Is.GreaterThanOrEqualTo(3), "the cut · fill figure moves several times mid-drag: " + string.Join(", ", seen));
            _host.Tick(At(0, 0, left: true, release: true));
            Assert.That(tool.Pending, Is.EqualTo(1));
            Assert.That(tool.Bill.Rows[0].Label, Is.EqualTo("Cut · fill"), "the bill stays once the stroke is down");
            Assert.That(tool.Bill.CanBreakGround && tool.Bill.CanSavePlan, Is.True, "BR17: Confirm and Revert go live");
        }

        [Test]
        public void BR15_SwitchingBrushesKeepsTheSession()
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
        public void TB08_CtrlZTakesBackOnePressAndStopsAtTheSessionStart()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            var bill0 = tool.Bill.Rows.Count;
            Stroke(-20, 20, 0, right: false);
            var one = tool.PendingGround;
            string billOne = tool.Bill.Rows[0].Value;
            Stroke(-20, 20, 0, right: false);
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            Assert.That(tool.Pending, Is.EqualTo(1));
            Assert.That(tool.PendingGround, Is.SameAs(one), "the very ground before the second press");
            Assert.That(_ground.Shown, Is.SameAs(one), "and it's what the map shows");
            Assert.That(tool.Bill.Rows[0].Value, Is.EqualTo(billOne), "the bill goes back too");
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            Assert.That(tool.Pending, Is.EqualTo(0));
            Assert.That(_ground.Shown, Is.SameAs(_history.Terrain), "back to the session's start");
            long revision = _history.Revision;
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            Assert.That(_history.Revision, Is.EqualTo(revision), "never past the session's start (BR17)");
            Assert.That(tool.Bill.Rows.Count, Is.EqualTo(bill0));
        }

        [Test]
        public void TB08_ALongDragIsOneCtrlZ()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            tool.SetBrush(10, 50);
            // A drag long enough to carry on as several strokes (TB-14), then one Ctrl+Z.
            _host.Tick(At(-40, 0, left: true, click: true));
            int ticks = DesignRules.MaxDabsPerStroke + 40;
            for (int t = 1; t <= ticks; t++) _host.Tick(At(-40 + 80f * t / ticks, 0, left: true));
            _host.Tick(At(40, 0, left: true, release: true));
            Assert.That(tool.Pending, Is.GreaterThan(1), "stored as several strokes");
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            Assert.That(tool.Pending, Is.EqualTo(0), "one press, one Ctrl+Z");
        }

        sealed class Charges : IEarthworkCharges
        {
            public readonly List<EarthworkBill> Bills = new List<EarthworkBill>();
            public void Charge(in EarthworkBill bill) => Bills.Add(bill);
        }

        IEnumerator WhileConfirming(TerrainBrushTool tool)
        {
            var started = System.Diagnostics.Stopwatch.StartNew();   // the fold runs off the main thread
            while (tool.Confirming && started.Elapsed.TotalSeconds < 60) { _host.Tick(default); yield return null; }
            _host.Tick(default);
            Assert.That(tool.Confirming, Is.False, "Confirm landed");
        }

        [UnityTest]
        public IEnumerator BR17_ConfirmPutsTheSessionInAsOneStepAndChargesForIt()
        {
            _host.Choose(TerrainIds.Flatten);
            var tool = (TerrainBrushTool)_host.Active;
            var charges = new Charges();
            _host.Context.Charges = charges;
            _host.Tick(At(0, 50, right: true, click: true));   // the target: the ground 50 m north
            _host.Tick(At(0, 50, right: true, release: true));
            Stroke(-30, 30, 0, right: false, hold: 40);
            Stroke(-30, 30, 20, right: false, hold: 40);
            var seen = tool.PendingGround;
            int shows = _ground.ShowCalls;
            long revision = _history.Revision;
            _host.Tick(At(0, 0, keys: ToolKeys.Next));   // Enter: Confirm, no review page
            Assert.That(tool.Confirming, Is.True);
            yield return WhileConfirming(tool);
            Assert.That(_history.Document.Strokes.Count, Is.EqualTo(2));
            Assert.That(_history.Terrain, Is.SameAs(seen), "TB-14: what was seen is what's confirmed, the very set, so nothing re-applies");
            Assert.That(_ground.ShowCalls, Is.EqualTo(shows), "the map doesn't move");
            Assert.That(charges.Bills.Count, Is.EqualTo(1), "BR19: charged once, at Confirm");
            Assert.That(charges.Bills[0].Strokes, Is.EqualTo(2));
            Assert.That(charges.Bills[0].CutCubicMetres + charges.Bills[0].FillCubicMetres, Is.GreaterThan(0));
            Assert.That(tool.Pending, Is.EqualTo(0), "a fresh session");
            Assert.That(_host.Armed, Is.True, "the tool stays open (P3-06 Q4)");
            Assert.That(_history.Undo(), Is.True);
            Assert.That(_history.Document.Strokes, Is.Empty, "one undo takes the whole session back");
            Assert.That(_history.Terrain.IsEmpty, Is.True);
            Assert.That(revision, Is.LessThan(_history.Revision));
        }

        [UnityTest]
        public IEnumerator BR17_ConfirmUsesTheFoldStartedWhenThePressEnded()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            Stroke(-20, 20, 30, right: false);
            var started = System.Diagnostics.Stopwatch.StartNew();
            // The fold starts once the let-go's catch-up has landed, on a later tick.
            while (!tool.FoldDone && started.Elapsed.TotalSeconds < 60) { _host.Tick(default); yield return null; }
            Assert.That(tool.FoldDone, Is.True, "letting go folds the session ahead of Confirm");
            var seen = tool.PendingGround;
            _host.Confirm(WorkStage.Built);
            _host.Tick(default);   // the fold is done: Confirm lands on the next tick
            Assert.That(tool.Confirming, Is.False);
            Assert.That(tool.FoldWasAhead, Is.True);
            Assert.That(_history.Document.Strokes.Count, Is.EqualTo(2));
            Assert.That(_history.Terrain, Is.SameAs(seen), "TB-14: the same set, so nothing re-applies");
        }

        [UnityTest]
        public IEnumerator BR17_CtrlZAfterLettingGoFoldsTheSessionAgain()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            var one = tool.PendingGround;
            Stroke(-20, 20, 30, right: false);
            _host.Tick(At(0, 0, keys: ToolKeys.UndoStroke));
            _host.Confirm(WorkStage.Built);
            yield return WhileConfirming(tool);
            Assert.That(_history.Document.Strokes.Count, Is.EqualTo(1), "the second stroke's fold isn't the one that goes in");
            Assert.That(_history.Terrain, Is.SameAs(one));
        }

        [Test]
        public void BR17_RevertPutsTheSessionsGroundBack()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            var charges = new Charges();
            _host.Context.Charges = charges;
            Stroke(-20, 20, 0, right: false);
            Stroke(-20, 20, 30, right: true);
            long revision = _history.Revision;
            _host.Confirm(WorkStage.Plan);   // the pane's second button: Revert
            Assert.That(tool.Pending, Is.EqualTo(0));
            Assert.That(_ground.Shown, Is.SameAs(_history.Terrain), "the ground before the session");
            Assert.That(_history.Revision, Is.EqualTo(revision), "nothing reaches the design");
            Assert.That(charges.Bills, Is.Empty, "or the bill");
            Assert.That(_host.Armed, Is.True);
        }

        [Test]
        public void BR18_ClosingAsksAndEscKeepsPainting()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            Assert.That(_host.Escape(), Is.True);
            Assert.That(tool.Asking, Is.True, "Esc with ground not confirmed asks");
            Assert.That(_host.Armed, Is.True);
            var pane = _host.Context.Window.Window;
            bool Hidden(string name) => pane.Q(name).ClassListContains("hidden");
            Assert.That(Hidden("toolwin-acts") && Hidden("toolwin-keys"), Is.True, "the ask's row takes the foot: no second Confirm and Revert (terrainask)");
            Assert.That(Hidden("toolwin-askacts"), Is.False);
            Assert.That(pane.Q("toolwin-btn-ask-confirm").ClassListContains("mp-go"), Is.True, "Confirm is the main button");
            Assert.That(pane.Q("toolwin-btn-ask-revert"), Is.Not.Null);
            Assert.That(pane.Q("toolwin-btn-ask-keep"), Is.Not.Null);
            _host.Escape();
            Assert.That(tool.Asking, Is.False, "Esc again: keep painting");
            Assert.That(_host.Armed, Is.True);
            Assert.That(Hidden("toolwin-acts") || Hidden("toolwin-keys"), Is.False, "the keys and the pane's Confirm and Revert come back");
            Assert.That(Hidden("toolwin-askacts"), Is.True);
            Assert.That(tool.Pending, Is.EqualTo(1), "nothing lost");
            _host.Choose(TerrainIds.Raise);   // its own tile again closes: asks too
            Assert.That(tool.Asking && _host.Armed, Is.True);
            Stroke(-20, 20, 40, right: false);
            Assert.That(tool.Asking, Is.False, "painting again answers it");
            Assert.That(tool.Pending, Is.EqualTo(2));
        }

        [Test]
        public void BR18_RevertOnTheAskThenGoes()
        {
            string notice = null;
            _host.Notice += s => notice = s;
            _host.Choose(TerrainIds.Raise);
            Stroke(-20, 20, 0, right: false);
            Assert.That(_host.Choose(NewLiftTool.ToolId), Is.False, "another kind of tool asks first");
            Assert.That(((TerrainBrushTool)_host.Active).Asking, Is.True);
            Assert.That(notice, Is.Null, "nothing discarded");
            _host.Confirm(WorkStage.Plan);   // Revert
            Assert.That(_host.ActiveId, Is.EqualTo(NewLiftTool.ToolId), "then the lift tool opens");
            Assert.That(_ground.Shown, Is.SameAs(_history.Terrain), "on the ground as it was");
            Assert.That(_history.Document.Strokes, Is.Empty);
        }

        [UnityTest]
        public IEnumerator BR18_ConfirmOnTheAskThenCloses()
        {
            _host.Choose(TerrainIds.Raise);
            var tool = (TerrainBrushTool)_host.Active;
            Stroke(-20, 20, 0, right: false);
            _host.Escape();
            Assert.That(tool.Asking, Is.True);
            _host.Tick(At(0, 0, keys: ToolKeys.Next));   // Enter: Confirm
            var started = System.Diagnostics.Stopwatch.StartNew();
            while (_host.Armed && started.Elapsed.TotalSeconds < 60) { _host.Tick(default); yield return null; }
            Assert.That(_host.Armed, Is.False, "closed once the ground went in");
            Assert.That(_history.Document.Strokes.Count, Is.EqualTo(1));
        }

        [Test]
        public void BR18_AClosingTheGameMakesRevertsWithANotice()
        {
            string notice = null;
            _host.Notice += s => notice = s;
            _host.Choose(TerrainIds.Raise);
            Stroke(-20, 20, 0, right: false);
            int shows = _ground.ShowCalls;
            _host.Close();   // photo mode, the title, the Toolbox closing
            Assert.That(_ground.ShowCalls, Is.EqualTo(shows + 1));
            Assert.That(_ground.Shown, Is.SameAs(_history.Terrain), "back to the committed ground");
            Assert.That(notice, Does.Contain("reverted"));
        }
    }
}
