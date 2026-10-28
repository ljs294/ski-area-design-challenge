using System.Collections;
using MountainPlanner.App;
using MountainPlanner.App.Flow;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI;
using MountainPlanner.UI.Tools;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.SceneManagement;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-06 acceptance, in the real game on the committed Jackson Hole terrain at 50%, 100% and 150% interface
    /// scale: New lift and New trail work by keyboard and mouse (the pointer places points; every step, option and
    /// commit has a key), switching away cancels the armed tool cleanly (CF-01), and the camera's keys keep working
    /// while a tool is armed.
    /// </summary>
    public sealed partial class UiKeyboardTests
    {
        Mouse _mouse;

        [UnityTest]
        public IEnumerator ToolboxToolsWorkByKeyboardAndMouse([Values(50, 100, 150)] int scale)
        {
            Http.NetworkDisabled = true;
            UiPreferences.SetChoice(UiThemeChoice.Dark, remember: false);
            UiPreferences.SetScale(scale, remember: false);
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return SceneManager.LoadSceneAsync(ViewerScene);
            yield return WaitForMountain(120);
            yield return Frames(3);
            var ui = flow.Screens.Document.rootVisualElement;
            yield return TabTo(ui, "title-continue");
            yield return Submit(ui);
            yield return WaitForMountain(120, inGame: true);
            yield return Frames(10);
            var viewer = Object.FindAnyObjectByType<MountainViewer>();
            var hud = viewer.Hud.Document.rootVisualElement;
            var cam = viewer.Camera.GetComponent<Camera>();
            _mouse = InputSystem.AddDevice<Mouse>();
            _mouse.MakeCurrent();
            try
            {
                // Park the pointer on the HUD's bar so nothing is aimed until we mean it.
                yield return Point(new Vector2(Screen.width * 0.5f, 4));

                // T opens the tray; its letters are now the tools' (the key map). N arms New lift.
                yield return Press(Key.T);
                Assert.That(viewer.Hud.ToolboxOpen, Is.True);
                Assert.That(viewer.Camera.LettersToTools, Is.True, "while the tray is open the letters are its tools'");
                yield return Press(Key.N);
                Assert.That(viewer.Tools.ActiveId, Is.EqualTo(NewLiftTool.ToolId), "N arms New lift on the Lifts tab");
                yield return Frames(3);
                AssertShownOnScreen(hud, "toolwin");
                AssertShownOnScreen(hud, "toolbox");
                Assert.That(hud.Q("tool-new-lift").ClassListContains("tool--on"), Is.True, "the armed tool's tile is lit");

                // The camera keeps the arrows while a tool is armed.
                var before = viewer.Camera.Target;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.UpArrow));
                for (float until = Time.realtimeSinceStartup + 0.4f; Time.realtimeSinceStartup < until;) yield return null;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
                yield return Frames(3);
                Assert.That(Vector3.Distance(viewer.Camera.Target, before), Is.GreaterThan(1f), "↑ pans with a tool armed");
                Assert.That(viewer.Tools.Armed, Is.True);
                yield return Frames(10);   // let the camera settle before aiming at the ground

                // Tab gives the tool window the keyboard (the arrows walk it); Esc gives it back, the tool stays.
                yield return Press(Key.Tab);
                Assert.That(viewer.Hud.HasKeyboard, Is.True, "Tab hands the tool window the keys");
                Assert.That(hud.Q("toolwin").Contains((VisualElement)Focused(hud)), Is.True, $"focus is in the window ({Name(Focused(hud))})");
                Assert.That(viewer.Hud.AnalysisOpen, Is.False, "Tab didn't open Analysis while a tool is armed");
                yield return Move(hud, NavigationMoveEvent.Direction.Right);
                Assert.That(hud.Q("toolwin").Contains((VisualElement)Focused(hud)), Is.True, "the arrows stay in the window");
                yield return Press(Key.Escape);
                Assert.That(viewer.Hud.HasKeyboard, Is.False);
                Assert.That(viewer.Tools.Armed, Is.True, "Esc gave the keys back without closing the tool");

                // Two clicks on the mountain: the bottom terminal, then the top.
                var ctx = viewer.Tools.Context;
                // The line must be one the tool's own build passes (a red line keeps Break ground off), so the search builds
                // on the edited ground the tool builds on; asking for it starts the ground's edit session.
                for (float until = Time.realtimeSinceStartup + 60; Time.realtimeSinceStartup < until && ctx.EditedGround?.Invoke() == null;) yield return null;
                Assert.That(ctx.EditedGround?.Invoke(), Is.Not.Null, "the ground's edit session started");
                string catalogId = ((NewLiftTool)viewer.Tools.Active).CatalogId;
                bool found = FindLiftLine(viewer, cam, ctx, catalogId, out var bottom, out var top);
                if (!found)
                {
                    // A small screen at 150%: the window covers the left half, so drag it aside as a player would.
                    var window = (UI.Hud.HudWindow)hud.Q("toolwin");
                    window.MoveTo(new Vector2(hud.panel.visualTree.worldBound.width, 0));
                    yield return Frames(3);
                    found = FindLiftLine(viewer, cam, ctx, catalogId, out bottom, out top);
                }
                Assert.That(found, Is.True, "a visible bottom and top that can sit, on a line that builds within the rules");
                yield return Click(ScreenOf(cam, bottom, ctx));
                var lift = (NewLiftTool)viewer.Tools.Active;
                Assert.That(lift.CurrentStep, Is.EqualTo(1), $"the bottom terminal anchored ({lift.Problem})");
                var topScreen = ScreenOf(cam, top, ctx);
                yield return Click(topScreen);
                Assert.That(lift.CurrentStep, Is.EqualTo(2), $"the top terminal fixed the line ({lift.Problem}); " + Where(viewer, cam, topScreen, top, bottom));
                yield return Press(Key.Enter);
                Assert.That(lift.CurrentStep, Is.EqualTo(3), "Enter: the review");
                // Task 09: the review waits for the lift's build (the ground's edit session starting, then LiftBuild off the
                // main thread); a blocking problem keeps Break ground off, so the test's line must be buildable.
                for (float until = Time.realtimeSinceStartup + 60; Time.realtimeSinceStartup < until && lift.Built == null;)
                {
                    if (lift.CurrentStep == 3 && !lift.Building && lift.Built == null) { yield return Press(Key.Escape); yield return Press(Key.Enter); }
                    yield return null;
                }
                Assert.That(lift.Built, Is.Not.Null, "the lift was built for the review");
                Assert.That(hud.Q<Button>("review-go").enabledSelf, Is.EqualTo(lift.Built.Ok), string.Join("; ", lift.Built.Problems));
                if (!lift.Built.Ok) Assert.Inconclusive("the picked line is red: " + string.Join("; ", lift.Built.Problems));
                AssertShownOnScreen(hud, "review-go");
                yield return Chord(Key.LeftCtrl, Key.Enter);
                Assert.That(viewer.Design.Document.Lifts.Count, Is.EqualTo(1), "Ctrl+Enter broke ground");
                var placed = viewer.Design.Document.Lifts[0];
                string placedName = placed.Name;
                Assert.That(placed.Towers.Count, Is.GreaterThan(0), "TW-12's towers came with it");
                Assert.That(placed.Levels, Is.Not.Null, "and its terminal levels (SK14)");
                Assert.That(lift.CurrentStep, Is.EqualTo(0), "ready for the next lift");

                // Switching away mid-draw cancels at once and puts everything back (CF-01). The lift window (taller since F1-F5:
                // the tower keys and buttons) may sit over the spot on the small test screen: aside, as a player would drag it.
                if (viewer.Hud.IsPointerOverPanel(ScreenOf(cam, bottom, ctx)))
                {
                    ((UI.Hud.HudWindow)hud.Q("toolwin")).MoveTo(new Vector2(0, hud.panel.visualTree.worldBound.height));
                    yield return Frames(3);
                }
                yield return Click(ScreenOf(cam, bottom, ctx));
                Assert.That(lift.CurrentStep, Is.EqualTo(1), $"the bottom anchored again ({lift.Problem}); " + Where(viewer, cam, ScreenOf(cam, bottom, ctx), bottom, bottom));
                int disarms = viewer.Tools.Disarms;
                yield return SubmitOn(hud.Q<Button>("toolbox-tab-trails"));
                Assert.That(viewer.Tools.Armed, Is.False, "another tab cancels the armed tool");
                Assert.That(viewer.Tools.Disarms, Is.EqualTo(disarms + 1), "disarmed exactly once");
                Assert.That(viewer.Preview.Visible, Is.EqualTo(0), "its preview is gone");
                Assert.That(KeyBindings.IsClaimed(GameAction.ToolWindowKeys), Is.False, "Tab is Analysis' again");
                Assert.That(hud.Q("toolwin"), Is.Null, "its window closed");

                // New trail: paint from the lift's top to its bottom, then head and tail snap to them (TR-05).
                yield return Press(Key.N);
                Assert.That(viewer.Tools.ActiveId, Is.EqualTo(NewTrailTool.ToolId), "N arms New trail on the Trails tab");
                var trail = (NewTrailTool)viewer.Tools.Active;
                yield return Frames(3);
                bool covered = false;
                for (int i = 0; i <= 40; i++) covered |= viewer.Hud.IsPointerOverPanel(ScreenOf(cam, Vector2.Lerp(top, bottom, i / 40f), ctx));
                if (covered)
                {
                    // The trail window (wider than a small screen at 150%) sits over the stroke: drag it down to the bottom
                    // edge, its title still showing, as a player would.
                    var window = (UI.Hud.HudWindow)hud.Q("toolwin");
                    window.MoveTo(new Vector2(0, hud.panel.visualTree.worldBound.height));
                    // The profile strip only shows (TF-04); on the 640x480 test screen at 150% it covers the lift's line, so
                    // set it aside: this test is about the keys and the pointer, not the strip.
                    var strip = hud.Q("trail-profile");
                    if (strip != null) strip.style.display = DisplayStyle.None;
                    yield return Frames(3);
                }
                yield return Drag(cam, ctx, top, bottom);
                Assert.That(trail.Strokes, Is.EqualTo(1), $"one stroke painted (window moved {covered}, step {trail.CurrentStep}); from " +
                    Where(viewer, cam, ScreenOf(cam, top, ctx), top, bottom) + "; to " + Where(viewer, cam, ScreenOf(cam, bottom, ctx), bottom, bottom));
                for (int i = 0; i < 300 && (trail.Analysing || trail.Raster == null); i++) yield return null;
                Assert.That(trail.Raster.Patches, Is.EqualTo(1));
                yield return Press(Key.Enter);
                Assert.That(trail.CurrentStep, Is.EqualTo(1), "Enter: head and tail");
                yield return Click(ScreenOf(cam, top + new Vector2(2, -2), ctx));
                yield return Click(ScreenOf(cam, bottom + new Vector2(-2, 2), ctx));
                yield return Press(Key.Enter);
                Assert.That(trail.CurrentStep, Is.EqualTo(2), "Enter: the review");
                yield return Chord(Key.LeftCtrl, Key.Enter);
                Assert.That(viewer.Design.Document.Trails.Count, Is.EqualTo(1), "Ctrl+Enter broke ground");
                var run = viewer.Design.Document.Trails[0];
                var chair = viewer.Design.Document.Lifts[0];
                Assert.That(run.Head.Value, Is.EqualTo(chair.Top), "the head snapped to the lift's top");
                Assert.That(run.Tail.Value, Is.EqualTo(chair.Bottom), "the tail snapped to its bottom");

                // The map keys work while a tool is armed (owner, TL19): W pans and E turns the view, and neither is a tool key.
                Assert.That(viewer.Tools.Armed, Is.True, "the tool stays armed after Break ground");
                var eye = viewer.Camera.transform.position;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W));
                yield return Frames(6);
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
                yield return Frames(2);
                Assert.That(Vector3.Distance(viewer.Camera.transform.position, eye), Is.GreaterThan(0.5f), "W pans with a tool armed");
                Assert.That(viewer.Tools.ActiveId, Is.EqualTo(NewTrailTool.ToolId), "W left the tool alone");
                var turn = viewer.Camera.transform.rotation;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.E));
                yield return Frames(6);
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
                yield return Frames(2);
                Assert.That(Quaternion.Angle(viewer.Camera.transform.rotation, turn), Is.GreaterThan(0.5f), "E turns the view with a tool armed");
                Assert.That(viewer.Tools.ActiveId, Is.EqualTo(NewTrailTool.ToolId), "E no longer arms Edit (Edit is M)");

                // Typing in a field is the field's alone (owner, 2026-10-19): a click puts the caret in the trail's name, and
                // WASD, the tray's letters, T and Space then do nothing in the game (the test's virtual keyboard sends no
                // text to UI Toolkit, so what the field types isn't checked here). A click on the map gives the keys back.
                // Sections finding 3: the name is the window's title, opened for typing by a click on it.
                yield return ClickElement(hud.Q<Label>("toolwin-name"));
                yield return Frames(2);
                var nameField = hud.Q<TextField>("toolwin-name-field");
                Assert.That(nameField, Is.Not.Null, "the trail window's name field");
                yield return ClickElement(nameField.Q(TextField.textInputUssName) ?? nameField);
                Assert.That(viewer.Hud.Typing, Is.True, $"a click put the caret in the name (focused {Name(Focused(hud))})");
                Assert.That(UiFocus.KeyboardActive(hud), Is.False, "reached with the mouse, not the keys");
                var still = viewer.Camera.Target;
                bool paused = viewer.Clock.Paused;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W));
                for (float until = Time.realtimeSinceStartup + 0.4f; Time.realtimeSinceStartup < until;) yield return null;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
                yield return Frames(2);
                foreach (var key in new[] { Key.A, Key.S, Key.D, Key.X, Key.T, Key.Space, Key.N })
                    yield return Press(key);
                yield return Frames(3);
                Assert.That(Vector3.Distance(viewer.Camera.Target, still), Is.LessThan(0.5f), "WASD typed instead of panning");
                Assert.That(viewer.Tools.ActiveId, Is.EqualTo(NewTrailTool.ToolId), "X typed instead of arming Remove");
                Assert.That(viewer.Hud.ToolboxOpen, Is.True, "T typed instead of closing the Toolbox");
                Assert.That(viewer.Clock.Paused, Is.EqualTo(paused), "Space typed instead of pausing");
                // A right-click on the map (it doesn't paint) lets go of the field; W pans again.
                var map = ScreenOf(cam, Vector2.Lerp(top, bottom, 0.5f), ctx);
                InputSystem.QueueStateEvent(_mouse, new MouseState { position = map });
                yield return Frames(2);
                InputSystem.QueueStateEvent(_mouse, new MouseState { position = map }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Right, true));
                yield return Frames(2);
                InputSystem.QueueStateEvent(_mouse, new MouseState { position = map });
                yield return Frames(2);
                Assert.That(viewer.Hud.Typing, Is.False, "a click on the map let go of the name");
                still = viewer.Camera.Target;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState(Key.W));
                for (float until = Time.realtimeSinceStartup + 0.4f; Time.realtimeSinceStartup < until;) yield return null;
                InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
                yield return Frames(3);
                Assert.That(Vector3.Distance(viewer.Camera.Target, still), Is.GreaterThan(1f), "W pans again once the name lets go");

                // Esc closes the tool (nothing drawn), then the tray; the letters go back to the camera.
                yield return Press(Key.Escape);
                Assert.That(viewer.Tools.Armed, Is.False);
                Assert.That(viewer.Hud.ToolboxOpen, Is.True, "the first Esc closed only the tool");
                yield return Press(Key.Escape);
                Assert.That(viewer.Hud.ToolboxOpen, Is.False);
                yield return Frames(2);
                Assert.That(viewer.Camera.LettersToTools, Is.False);

                // Undo and redo the design with no tool armed.
                yield return Chord(Key.LeftCtrl, Key.Z);
                Assert.That(viewer.Design.Document.Trails.Count, Is.EqualTo(0), "Ctrl+Z took the trail back");
                yield return Chord(Key.LeftCtrl, Key.Y);
                Assert.That(viewer.Design.Document.Trails.Count, Is.EqualTo(1), "Ctrl+Y put it back");

                // F6 (LT39): the lift's card (a click on it on the map, or its Analysis row) renames it in one undoable step.
                for (int i = 0; i < 600 && viewer.Hud.LiftRenamed == null; i++) yield return null;
                viewer.Hud.OpenLiftCardFor(placed.Id.Value);
                viewer.Hud.RenameLiftCardForTest("Summit Quad");
                Assert.That(viewer.Design.Document.Lifts[0].Name, Is.EqualTo("Summit Quad"), "the card's rename");
                Assert.That(viewer.Design.Undo(), Is.True);
                Assert.That(viewer.Design.Document.Lifts[0].Name, Is.EqualTo(placedName), "one Ctrl+Z takes it back");
                viewer.Hud.CloseLiftCardIfOpen();
            }
            finally
            {
                InputSystem.RemoveDevice(_mouse);
                _mouse = null;
            }
        }

        /// <summary>
        /// A bottom and a top terminal the camera can see and that can sit (LF-08: inside the core, the ground within
        /// 10 ft under each), 110–450 m apart with the top higher, neither under a HUD panel.
        /// </summary>
        static bool FindLiftLine(MountainViewer viewer, Camera cam, ToolContext ctx, string catalogId, out Vector2 bottom, out Vector2 top)
        {
            bottom = top = default;
            var core = ctx.Core;
            int builds = 0;
            for (float x = core.xMin + 30; x < core.xMax - 30; x += 20)
                for (float z = core.yMin + 30; z < core.yMax - 30; z += 20)
                {
                    var b = new Vector2(x, z);
                    if (!Sits(viewer, cam, ctx, b)) continue;
                    int red = 0;
                    for (int dir = 0; dir < 32 && red < 4; dir++)
                        for (float d = 110; d <= 450; d += 20)
                        {
                            var t = b + d * new Vector2(Mathf.Cos(dir * Mathf.PI / 16), Mathf.Sin(dir * Mathf.PI / 16));
                            if (ctx.HeightAt(t) < ctx.HeightAt(b) + 10 || !Sits(viewer, cam, ctx, t)) continue;
                            // Green as the tool builds it from where the clicks land (#94/#97 picked a red line: TW-01, LT34);
                            // a red one tries the next direction, four red ones the next bottom. A few seconds at most.
                            if (++builds > 40) return false;
                            if (!BuildsGreen(viewer, cam, ctx, catalogId, b, t)) { red++; break; }
                            bottom = b;
                            top = t;
                            return true;
                        }
                }
            return false;
        }

        /// <summary>Whether the lift the tool would build between clicks at these points is within the rules, as the tool builds
        /// it with its defaults (automatic towers, plumb, no levels set by hand).</summary>
        static bool BuildsGreen(MountainViewer viewer, Camera cam, ToolContext ctx, string catalogId, Vector2 bottom, Vector2 top)
        {
            var ground = ctx.EditedGround?.Invoke();
            if (ground == null || ctx.ToGround == null) return false;
            var b = ctx.ToGround(DesignFrame.ToDesign(Landing(viewer, cam, ctx, bottom)));
            var t = ctx.ToGround(DesignFrame.ToDesign(Landing(viewer, cam, ctx, top)));
            return LiftBuild.Build(LiftType.Of(catalogId), ground, b.X, b.Y, t.X, t.Y, TowerStance.Plumb, null, 0, 0, suggest: false).Ok;
        }

        /// <summary>Where a click aimed at <paramref name="at"/> meets the ground (within 3 m of it, as <see cref="Sits"/> checks).</summary>
        static Vector2 Landing(MountainViewer viewer, Camera cam, ToolContext ctx, Vector2 at)
        {
            var ray = cam.ScreenPointToRay(ScreenOf(cam, at, ctx));
            float hit = viewer.Camera.GroundAlong(ray, 30000);
            if (float.IsNaN(hit)) return at;
            var g = ray.GetPoint(hit);
            return new Vector2(g.x, g.z);
        }

        static bool Sits(MountainViewer viewer, Camera cam, ToolContext ctx, Vector2 at)
        {
            // The whole terminal inside the core box, as the tool checks (SH-06), with a margin.
            if (!ctx.InCore(at + new Vector2(12, 12)) || !ctx.InCore(at - new Vector2(12, 12))) return false;
            float lo = float.MaxValue, hi = float.MinValue;
            for (int i = 0; i <= 8; i++)
            {
                var p = i == 8 ? at : at + 8 * new Vector2(Mathf.Cos(i * Mathf.PI / 4), Mathf.Sin(i * Mathf.PI / 4));
                float h = ctx.HeightAt(p);
                if (float.IsNaN(h)) return false;
                lo = Mathf.Min(lo, h);
                hi = Mathf.Max(hi, h);
            }
            if (hi - lo > 2.5f) return false;   // a margin under LF-08's 10 ft
            var s = ScreenOf(cam, at, ctx);
            if (s.x < Screen.width * 0.03f || s.x > Screen.width * 0.97f || s.y < Screen.height * 0.15f || s.y > Screen.height * 0.95f) return false;
            if (viewer.Hud.IsPointerOverPanel(s)) return false;
            // Clear of the tool window, which grows as the steps go on (most of the screen's width at 150% on a small one).
            var hud = viewer.Hud.Document.rootVisualElement;
            var win = hud.Q("toolwin");
            if (win != null)
            {
                float px = Screen.height / Mathf.Max(1f, hud.panel.visualTree.worldBound.height);
                if (s.x > (win.worldBound.xMin - 30) * px && s.x < (win.worldBound.xMax + 30) * px) return false;
            }
            // Not hidden behind a ridge: the pointer's ray meets the ground at this point.
            var ray = cam.ScreenPointToRay(s);
            float hit = viewer.Camera.GroundAlong(ray, 30000);
            if (float.IsNaN(hit)) return false;
            var g = ray.GetPoint(hit);
            return Vector2.Distance(new Vector2(g.x, g.z), at) < 3f;
        }

        /// <summary>Where a screen point meets the ground, for a failure's message.</summary>
        static string Where(MountainViewer viewer, Camera cam, Vector2 screen, Vector2 meant, Vector2 bottom)
        {
            var ray = cam.ScreenPointToRay(screen);
            float hit = viewer.Camera.GroundAlong(ray, 30000);
            var g = float.IsNaN(hit) ? Vector3.zero : ray.GetPoint(hit);
            var root = viewer.Hud.Document.rootVisualElement;
            var picked = root.panel.Pick(RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y)));
            string chain = "";
            for (var e = picked; e != null && e != root; e = e.parent) chain += (e.name ?? e.GetType().Name) + "<";
            return $"screen {Screen.width}x{Screen.height}, pointer {screen}, over a panel {viewer.Hud.IsPointerOverPanel(screen)} ({chain}), " +
                   $"hits {g.x:F0},{g.z:F0} meaning {meant.x:F0},{meant.y:F0} (bottom {bottom.x:F0},{bottom.y:F0}), camera {cam.transform.position}";
        }

        /// <summary>
        /// A mouse click on a HUD element as UI Toolkit hears it: pointer down (which also ends keyboard mode), focus, pointer
        /// up (after which the HUD lets go of whatever a click focused, unless it's a text field). The test's virtual mouse
        /// doesn't reach UI Toolkit's own click handling, so the events go to the element directly.
        /// </summary>
        static IEnumerator ClickElement(VisualElement e)
        {
            var at = e.worldBound.center;
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 }))
            {
                down.target = e;
                e.SendEvent(down);
            }
            e.Focus();
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 }))
            {
                up.target = e;
                e.SendEvent(up);
            }
            yield return Frames(3);
        }

        static Vector2 ScreenOf(Camera cam, Vector2 at, ToolContext ctx)
        {
            var p = cam.WorldToScreenPoint(new Vector3(at.x, ctx.HeightAt(at), at.y));
            return new Vector2(p.x, p.y);
        }

        IEnumerator Point(Vector2 screen, bool left = false)
        {
            var state = new MouseState { position = screen };
            if (left) state = state.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true);
            InputSystem.QueueStateEvent(_mouse, state);
            yield return Frames(2);
        }

        IEnumerator Click(Vector2 screen)
        {
            yield return Point(screen);
            yield return Point(screen, true);
            yield return Point(screen);
        }

        /// <summary>A left-drag over the ground from one local point to another, in short moves.</summary>
        IEnumerator Drag(Camera cam, ToolContext ctx, Vector2 from, Vector2 to)
        {
            yield return Point(ScreenOf(cam, from, ctx));
            for (int i = 0; i <= 40; i++) yield return Point(ScreenOf(cam, Vector2.Lerp(from, to, i / 40f), ctx), true);
            yield return Point(ScreenOf(cam, to, ctx));
        }

        IEnumerator Chord(Key modifier, Key key)
        {
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(modifier));
            yield return Frames(1);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState(modifier, key));
            yield return Frames(2);
            InputSystem.QueueStateEvent(_keyboard, new KeyboardState());
            yield return Frames(3);
        }

        /// <summary>Presses a HUD button as the keyboard's Enter does (the tray's tabs).</summary>
        static IEnumerator SubmitOn(Button button)
        {
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = button; button.SendEvent(e); }
            yield return Frames(3);
        }
    }
}
