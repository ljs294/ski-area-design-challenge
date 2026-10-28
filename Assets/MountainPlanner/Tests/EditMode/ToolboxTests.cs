using System.Linq;
using System;
using System.Collections;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI;
using MountainPlanner.UI.Tools;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.InputSystem;
using UnityEngine.TestTools;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// The Toolbox's tool contract and its first two tools (task P3-06): one tool at a time (CF-01), Esc (CF-07), one
    /// confirmation a tick and stale commands (CF-04, CF-05), the review's words (CF-06), the preview cleared (CF-08),
    /// the keys (SH-07, LF-09) and an idle tool allocating nothing (CF-09). Everything here runs without a scene: a fake
    /// slope stands in for the terrain and a counting preview for the drawing.
    /// </summary>
    public sealed class ToolboxTests
    {
        const string TestPrefix = "MountainPlannerTest.P3-06.";

        /// <summary>A plane rising north at 15%: z (north) metres give 0.15 × z of height, over a 4 km square.</summary>
        sealed class Slope : ITerrainSurface
        {
            public Rect Footprint => new Rect(-2000, -2000, 4000, 4000);
            public float HeightAt(float x, float z) => Footprint.Contains(new Vector2(x, z)) ? 2000 + 0.15f * z : float.NaN;
        }

        sealed class CountingPreview : IToolPreview
        {
            public readonly HashSet<int> Shown = new HashSet<int>();
            public int Clears;
            public event Action Cleared;
            public void Line(int slot, Vector2[] xz, int count, PreviewInk ink, bool closed = false) => Shown.Add(slot);
            public void Ring(int slot, Vector2 centre, float radiusMetres, PreviewInk ink) => Shown.Add(slot);
            public void Dot(int slot, Vector2 at, PreviewInk ink) => Shown.Add(slot);
            public void Hide(int slot) => Shown.Remove(slot);
            public void HideFrom(int slot) { for (int i = slot; i < slot + 256; i++) Shown.Remove(i); }
            public void Clear() { Shown.Clear(); Clears++; Cleared?.Invoke(); }
        }

        /// <summary>A tool with two steps that claims Page Up while armed and counts what the host does to it.</summary>
        sealed class FakeTool : IDesignTool
        {
            public readonly string ToolIdValue;
            public int Arms, Disarms, Ticks, Commits;
            public int Step;
            public bool Work;
            public long StaleBy;
            ToolContext _ctx;
            public FakeTool(string id) { ToolIdValue = id; }
            public string Id => ToolIdValue;
            public string Name => "Fake " + ToolIdValue;
            public bool HasWork => Work;
            public void Arm(ToolContext context)
            {
                Arms++;
                _ctx = context;
                context.Claim(GameAction.TowerUp, true);
                context.Preview.Dot(0, Vector2.zero, PreviewInk.Ok);
                context.Window.SetCommit(true, true);
            }
            public void Tick(in ToolInput input) { Ticks++; if (input.Pressed(ToolKeys.Next)) Step = 1; }
            public ToolBackOut Escape()
            {
                if (Step > 0) { Step--; return ToolBackOut.Stepped; }
                return ToolBackOut.Close;
            }
            public DesignCommand Commit(WorkStage stage, long baseRevision) =>
                new AddLift(baseRevision - StaleBy, "Fake", LiftCatalogIds.SesselliftFgq4, new PointMm(0, 0), new PointMm(0, 200_000),
                            TowerMode.Automatic, TowerStance.Plumb, null, stage);
            public void Committed(CommandResult result) { Commits++; Step = 0; }
            public void Disarm() { Disarms++; _ctx = null; }
        }

        readonly List<IDisposable> _registered = new List<IDisposable>();
        DesignHistory _design;
        CountingPreview _preview;
        ToolHost _host;
        int _opened;

        [SetUp]
        public void SetUp()
        {
            SettingsStore.Prefix = TestPrefix;
            PlayerPrefs.DeleteKey(TestPrefix + "Keys");
            KeyBindings.Reload();
            KeyBindings.ClearClaims();
            _design = new DesignHistory(DesignDocument.Empty(0x5EED_0006));
            _preview = new CountingPreview();
            _opened = 0;
            _host = new ToolHost(_ => new ToolContext(_design, _preview, new Slope(), new Rect(-1500, -1500, 3000, 3000)), _ => _opened++);
        }

        [TearDown]
        public void TearDown()
        {
            _host.Close();
            foreach (var r in _registered) r.Dispose();
            _registered.Clear();
            KeyBindings.ClearClaims();
            PlayerPrefs.DeleteKey(TestPrefix + "Keys");
            SettingsStore.Prefix = SettingsStore.DefaultPrefix;
            KeyBindings.Reload();
        }

        FakeTool Fake(string id)
        {
            var tool = new FakeTool(id);
            _registered.Add(ToolCatalog.Register(id, "s-lift", "new", () => tool));
            return tool;
        }

        // ---------- the host (CF-01 to CF-08) ----------

        [Test]
        public void CF01_AnotherToolCancelsTheArmedOneOnceAndPutsEverythingBack()
        {
            var a = Fake("fake-a");
            var b = Fake("fake-b");
            Assert.That(_host.Choose("fake-a"), Is.True);
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.True, "the tool's key is claimed while armed");
            Assert.That(_preview.Shown, Is.Not.Empty);

            Assert.That(_host.Choose("fake-b"), Is.True);
            Assert.That(a.Disarms, Is.EqualTo(1), "disarmed exactly once");
            Assert.That(_host.Disarms, Is.EqualTo(1));
            Assert.That(_preview.Clears, Is.EqualTo(1), "the preview was cleared (CF-08)");
            Assert.That(_host.ActiveId, Is.EqualTo("fake-b"));
            Assert.That(b.Arms, Is.EqualTo(1));

            _host.Close();
            _host.Close();   // closing twice disarms once
            Assert.That(b.Disarms, Is.EqualTo(1));
            Assert.That(_host.Armed, Is.False);
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.False, "the camera has Page Up back");
            Assert.That(KeyBindings.IsClaimed(GameAction.ToolWindowKeys), Is.False, "and Analysis has Tab back");
        }

        [Test]
        public void ChoosingTheArmedToolAgainClosesIt()
        {
            var a = Fake("fake-a");
            _host.Choose("fake-a");
            _host.Choose("fake-a");
            Assert.That(_host.Armed, Is.False);
            Assert.That(a.Disarms, Is.EqualTo(1));
            Assert.That(_host.Choose("no-such-tool"), Is.False, "a greyed tool doesn't arm");
        }

        [Test]
        public void CF07_EscBacksOutAStepThenAsksBeforeDroppingWork()
        {
            var a = Fake("fake-a");
            _host.Choose("fake-a");
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            a.Work = true;
            Assert.That(_host.Escape(), Is.True);
            Assert.That(a.Step, Is.EqualTo(0), "back one step");
            Assert.That(_host.Armed, Is.True);
            Assert.That(_host.Escape(), Is.True);
            Assert.That(_host.Armed, Is.True, "drawn work asks first");
            Assert.That(_host.Escape(), Is.True);
            Assert.That(_host.Armed, Is.False, "the second Esc drops it");
            Assert.That(_host.Escape(), Is.False, "nothing armed: Esc goes on to the HUD");

            var b = Fake("fake-b");
            _host.Choose("fake-b");
            Assert.That(_host.Escape(), Is.True);
            Assert.That(_host.Armed, Is.False, "with nothing drawn, Esc at the first step closes at once");
        }

        [Test]
        public void CF04_OneConfirmationATick()
        {
            var a = Fake("fake-a");
            _host.Choose("fake-a");
            _host.Tick(default);
            long before = _design.Revision;
            Assert.That(_host.Confirm(WorkStage.Built).Accepted, Is.True);
            Assert.That(_host.Confirm(WorkStage.Built), Is.Null, "a second confirmation in the same tick is turned away");
            Assert.That(_design.Revision, Is.EqualTo(before + 1));
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(1));
            Assert.That(a.Commits, Is.EqualTo(1));
            _host.Tick(default);
            Assert.That(_host.Confirm(WorkStage.Plan).Accepted, Is.True, "the next tick may confirm again");
            Assert.That(_design.Document.Lifts[1].Stage, Is.EqualTo(WorkStage.Plan), "Save plan keeps it as a plan (CF-06)");
        }

        [Test]
        public void CF05_AStaleCommandIsRejectedWholeAndTheToolStays()
        {
            var a = Fake("fake-a");
            a.StaleBy = 1;
            _host.Choose("fake-a");
            _host.Tick(default);
            var result = _host.Confirm(WorkStage.Built);
            Assert.That(result.Accepted, Is.False);
            Assert.That(result.Code, Is.EqualTo(RejectionCode.Stale));
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(0));
            Assert.That(_host.Armed, Is.True, "the tool stays in review");
            Assert.That(_host.Context.Window.Message, Does.Contain("changed"));
            Assert.That(a.Commits, Is.EqualTo(0));
        }

        [Test]
        public void CF06_ABlockingWarningTurnsBreakGroundOff()
        {
            var review = new ReviewModel().Add("Length", "1,000 ft").Warn("Too steep", "GR-07", blocks: true).Ready(true, true);
            Assert.That(review.CanBreakGround, Is.False);
            Assert.That(review.CanSavePlan, Is.True, "a plan can still be saved");
            review.Clear().Warn("Cut and fill don't balance").Ready(true, true);
            Assert.That(review.CanBreakGround, Is.True, "a caution doesn't block");
        }

        // ---------- keys (SH-07, LF-09) ----------

        [Test]
        public void SH07_ToolKeysAreRebindableAndSwapWithTheCamera()
        {
            Assert.That(KeyBindings.Caption(GameAction.ToolNew), Is.EqualTo("N"));
            Assert.That(KeyBindings.Caption(GameAction.ToolNext), Is.EqualTo("Enter"));
            Assert.That(KeyBindings.Caption(GameAction.BreakGround), Is.EqualTo("Ctrl Enter"));
            Assert.That(KeyBindings.Caption(GameAction.TowerUp), Is.EqualTo("Page Up"));
            Assert.That(KeyBindings.Caption(GameAction.Erase), Is.EqualTo("Shift"));
            Assert.That(KeyBindings.Caption(GameAction.ToolEdit), Is.EqualTo("M"), "E rotates the camera (TL19)");
            // The camera's keys work with the tray open too (TL19), so a tray letter on W swaps with Move forward.
            Assert.That(KeyBindings.Bind(GameAction.ToolNew, 0, new KeyChord(Key.W)), Is.EqualTo(GameAction.MoveForward));
            Assert.That(KeyBindings.Get(GameAction.MoveForward, 0), Is.EqualTo(new KeyChord(Key.N)));
            // Two tray keys swap.
            Assert.That(KeyBindings.Bind(GameAction.ToolEdit, 0, new KeyChord(Key.W)), Is.EqualTo(GameAction.ToolNew));
            Assert.That(KeyBindings.Get(GameAction.ToolNew, 0), Is.EqualTo(new KeyChord(Key.M)));
            // A key a tool takes only while it claims it shadows the camera's instead of swapping (as Page Up does).
            Assert.That(KeyBindings.Bind(GameAction.TowerUp, 0, new KeyChord(Key.Home)), Is.Null);
            Assert.That(KeyBindings.Get(GameAction.ResetView, 0), Is.EqualTo(new KeyChord(Key.Home)));
        }

        [Test]
        public void EnterAndModifiersGoOnlyWhereTheRulesAllow()
        {
            Assert.That(KeyBindings.Refuses(GameAction.Pause, Key.Enter), Is.True, "Enter is the tools' Next step");
            Assert.That(KeyBindings.Refuses(GameAction.ToolNext, Key.Enter), Is.False);
            Assert.That(KeyBindings.Refuses(GameAction.ToolNext, Key.Escape), Is.True, "Esc always backs out");
            Assert.That(KeyBindings.Refuses(GameAction.Erase, Key.LeftCtrl), Is.False, "Erase is held like a modifier");
            Assert.That(KeyBindings.Refuses(GameAction.Pause, Key.LeftShift), Is.True);
            Assert.That(KeyBindings.Bind(GameAction.Pause, 0, new KeyChord(Key.Enter)), Is.Null);
            Assert.That(KeyBindings.Get(GameAction.Pause, 0), Is.EqualTo(new KeyChord(Key.Space)), "refused, nothing changed");
            KeyBindings.Bind(GameAction.Erase, 0, new KeyChord(Key.LeftAlt));
            Assert.That(KeyBindings.Caption(GameAction.Erase), Is.EqualTo("Alt"));
        }

        [Test]
        public void LF09_AClaimedToolKeyShadowsTheCameraKeyOnTheSameKey()
        {
            var keyboard = InputSystem.AddDevice<Keyboard>();
            try
            {
                InputSystem.QueueStateEvent(keyboard, new UnityEngine.InputSystem.LowLevel.KeyboardState(Key.PageUp));
                InputSystem.Update();
                Assert.That(KeyBindings.Held(keyboard, GameAction.RiseOrZoomIn), Is.True, "Page Up zooms with no tool");
                KeyBindings.Claim(GameAction.TowerUp, true);
                Assert.That(KeyBindings.Held(keyboard, GameAction.RiseOrZoomIn), Is.False, "a tower in hand takes Page Up");
                Assert.That(KeyBindings.Held(keyboard, GameAction.TowerUp), Is.True);
                Assert.That(KeyBindings.Held(keyboard, GameAction.MoveForward), Is.False);
                KeyBindings.ClearClaims();
                Assert.That(KeyBindings.Held(keyboard, GameAction.RiseOrZoomIn), Is.True, "and gives it back");
            }
            finally { InputSystem.RemoveDevice(keyboard); }
        }

        // ---------- New lift (LF-01 to LF-03, LF-08) ----------

        static ToolInput At(float x, float z, bool click = false)
        {
            var g = new Vector3(x, 2000 + 0.15f * z, z);
            return new ToolInput { HasGround = true, Ground = g, Point = DesignFrame.ToDesign(g), InCore = true, Click = click };
        }

        [Test]
        public void NewLift_BottomThenTopThenReviewBreaksGround()
        {
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            _host.Tick(At(0, -400));
            Assert.That(lift.Problem, Is.Null, "a 15% plane varies under 10 ft across a terminal");
            _host.Tick(At(0, -400, click: true));
            Assert.That(lift.CurrentStep, Is.EqualTo(1), "anchored: now the other terminal");

            _host.Tick(At(0, -700));
            Assert.That(lift.Problem, Does.Contain("higher"), "LF-03: the top can't be lower");
            _host.Tick(At(0, -380, click: true));
            Assert.That(lift.Problem, Does.Contain("300 ft"), "LF-08: 300 ft apart");
            Assert.That(lift.CurrentStep, Is.EqualTo(1), "a red ghost doesn't place");
            _host.Tick(At(0, 600, click: true));
            Assert.That(lift.CurrentStep, Is.EqualTo(2), "the line is fixed: towers");

            Assert.That(_host.Confirm(WorkStage.Built), Is.Null, "nothing to break ground on before the review");
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(lift.CurrentStep, Is.EqualTo(3));
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });   // Ctrl+Enter
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(1));
            var built = _design.Document.Lifts[0];
            Assert.That(built.Stage, Is.EqualTo(WorkStage.Built));
            Assert.That(built.Name, Is.EqualTo("Chair 1"));
            Assert.That(built.Bottom.Y, Is.LessThan(built.Top.Y), "the first terminal is the bottom one (LF-02)");
            Assert.That(lift.CurrentStep, Is.EqualTo(0), "the tool stays armed at step 1 (P3-06 Q4)");
            Assert.That(_host.Armed, Is.True);
            Assert.That(_design.Undo(), Is.True);
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(0), "undo takes it back");
        }

        [Test]
        public void NewLift_OutsideTheCoreOrOffTheGroundCantSit()
        {
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            _host.Tick(At(1600, 0, click: true));
            Assert.That(lift.Problem, Does.Contain("inside the area"), "SH-06");
            Assert.That(lift.CurrentStep, Is.EqualTo(0));
        }

        [Test]
        public void LT19_TheTerminalInHandTurnsAndTakesItsLevel()
        {
            // Task 09 (LT19–LT21): while a terminal is in hand, , and . (or Shift + wheel) turn it and Page Up / Page Down set
            // its level; the camera has those keys back once it's placed.
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            Assert.That(KeyBindings.IsClaimed(GameAction.TerminalLevelUp), Is.True, "the bottom terminal takes Page Up");
            Assert.That(KeyBindings.IsClaimed(GameAction.TerminalTurnLeft), Is.True);
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.False);
            var at = At(0, -400);
            at.Keys = ToolKeys.TurnLeft;
            _host.Tick(at);
            Assert.That(lift.TurnDegrees, Is.EqualTo(5).Within(1e-4));
            at.Keys = ToolKeys.TurnRight;
            at.Shift = true;
            _host.Tick(at);
            Assert.That(lift.TurnDegrees, Is.EqualTo(320).Within(1e-4), "Shift: 45°");
            at.Keys = ToolKeys.None;
            at.Shift = false;
            at.TurnNotches = 2;
            _host.Tick(at);
            Assert.That(lift.TurnDegrees, Is.EqualTo(350).Within(1e-4), "a wheel notch: 15°");
            at.TurnNotches = 0;
            at.Keys = ToolKeys.LevelUp;
            for (int i = 0; i < 3; i++) _host.Tick(at);
            Assert.That(lift.BottomLevelFeet, Is.EqualTo(3));
            at.Keys = ToolKeys.LevelDown;
            for (int i = 0; i < 40; i++) _host.Tick(at);
            Assert.That(lift.BottomLevelFeet, Is.EqualTo(-10), "10 ft into the hill at most");
            _host.Tick(At(0, -400, click: true));
            Assert.That(lift.CurrentStep, Is.EqualTo(1));
            Assert.That(KeyBindings.IsClaimed(GameAction.TerminalTurnLeft), Is.False, "the top faces the line (LT20)");
            Assert.That(KeyBindings.IsClaimed(GameAction.TerminalLevelUp), Is.True, "the top terminal's level");
            var top = At(0, 600);
            top.Keys = ToolKeys.LevelUp;
            _host.Tick(top);
            Assert.That(lift.TopLevelFeet, Is.EqualTo(1));
            _host.Tick(At(0, 600, click: true));
            Assert.That(KeyBindings.IsClaimed(GameAction.TerminalLevelUp), Is.False, "placed: Page Up zooms again");
            _host.Close();
        }

        [Test]
        public void LF09_PageUpRaisesTheTowerInHandOnlyByHand()
        {
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            lift.ChooseTowers(TowerMode.Automatic);
            _host.Tick(At(0, -400, click: true));
            _host.Tick(At(0, 600, click: true));
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.False, "Automatic: no tower in hand, Page Up zooms");
            lift.ChooseTowers(TowerMode.ByHand);
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.True, "By hand: the tower in hand takes Page Up");
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerDown), Is.True);
            _host.Tick(new ToolInput { Keys = ToolKeys.TowerUp });
            Assert.That(lift.TowerFeet, Is.EqualTo(40), "5 ft taller");
            for (int i = 0; i < 20; i++) _host.Tick(new ToolInput { Keys = ToolKeys.TowerUp });
            Assert.That(lift.TowerFeet, Is.EqualTo(100), "TW-02: 100 ft at most");
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.False, "the review lets go of it");
            lift.ChooseTowers(TowerMode.Automatic);   // the next test starts from Automatic
        }

        [Test]
        public void CF09_AnArmedIdleToolAllocatesNothing()
        {
            _host.Choose(NewLiftTool.ToolId);
            void Pass()
            {
                for (int i = 0; i < 600; i++)
                {
                    _host.Tick(At(10, -400));
                    _host.Tick(default);
                }
            }
            Pass();
            long before = GC.GetAllocatedBytesForCurrentThread();
            Pass();
            Assert.That(GC.GetAllocatedBytesForCurrentThread() - before, Is.EqualTo(0), "1,200 ticks allocated nothing");
        }

        // ---------- New trail (TF-01 to TF-03) ----------

        IEnumerator Analysed(NewTrailTool trail)
        {
            // The work runs on a worker thread; the host hands the result over on its next tick (CF-03). Batch-mode frames are
            // short, so the wait is by time, not frames.
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (trail.Analysing && watch.Elapsed.TotalSeconds < 30)
            {
                yield return null;
                _host.Tick(default);
            }
            Assert.That(trail.Analysing, Is.False, $"the area was worked out off the main thread ({_host.Context.Running} still running)");
        }

        /// <summary>Counts what the trail tool asks of the paint on the ground.</summary>
        sealed class CountingPaint : ITrailPaint
        {
            public int Redraws, Stamps;
            public void Align(MountainPlanner.Domain.Edits.EditLattice lattice, double originX, double originY) { }
            public void Stamp(Vector2 a, Vector2 b, float radius, bool erase) => Stamps++;
            public void Redraw(TrailShape baseShape, IReadOnlyList<Stroke> strokes, TrailFootprint footprint) => Redraws++;
            public void Clear() { }
            public void Cursor(Vector2 xz, float radius, bool erase, bool shown) { }
            public void Tint(Color colour) { }
        }

        [UnityTest]
        public IEnumerator NewTrail_AStrokeBeingPaintedIsNotRedrawnAway()
        {
            // Findings 2026-10-20, #3: the last stroke's footprint arriving mid-drag replayed only the finished strokes,
            // wiping the paint of the stroke being painted. It waits for that stroke's own let-go now.
            var paint = new CountingPaint();
            _host.Choose(NewTrailTool.ToolId);
            _host.Context.Paint = paint;   // the context is made when a tool is chosen
            var trail = (NewTrailTool)_host.Active;
            // A long, wide first stroke, so its footprint takes a while to work out.
            for (int i = 0; i < 20; i++) _host.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
            Assume.That(trail.BrushFeet, Is.EqualTo(200));
            var down = At(0, 600, click: true);
            down.Drag = true;
            _host.Tick(down);
            for (int z = 590; z >= -600; z -= 10) { var d = At(0, z); d.Drag = true; _host.Tick(d); }
            var up = At(0, -600);
            up.Release = true;
            _host.Tick(up);
            // The second stroke starts before the first's footprint is worked out, and its result lands mid-drag.
            var second = At(300, 300, click: true);
            second.Drag = true;
            _host.Tick(second);
            // The host hands a finished result over before the tool's tick, so one done by the click redraws before the
            // stroke begins (rightly): only what lands mid-drag counts.
            Assume.That(trail.Analysing, "the first footprint was still being worked out when the second stroke began");
            int before = paint.Redraws;
            for (int i = 0; i < 600 && trail.Analysing; i++)
            {
                yield return null;
                var d = At(300, 300 - i % 50);
                d.Drag = true;
                _host.Tick(d);
            }
            Assert.That(trail.Analysing, Is.False);
            Assert.That(paint.Redraws, Is.EqualTo(before), "no redraw while the second stroke paints");
            var lift = At(300, 250);
            lift.Release = true;
            _host.Tick(lift);
            yield return Analysed(trail);
            Assert.That(paint.Redraws, Is.EqualTo(before + 1), "the let-go redraws both strokes");
            Assert.That(trail.Strokes, Is.EqualTo(2));
            for (int i = 0; i < 20; i++) _host.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });   // the brush carries over
        }

        [UnityTest]
        public IEnumerator NewTrail_PaintHeadTailReviewBreaksGround()
        {
            _host.Choose(NewTrailTool.ToolId);
            var trail = (NewTrailTool)_host.Active;
            Assert.That(trail.BrushFeet, Is.EqualTo(100));
            _host.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
            Assert.That(trail.BrushFeet, Is.EqualTo(105), "] widens the brush 5 ft");
            _host.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });

            // One stroke down the fall line, 600 m.
            var input = At(0, 300, click: true);
            input.Drag = true;
            _host.Tick(input);
            for (int z = 290; z >= -300; z -= 10)
            {
                var drag = At(0, z);
                drag.Drag = true;
                _host.Tick(drag);
            }
            var up = At(0, -300);
            up.Release = true;
            _host.Tick(up);
            Assert.That(trail.Strokes, Is.EqualTo(1));
            yield return Analysed(trail);
            Assert.That(trail.Raster.Patches, Is.EqualTo(1));
            Assert.That(trail.Raster.AreaSquareMetres, Is.InRange(600 * 30.48 * 0.9, 630 * 30.48 * 1.1), "about 600 m × 100 ft");

            // A second, separate patch blocks Next (TF-02); Ctrl+Z takes it back.
            var dab = At(400, 0, click: true);
            _host.Tick(dab);
            var lift = At(400, 0);
            lift.Release = true;
            _host.Tick(lift);
            yield return Analysed(trail);
            Assert.That(trail.Raster.Patches, Is.EqualTo(2));
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(trail.CurrentStep, Is.EqualTo(0), "Next waits while a patch is separate");
            _host.Tick(new ToolInput { Keys = ToolKeys.UndoStroke });
            yield return Analysed(trail);
            Assert.That(trail.Strokes, Is.EqualTo(1));

            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(trail.CurrentStep, Is.EqualTo(1), "head and tail");
            _host.Tick(At(900, 0, click: true));
            Assert.That(_host.Context.Window.Message, Does.Contain("painted trail"), "an end goes on the trail");
            _host.Tick(At(0, 280, click: true));
            _host.Tick(At(0, -280, click: true));
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(trail.CurrentStep, Is.EqualTo(2), "review");
            Assert.That(_host.Confirm(WorkStage.Built).Accepted, Is.True);
            var built = _design.Document.Trails[0];
            Assert.That(built.Name, Is.EqualTo("Run 1"));
            Assert.That(built.HasEnds, Is.True);
            Assert.That(built.Shape.Rings.Count, Is.EqualTo(1), "the trail keeps its clearing's outline, not the stroke (finding 7)");
            Assert.That(trail.CurrentStep, Is.EqualTo(0), "ready for the next trail");
        }

        // ---------- Edit and Remove trail (task 08, TF-07) ----------

        TrailId BuiltTrail()
        {
            var parts = TrailShape.FromStrokes(null, new[] { new Stroke(DesignUnits.FeetToMm(100), false, new[] { DesignFrame.ToDesign(new Vector2(0, 300)), DesignFrame.ToDesign(new Vector2(0, -300)) }) });
            var r = _design.Submit(new AddTrail(_design.Revision, "Run 1", TrailSettings.NaturalRun, parts,
                DesignFrame.ToDesign(new Vector2(0, 280)), DesignFrame.ToDesign(new Vector2(0, -280)), WorkStage.Built));
            Assert.That(r.Accepted, Is.True, r.ToString());
            return new TrailId(r.Created[0]);
        }

        [Test]
        public void TF07_EditTrailPicksATrailAndRegradesItInOneUndo()
        {
            var id = BuiltTrail();
            _host.Choose(EditTrailTool.ToolId);
            var tool = (EditTrailTool)_host.Active;
            Assert.That(tool.CurrentStep, Is.EqualTo(0), "Pick");
            _host.Tick(At(5, 0, click: true));
            Assert.That(tool.Target?.Id, Is.EqualTo(id), "a click on the trail picks it");
            Assert.That(tool.Graded, Is.False, "it keeps its own setting (GR-14)");
            tool.ChooseGraded(true);
            Assert.That(tool.CurrentStep, Is.EqualTo(3), "Natural to Graded goes straight to review");
            Assert.That(_host.Confirm(WorkStage.Built).Accepted, Is.True);
            Assert.That(_design.Document.FindTrail(id).Settings.Grading, Is.EqualTo(TrailGrading.Graded));
            Assert.That(_design.UndoLabel, Is.EqualTo("Edit trail"));
            // NT47: picked again, its Graded box is ticked and locked; unticking does nothing.
            _host.Tick(At(5, 0, click: true));
            Assert.That(tool.Target?.Id, Is.EqualTo(id));
            Assert.That(tool.Graded, Is.True);
            tool.ChooseGraded(false);
            Assert.That(tool.Graded, Is.True, "grading is permanent once built");
            Assert.That(tool.CurrentStep, Is.EqualTo(0), "and nothing goes to review");
            _host.Close();
            _design.Undo();
            Assert.That(_design.Document.FindTrail(id).Settings.Grading, Is.EqualTo(TrailGrading.Natural), "one undo puts it back");
        }

        [UnityTest]
        public IEnumerator TF07_RepaintPaintsOntoTheTrailsOwnClearing()
        {
            // Sections finding 7: the trail keeps its clearing, not strokes; Repaint paints onto it (and may rub out of it
            // from the first stroke), and Apply sends the new clearing in one undo.
            var id = BuiltTrail();
            var before = _design.Document.FindTrail(id).Shape;
            _host.Choose(EditTrailTool.ToolId);
            var tool = (EditTrailTool)_host.Active;
            _host.Tick(At(5, 0, click: true));
            Assert.That(tool.Target?.Id, Is.EqualTo(id));
            Assert.That(tool.Strokes, Is.EqualTo(0), "the picked trail brings no strokes");
            tool.RepaintForCapture();   // the Repaint button
            Assert.That(tool.CurrentStep, Is.EqualTo(0).Or.EqualTo(1));
            // Rub out an island in the middle with a 25 ft brush: an erase first, on the trail's own clearing.
            for (int k = 0; k < 15; k++) _host.Tick(new ToolInput { Keys = ToolKeys.BrushNarrower });
            Assert.That(tool.BrushFeet, Is.EqualTo(25));
            var rub = At(0, 0, click: true);
            rub.Erase = true;
            _host.Tick(rub);
            var up = At(0, 0);
            up.Release = true;
            _host.Tick(up);
            // And paint a spur onto it, 100 ft wide again.
            for (int k = 0; k < 15; k++) _host.Tick(new ToolInput { Keys = ToolKeys.BrushWider });
            var spur = At(0, 100, click: true);
            spur.Drag = true;
            _host.Tick(spur);
            for (int x = 10; x <= 120; x += 10) { var d = At(x, 100); d.Drag = true; _host.Tick(d); }
            var end = At(120, 100);
            end.Release = true;
            _host.Tick(end);
            Assert.That(tool.Strokes, Is.EqualTo(2));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            while (tool.Analysing && watch.Elapsed.TotalSeconds < 30) { yield return null; _host.Tick(default); }
            Assert.That(tool.Raster.Contains(DesignFrame.ToDesign(new Vector2(0, 0))), Is.False, "the island is rubbed out");
            Assert.That(tool.Raster.Contains(DesignFrame.ToDesign(new Vector2(100, 100))), Is.True, "the spur is painted on");
            Assert.That(tool.Raster.Contains(DesignFrame.ToDesign(new Vector2(0, -200))), Is.True, "the rest of the clearing stays");
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(tool.CurrentStep, Is.EqualTo(2), "head and tail again");
            _host.Tick(At(0, 280, click: true));
            _host.Tick(At(0, -280, click: true));
            watch.Restart();
            while (tool.CurrentStep != 3 && watch.Elapsed.TotalSeconds < 30) { yield return null; _host.Tick(new ToolInput { Keys = ToolKeys.Next }); }
            Assert.That(tool.CurrentStep, Is.EqualTo(3), "review");
            Assert.That(_host.Confirm(WorkStage.Built).Accepted, Is.True);
            var after = _design.Document.FindTrail(id).Shape;
            Assert.That(after.Equals(before), Is.False);
            Assert.That(after.Rings.Count, Is.EqualTo(2), "an outer ring and the island's hole");
            _design.Undo();
            Assert.That(_design.Document.FindTrail(id).Shape, Is.EqualTo(before), "one undo puts the clearing back");
        }

        [Test]
        public void TF07_RemoveTrailPicksATrailAndRemovesIt()
        {
            var id = BuiltTrail();
            _host.Choose(RemoveTrailTool.ToolId);
            var tool = (RemoveTrailTool)_host.Active;
            _host.Tick(At(-5, 100, click: true));
            Assert.That(tool.CurrentStep, Is.EqualTo(1), "its review");
            Assert.That(_host.Confirm(WorkStage.Built).Accepted, Is.True);
            Assert.That(_design.Document.FindTrail(id), Is.Null);
            _design.Undo();
            Assert.That(_design.Document.FindTrail(id), Is.Not.Null, "Ctrl+Z brings it back");
        }

        [Test]
        public void GR14_ANewTrailStartsNatural()
        {
            _host.Choose(NewTrailTool.ToolId);
            Assert.That(((NewTrailTool)_host.Active).Graded, Is.False);
        }

        [Test]
        public void TR05_ZoomedFarOutSnapsWithinSixPixels()
        {
            Assert.That(ToolContext.ReachFor(0), Is.EqualTo(DesignUnits.SnapMm), "30 ft up close");
            Assert.That(ToolContext.ReachFor(1), Is.EqualTo(DesignUnits.SnapMm), "6 px of 1 m each is still under 30 ft");
            Assert.That(ToolContext.ReachFor(4), Is.EqualTo(24000), "6 px of 4 m each: 24 m");
            var r = _design.Submit(new AddLift(_design.Revision, "Chair 1", LiftCatalogIds.SesselliftFgq4,
                DesignFrame.ToDesign(new Vector2(0, -600)), DesignFrame.ToDesign(new Vector2(0, 300)), TowerMode.Automatic, TowerStance.Plumb, null, WorkStage.Built));
            Assert.That(r.Accepted, Is.True);
            _host.Choose(NewTrailTool.ToolId);
            var near = DesignFrame.ToDesign(new Vector2(0, 280));   // 20 m (66 ft) off the top
            _host.Tick(default);
            Assert.That(_host.Context.Snap(near, SnapTargets.All).Snapped, Is.False, "up close, 66 ft is too far");
            _host.Tick(new ToolInput { MetresPerPixel = 4 });
            var hit = _host.Context.Snap(near, SnapTargets.All);
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.LiftTop), "zoomed out, 6 px reach it");
            Assert.That(DesignFrame.ToLocal(hit.At), Is.EqualTo(new Vector2(0, 300)), "and the end lands exactly on the top");
        }

        [Test]
        public void NewLift_TheTypeMenuPicksKindCarrierAndMaker()
        {
            // LT13 and the approved mockup: Type, then Chair (Cabin for gondolas and trams), then Model; every choice a dropdown.
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            var w = _host.Context.Window.Window;
            var kind = w.Q<UnityEngine.UIElements.Button>("toolwin-dd-kind");
            Assert.That(lift.CatalogId, Is.EqualTo(LiftCatalogIds.SesselliftFgq4), "the Sessellift quad first");
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-kind-value").text, Is.EqualTo("Fixed-grip chair"));
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-carrier-value").text, Is.EqualTo("Quad"));
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-model-value").text, Is.EqualTo("Sessellift FGQ-4"));
            lift.Choose(LiftCatalogIds.MontaFg4);
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-model-value").text, Is.EqualTo("Monta FG4"));
            lift.Choose(LiftCatalogIds.Of("gondola-8", "monta"));
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-kind-value").text, Is.EqualTo("Detachable gondola"));
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-carrier-value").text, Is.EqualTo("8 person"));
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-model-value").text, Does.StartWith("Monta "));
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-note-capacity").text, Does.Contain("every"), "a detachable's interval (LT41)");
            Assert.That(kind, Is.Not.Null);

            // Through the menus as a click would (LT41): the trams are placeholders, greyed; the detachable chair is the D6.
            var dd = ((LiftTypeMenu)typeof(NewLiftTool).GetField("_typeMenu", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).GetValue(lift)).Kind;
            dd.Choose(0);
            Assert.That(lift.CatalogId, Is.EqualTo(LiftCatalogIds.SesselliftFgq4), "back to the fixed-grip quad");
            dd.Choose(3);
            Assert.That(lift.CatalogId, Is.EqualTo(LiftCatalogIds.SesselliftFgq4), "a tram can't be picked: in the menu only");
            dd.Choose(1);
            Assert.That(lift.CatalogId, Is.EqualTo(LiftCatalogIds.SesselliftD6), "the detachable chair: the D6");
            Assert.That(w.Q<UnityEngine.UIElements.Label>("toolwin-dd-model-value").text, Is.EqualTo("Sessellift D6"));

            // The chosen type is what Break ground builds.
            lift.Choose(LiftCatalogIds.SesselliftFgq4);
            lift.Choose(LiftCatalogIds.SesselliftD6);
            _host.Tick(At(0, -400, click: true));
            _host.Tick(At(0, 600, click: true));
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });
            Assert.That(_design.Document.Lifts[0].CatalogId, Is.EqualTo(LiftCatalogIds.SesselliftD6));
        }

        [Test]
        public void ToolDropdown_OpensOverTheWindowAndEscClosesItFirst()
        {
            _host.Choose(NewLiftTool.ToolId);
            var w = _host.Context.Window;
            var lift = (NewLiftTool)_host.Active;
            var dd = lift.CarrierMenu;
            dd.Show();
            var menu = w.Window.Q("toolwin-dd-carrier-menu");
            Assert.That(menu, Is.Not.Null, "the menu opens");
            Assert.That(menu.Query<UnityEngine.UIElements.Button>().ToList().Count, Is.EqualTo(3), "Double, Triple, Quad");
            Assert.That(_host.Escape(), Is.True);
            Assert.That(w.Window.Q("toolwin-dd-carrier-menu"), Is.Null, "Esc closes the menu first");
            Assert.That(_host.Armed, Is.True, "and leaves the tool alone");
        }

        // ---------- Towers by hand (task 09, LF-04) ----------

        /// <summary>The slope as the edited ground: design points map to (x, z) metres, height 2000 + 0.15 z.</summary>
        sealed class SlopeHeights : MountainPlanner.Domain.Edits.IHeightSource
        {
            public double At(double x, double y) => 2000 + 0.15 * y;
        }

        void GiveGround()
        {
            var ground = new MountainPlanner.Domain.Edits.EditedHeights(new SlopeHeights(), new MountainPlanner.Domain.Edits.EditLattice(-3000.5, 3000.5),
                                                                        MountainPlanner.Domain.Edits.TerrainEdits.Empty);
            _host.Context.EditedGround = () => ground;
            _host.Context.ToGround = p => (p.XMetres, p.YMetres);
        }

        void PumpUntil(Func<bool> done, string what)
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            while (!done() && clock.ElapsedMilliseconds < 60_000) { System.Threading.Thread.Sleep(10); _host.Tick(default); }
            Assert.That(done(), Is.True, what);
        }

        [Test]
        public void ByHand_PlaceATowerTakeItBackFillTheRestAndBreakGround()
        {
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            lift.ChooseTowers(TowerMode.ByHand);
            GiveGround();
            _host.Tick(At(0, -400, click: true));
            _host.Tick(At(0, 600, click: true));
            Assert.That(lift.CurrentStep, Is.EqualTo(2), "towers");
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerSlideOn), Is.True, "← → slide the tower in hand");
            _host.Tick(new ToolInput { Keys = ToolKeys.None });
            Assert.That(lift.HandStatus, Is.Not.Null, "a tower in hand");
            // Slide it up the line until it's green, then Enter places it.
            for (int i = 0; i < 400 && !lift.HandStatus.Ok; i++) _host.Tick(new ToolInput { Keys = ToolKeys.SlideOn });
            Assert.That(lift.HandStatus.Ok, Is.True, "somewhere up the line it can stand");
            double at = lift.HandAlong;
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(lift.Placed.Count, Is.EqualTo(1), "Enter placed it");
            Assert.That(lift.Placed[0].AlongMm / 1000.0, Is.EqualTo(at).Within(0.01));
            Assert.That(lift.HandAlong, Is.GreaterThan(at), "the next one starts past it");
            _host.Tick(new ToolInput { Keys = ToolKeys.Delete });
            Assert.That(lift.Placed.Count, Is.EqualTo(0), "Delete takes it back");
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(lift.CurrentStep, Is.EqualTo(2), "no towers: Enter places, it doesn't skip to the review");

            // Fill the rest finishes the line, then the review builds on exactly those towers.
            typeof(NewLiftTool).GetMethod("FillTheRest", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(lift, null);
            PumpUntil(() => lift.CurrentStep == 3, "Fill the rest goes on to the review");
            PumpUntil(() => lift.Built != null, "the review builds");
            Assert.That(lift.Built.Towers.Select(t => t.AlongMm), Is.EqualTo(lift.Placed.Select(t => t.AlongMm)), "built on the towers placed");
            Assert.That(lift.Built.Ok, Is.True, string.Join("; ", lift.Built.Problems));
            int count = lift.Built.Towers.Count;
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(1));
            var built = _design.Document.Lifts[0];
            Assert.That(built.TowerMode, Is.EqualTo(TowerMode.ByHand));
            Assert.That(built.Towers.Count, Is.EqualTo(count));
            Assert.That(lift.Placed.Count, Is.EqualTo(0), "the next lift starts with none");
            lift.ChooseTowers(TowerMode.Automatic);   // the next test starts from Automatic
        }

        // ---------- Edit lift (task 09, LT2) ----------

        LiftId BuiltOnSlope()
        {
            var ground = new MountainPlanner.Domain.Edits.EditedHeights(new SlopeHeights(), new MountainPlanner.Domain.Edits.EditLattice(-3000.5, 3000.5),
                                                                        MountainPlanner.Domain.Edits.TerrainEdits.Empty);
            var quad = LiftType.Of(LiftCatalogIds.SesselliftFgq4);
            var b = LiftBuild.Build(quad, ground, 0, -400, 0, 600, TowerStance.Plumb);
            var r = _design.Submit(new AddLift(_design.Revision, "Chair 1", quad.CatalogId, DesignFrame.ToDesign(new Vector2(0, -400)), DesignFrame.ToDesign(new Vector2(0, 600)),
                                               TowerMode.Automatic, TowerStance.Plumb, b.Towers, WorkStage.Built, b.Levels), b.Edits);
            Assert.That(r.Accepted, Is.True, r.Reason);
            return new LiftId(r.Created[0]);
        }

        [Test]
        public void EditLift_ARaisedTowerAppliesAsOneStepAndUndoesWhole()
        {
            var id = BuiltOnSlope();
            var before = _design.Document.FindLift(id);
            Assert.That(ToolCatalog.IsLive(EditLiftTool.ToolId), Is.True);
            _host.Choose(EditLiftTool.ToolId);
            var tool = (EditLiftTool)_host.Active;
            GiveGround();
            _host.Context.LiftUnderPointer = () => id.Value;
            _host.Tick(At(5, 0, click: true));
            Assert.That(tool.Editing, Is.True, "a click on the lit lift opens it");
            Assert.That(_host.Context.Window.Window.Q<Label>("toolwin-dd-model-value").text, Is.EqualTo("Sessellift FGQ-4"));

            // A tower 5 ft taller: one change, By hand now; Ctrl+Z takes it back inside the tool.
            int mid = before.Towers.Count / 2;
            tool.SelectTower(mid);
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerUp), Is.True, "Page Up is the selected tower's");
            _host.Tick(new ToolInput { Keys = ToolKeys.TowerUp });
            Assert.That(tool.Changes, Is.EqualTo(1));
            Assert.That(tool.DraftTowers[mid].HeightMm, Is.EqualTo(before.Towers[mid].HeightMm + DesignUnits.FiveFeetMm));
            _host.Tick(new ToolInput { Keys = ToolKeys.UndoStroke });
            Assert.That(tool.Changes, Is.EqualTo(0), "Ctrl+Z inside the tool");
            Assert.That(tool.DraftTowers[mid].HeightMm, Is.EqualTo(before.Towers[mid].HeightMm));

            // Again, then Apply once the draft is built.
            _host.Tick(new ToolInput { Keys = ToolKeys.TowerUp });
            _host.Tick(new ToolInput { Keys = ToolKeys.TowerUp });
            PumpUntil(() => tool.Built != null && _host.Context.Window.CanBreakGround, "the draft builds and can be applied");
            int revision = (int)_design.Revision;
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });
            var after = _design.Document.FindLift(id);
            Assert.That(_design.Revision, Is.EqualTo(revision + 1), "one command for the whole edit");
            Assert.That(after.Towers[mid].HeightMm, Is.EqualTo(before.Towers[mid].HeightMm + 2 * DesignUnits.FiveFeetMm));
            Assert.That(after.TowerMode, Is.EqualTo(TowerMode.ByHand), "a tower changed by hand makes it By hand");
            Assert.That(after.Towers[mid].Id, Is.EqualTo(before.Towers[mid].Id), "the tower keeps its ID");
            Assert.That(tool.Editing, Is.False, "back to picking the next lift");
            Assert.That(_design.Undo(), Is.True);
            Assert.That(_design.Document.FindLift(id).Towers[mid].HeightMm, Is.EqualTo(before.Towers[mid].HeightMm), "one Ctrl+Z undoes the edit");
        }

        [Test]
        public void EditLift_CancelAsksOnceWhenThereAreChanges()
        {
            var id = BuiltOnSlope();
            var levels = _design.Document.FindLift(id).Levels;
            long revision = _design.Revision;
            _host.Choose(EditLiftTool.ToolId);
            var tool = (EditLiftTool)_host.Active;
            GiveGround();
            Assert.That(tool.Open(id.Value), Is.True);
            tool.SelectTerminal(bottom: true);
            Assert.That(KeyBindings.IsClaimed(GameAction.TerminalLevelUp), Is.True);
            PumpUntil(() => tool.Built != null, "the draft builds");
            _host.Tick(new ToolInput { Keys = ToolKeys.LevelUp });
            Assert.That(tool.Changes, Is.EqualTo(1), "the bottom terminal 1 ft up");
            Assert.That(_host.Escape(), Is.True);   // deselects
            Assert.That(_host.Escape(), Is.True);   // asks
            Assert.That(tool.Editing, Is.True, "the first Esc with changes only asks");
            Assert.That(_host.Escape(), Is.True);
            Assert.That(tool.Editing, Is.False, "the second drops the draft");
            Assert.That(_host.Armed, Is.True, "the tool stays out");
            Assert.That(_design.Revision, Is.EqualTo(revision), "nothing applied");
            Assert.That(_design.Document.FindLift(id).Levels, Is.EqualTo(levels));
        }

        // ---------- one tower editor, naming (task 09, F1–F3, F5; LT37, LT39) ----------

        void Invoke(object tool, string method) =>
            tool.GetType().GetMethod(method, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance).Invoke(tool, null);

        [Test]
        public void E6_KTurnsASelectedTowersKindRoundAndApplyKeepsIt()
        {
            // LT44 (owner, second review E6): the player chooses a tower's kind; the game sizes it.
            var id = BuiltOnSlope();
            var before = _design.Document.FindLift(id);
            _host.Choose(EditLiftTool.ToolId);
            var tool = (EditLiftTool)_host.Active;
            GiveGround();
            Assert.That(tool.Open(id.Value), Is.True);
            PumpUntil(() => tool.Built != null, "the draft builds");
            int mid = before.Towers.Count / 2;
            tool.SelectTower(mid);
            Assert.That(KeyBindings.IsClaimed(GameAction.TowerKind), Is.True, "K is the selected tower's");
            Assert.That(tool.Editor.Describe(), Does.Contain("Kind: Auto"));
            var round = new List<TowerKind>();
            for (int k = 0; k < 4; k++)
            {
                _host.Tick(new ToolInput { Keys = ToolKeys.TowerKind });
                round.Add(tool.DraftTowers[mid].Kind);
            }
            Assert.That(round, Is.EqualTo(new[] { TowerKind.Support, TowerKind.HoldDown, TowerKind.Combination, TowerKind.Auto }));
            // A support where the rope presses down on a slope: within the rules, and Apply keeps it.
            _host.Tick(new ToolInput { Keys = ToolKeys.TowerKind });
            Assert.That(tool.Editor.Describe(), Does.Contain("Kind: support"));
            PumpUntil(() => tool.Built != null && _host.Context.Window.CanBreakGround, "the draft builds and can be applied");
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });
            var after = _design.Document.FindLift(id);
            Assert.That(after.Towers[mid].Kind, Is.EqualTo(TowerKind.Support), "stored with the tower");
            Assert.That(after.Towers.Where((t, i) => i != mid).All(t => t.Kind == TowerKind.Auto), Is.True);
        }

        [Test]
        public void E5_AClickOnTheRopeBesideATowerSelectsTheTower()
        {
            // Finding E5: the rope's pick (or a terminal's) under the pointer gave the click to the line, so a tower came into
            // hand instead of the one clicked being selected. A tower within a few pixels of the click takes it.
            var id = BuiltOnSlope();
            var before = _design.Document.FindLift(id);
            _host.Choose(EditLiftTool.ToolId);
            var tool = (EditLiftTool)_host.Active;
            GiveGround();
            Assert.That(tool.Open(id.Value), Is.True);
            PumpUntil(() => tool.Built != null, "the draft builds");
            int mid = before.Towers.Count / 2;
            var e = tool.Editor;
            var at = e.BottomXz + (e.TopXz - e.BottomXz).normalized * (before.Towers[mid].AlongMm / 1000f);
            _host.Context.DraftPartUnderPointer = () => MountainPlanner.Presentation.Lifts.LiftView.PartRope;
            _host.Tick(At(at.x + 1, at.y, click: true));
            _host.Tick(new ToolInput { Release = true });
            Assert.That(e.Adding, Is.False, "no tower in hand");
            Assert.That(tool.Selected, Is.EqualTo("tower " + mid), "the tower beside the click is selected");
            // Mid-span, away from any tower, a click on the rope still puts a tower in hand (F2).
            Assert.That(_host.Escape(), Is.True);   // deselects
            double gapMid = (before.Towers[mid].AlongMm + before.Towers[mid + 1].AlongMm) / 2000.0;
            var span = e.BottomXz + (e.TopXz - e.BottomXz).normalized * (float)gapMid;
            Assume.That(before.Towers[mid + 1].AlongMm - before.Towers[mid].AlongMm, Is.GreaterThan(30_000), "a span long enough to click in");
            _host.Tick(At(span.x, span.y, click: true));
            Assert.That(e.Adding, Is.True, "a click on the line between towers adds one");
        }

        [Test]
        public void F1F2_EditLiftPicksATowerIn3DAddsOneInAGapRemovesItAndRevertsToAuto()
        {
            var id = BuiltOnSlope();
            var before = _design.Document.FindLift(id);
            _host.Choose(EditLiftTool.ToolId);
            var tool = (EditLiftTool)_host.Active;
            GiveGround();
            Assert.That(tool.Open(id.Value), Is.True);
            PumpUntil(() => tool.Built != null, "the draft builds");
            int n = before.Towers.Count, mid = n / 2;

            // F1: a click selects what the pointer is on in 3D (the draft's part), not what's on the ground behind it.
            _host.Context.DraftPartUnderPointer = () => mid;
            _host.Tick(At(300, 300, click: true));
            _host.Tick(new ToolInput { Release = true });
            Assert.That(tool.Selected, Is.EqualTo("tower " + mid));
            _host.Context.DraftPartUnderPointer = () => LiftDraftEditor.None;
            _host.Tick(new ToolInput { Keys = ToolKeys.TowerUp });
            Assert.That(tool.DraftTowers[mid].HeightMm, Is.EqualTo(before.Towers[mid].HeightMm + DesignUnits.FiveFeetMm), "Page Up: 5 ft taller");
            _host.Tick(new ToolInput { Keys = ToolKeys.SlideOn });
            Assert.That(tool.DraftTowers[mid].AlongMm, Is.EqualTo(before.Towers[mid].AlongMm + DesignUnits.FiveFeetMm).Or.EqualTo(before.Towers[mid].AlongMm),
                        ". slides it 5 ft (or it's already 50 ft from the next)");
            int changes = tool.Changes;

            // F2: Insert puts a tower in hand in the widest gap; it's placed where it's green.
            _host.Tick(new ToolInput { Keys = ToolKeys.AddTower });
            Assert.That(tool.Editor.Adding, Is.True, "Insert: a tower in hand");
            for (int i = 0; i < 400 && (tool.Editor.HandStatus == null || !tool.Editor.HandStatus.Ok); i++) _host.Tick(new ToolInput { Keys = ToolKeys.SlideOn });
            Assume.That(tool.Editor.HandStatus != null && tool.Editor.HandStatus.Ok, "somewhere in a gap it can stand");
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            Assert.That(tool.DraftTowers.Count, Is.EqualTo(n + 1), "Enter placed it between its neighbours");
            Assert.That(tool.DraftTowers.Zip(tool.DraftTowers.Skip(1), (a, b) => b.AlongMm - a.AlongMm).All(d => d >= DesignUnits.TowerGapMm), Is.True, "in order, 50 ft apart");
            int added = tool.Editor.Selected;
            Assert.That(added, Is.GreaterThanOrEqualTo(0), "the new tower is selected");
            _host.Tick(new ToolInput { Keys = ToolKeys.Delete });
            Assert.That(tool.DraftTowers.Count, Is.EqualTo(n), "Delete removes it");
            _host.Tick(new ToolInput { Keys = ToolKeys.UndoStroke });
            Assert.That(tool.DraftTowers.Count, Is.EqualTo(n + 1), "Ctrl+Z puts it back");

            // F3: Revert to auto asks first, then places every tower again and the lift is Automatic.
            Invoke(tool, "RevertToAuto");
            Assert.That(tool.Changes, Is.EqualTo(changes + 1), "the first press only asks");
            Invoke(tool, "RevertToAuto");
            PumpUntil(() => tool.Built != null, "the re-placed draft builds");
            Assert.That(_host.Context.Window.Window.Q<Label>("toolwin-dd-mode-value").text, Is.EqualTo("Automatic"));
            Assert.That(tool.DraftTowers.Select(t => t.AlongMm), Is.EqualTo(before.Towers.Select(t => t.AlongMm)), "the automatic towers again");
        }

        [Test]
        public void F5_NewLiftsNameIsTypedInTheTitleAndReachesAddLift()
        {
            _host.Choose(NewLiftTool.ToolId);
            var lift = (NewLiftTool)_host.Active;
            var w = _host.Context.Window;
            w.BeginRename();
            Assert.That(w.Renaming, Is.True, "a click on the name (or F2) types a new one");
            var field = w.Window.Q<TextField>("toolwin-name-field");
            Assert.That(field.ClassListContains(InlineName.FieldClass), Is.True, "sections finding 8: typed in place, not in a box");
            Assert.That(w.Window.Q<Label>("toolwin-name").ClassListContains("hidden"), Is.True, "the field takes the name's spot");
            Assert.That(field.parent, Is.SameAs(w.Window.Q<Label>("toolwin-name").parent));
            w.Window.Q<TextField>("toolwin-name-field").value = " ";
            w.EndRename(true);
            Assert.That(lift.LiftName, Is.EqualTo("Chair 1"), "a bad name isn't kept");
            Assert.That(w.Message, Does.Contain("empty"), "and says why");
            w.BeginRename();
            w.Window.Q<TextField>("toolwin-name-field").value = "Summit Quad";
            w.EndRename(false);
            Assert.That(lift.LiftName, Is.EqualTo("Chair 1"), "Esc puts the old one back");
            w.BeginRename();
            w.Window.Q<TextField>("toolwin-name-field").value = "Summit Quad";
            w.EndRename(true);
            Assert.That(lift.LiftName, Is.EqualTo("Summit Quad"));
            Assert.That(w.Window.Q<Label>("toolwin-name").text, Is.EqualTo("Summit Quad"));
            _host.Tick(At(0, -400, click: true));
            _host.Tick(At(0, 600, click: true));
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });
            Assert.That(_design.Document.Lifts[0].Name, Is.EqualTo("Summit Quad"), "Break ground's AddLift carries it");
            Assert.That(lift.LiftName, Is.EqualTo("Chair 2"), "the next lift's default");
        }

        [Test]
        public void F5_EditLiftsRenameIsPartOfApplysOneStep()
        {
            var id = BuiltOnSlope();
            _host.Choose(EditLiftTool.ToolId);
            var tool = (EditLiftTool)_host.Active;
            GiveGround();
            Assert.That(tool.Open(id.Value), Is.True);
            var w = _host.Context.Window;
            w.BeginRename();
            w.Window.Q<TextField>("toolwin-name-field").value = "Lookout Quad";
            w.EndRename(true);
            Assert.That(tool.Changes, Is.EqualTo(1), "a change of the draft");
            PumpUntil(() => tool.Built != null && w.CanBreakGround, "the draft builds and can be applied");
            long revision = _design.Revision;
            _host.Tick(new ToolInput { Keys = ToolKeys.BreakGround });
            Assert.That(_design.Revision, Is.EqualTo(revision + 1), "one step");
            Assert.That(_design.Document.FindLift(id).Name, Is.EqualTo("Lookout Quad"));
            Assert.That(_design.Undo(), Is.True);
            Assert.That(_design.Document.FindLift(id).Name, Is.EqualTo("Chair 1"), "one Ctrl+Z undoes it");
        }

        // ---------- Remove lift (task 09) ----------

        LiftId AddLiftAt(WorkStage stage)
        {
            var r = _design.Submit(new AddLift(_design.Revision, "Chair 1", LiftCatalogIds.SesselliftFgq4,
                DesignFrame.ToDesign(new Vector2(0, -600)), DesignFrame.ToDesign(new Vector2(0, 300)), TowerMode.Automatic, TowerStance.Plumb, null, stage));
            Assert.That(r.Accepted, Is.True);
            return new LiftId(r.Created[0]);
        }

        [Test]
        public void RemoveLift_TheFirstClickSelectsTheSecondRemovesAndUndoPutsItBack()
        {
            var id = AddLiftAt(WorkStage.Built);
            Assert.That(ToolCatalog.IsLive(RemoveLiftTool.ToolId), Is.True);
            _host.Choose(RemoveLiftTool.ToolId);
            var tool = (RemoveLiftTool)_host.Active;
            ulong held = 0, under = id.Value;
            _host.Context.LiftUnderPointer = () => under;
            _host.Context.HoldLift = v => held = v;
            _host.Tick(At(300, 0, click: true));
            Assert.That(tool.Armed, Is.EqualTo(id.Value), "the first click selects it");
            Assert.That(held, Is.EqualTo(id.Value), "and keeps it lit");
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(1), "nothing removed yet");
            _host.Tick(default);
            _host.Tick(At(300, 0, click: true));
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(0), "the second click removes it");
            Assert.That(tool.Armed, Is.EqualTo(0UL));
            Assert.That(held, Is.EqualTo(0UL), "nothing stays lit");
            Assert.That(_host.Armed, Is.True, "the tool stays out for the next one");
            Assert.That(_design.Undo(), Is.True);
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(1), "one Ctrl+Z puts it back");
        }

        [Test]
        public void RemoveLift_EscKeepsItAndAPlanPicksByItsLine()
        {
            AddLiftAt(WorkStage.Plan);
            _host.Choose(RemoveLiftTool.ToolId);
            var tool = (RemoveLiftTool)_host.Active;
            _host.Tick(At(2, -100, click: true));   // 2 m off the plan's line
            Assert.That(tool.Armed, Is.Not.EqualTo(0UL), "a plan picks by its dashed line");
            Assert.That(_host.Escape(), Is.True);
            Assert.That(tool.Armed, Is.EqualTo(0UL), "Esc keeps it");
            Assert.That(_host.Armed, Is.True, "Esc with a lift selected only steps back");
            _host.Tick(At(2, -100, click: true));
            _host.Tick(default);
            _host.Tick(new ToolInput { Keys = ToolKeys.Next });   // Enter removes the selected lift
            Assert.That(_design.Document.Lifts.Count, Is.EqualTo(0));
            _host.Tick(At(200, -100, click: true));
            Assert.That(tool.Armed, Is.EqualTo(0UL), "nothing under the pointer: nothing selected");
        }

        [Test]
        public void TR05_ATrailsHeadSnapsToALiftTop()
        {
            var r = _design.Submit(new AddLift(_design.Revision, "Chair 1", LiftCatalogIds.SesselliftFgq4,
                DesignFrame.ToDesign(new Vector2(0, -600)), DesignFrame.ToDesign(new Vector2(0, 300)), TowerMode.Automatic, TowerStance.Plumb, null, WorkStage.Built));
            Assert.That(r.Accepted, Is.True);
            _host.Choose(NewTrailTool.ToolId);
            var ctx = _host.Context;
            var hit = ctx.Snap(DesignFrame.ToDesign(new Vector2(5, 295)), SnapTargets.All);
            Assert.That(hit.Kind, Is.EqualTo(SnapKind.LiftTop));
            Assert.That(DesignFrame.ToLocal(hit.At), Is.EqualTo(new Vector2(0, 300)));
        }
    }
}
