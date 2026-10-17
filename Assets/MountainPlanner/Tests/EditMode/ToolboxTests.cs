using System;
using System.Collections;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
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
        public void SH07_ToolKeysAreRebindableAndNeverSwapWithTheCamera()
        {
            Assert.That(KeyBindings.Caption(GameAction.ToolNew), Is.EqualTo("N"));
            Assert.That(KeyBindings.Caption(GameAction.ToolNext), Is.EqualTo("Enter"));
            Assert.That(KeyBindings.Caption(GameAction.BreakGround), Is.EqualTo("Ctrl Enter"));
            Assert.That(KeyBindings.Caption(GameAction.TowerUp), Is.EqualTo("Page Up"));
            Assert.That(KeyBindings.Caption(GameAction.Erase), Is.EqualTo("Shift"));
            // The tray's letters live only while it's open, when the camera has no letters: no swap with WASD.
            Assert.That(KeyBindings.Bind(GameAction.ToolNew, 0, new KeyChord(Key.W)), Is.Null);
            Assert.That(KeyBindings.Get(GameAction.MoveForward, 0), Is.EqualTo(new KeyChord(Key.W)));
            // Two tray keys do swap.
            Assert.That(KeyBindings.Bind(GameAction.ToolEdit, 0, new KeyChord(Key.W)), Is.EqualTo(GameAction.ToolNew));
            Assert.That(KeyBindings.Get(GameAction.ToolNew, 0), Is.EqualTo(new KeyChord(Key.E)));
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
            // The work runs on a worker thread; the host hands the result over on its next tick (CF-03).
            for (int i = 0; i < 600 && trail.Analysing; i++)
            {
                yield return null;
                _host.Tick(default);
            }
            Assert.That(trail.Analysing, Is.False, "the area was worked out off the main thread");
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
            Assert.That(built.Parts[0].Strokes.Count, Is.EqualTo(1));
            Assert.That(trail.CurrentStep, Is.EqualTo(0), "ready for the next trail");
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
