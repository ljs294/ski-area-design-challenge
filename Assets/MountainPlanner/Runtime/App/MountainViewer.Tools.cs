using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Persistence.Saves;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// The Toolbox's tools in the mountain view (task P3-06): the session's design, the tool host, the pending preview
    /// and the placeholder drawing of committed work; the pointer's ground point, the left button and the tool keys
    /// handed to the armed tool each frame; Esc's step for the tool; Ctrl+Z / Ctrl+Y for the design when no tool is armed.
    /// </summary>
    public sealed partial class MountainViewer
    {
        DesignHistory _design;
        ToolHost _tools;
        PendingPreview _preview;
        TrailPaintMap _trailPaint;
        DesignOverlay _designOverlay;
        bool _pressOnMap;

        /// <summary>
        /// The design the tools work on: the open save's (task P3-04, <see cref="OpenDesign.Current"/>), or, when the
        /// game was started straight into an area without the app flow (-package, captures), a session-only one.
        /// </summary>
        public DesignHistory Design => OpenDesign.Current?.History ?? _design;

        /// <summary>The Toolbox's tool host (tests and captures).</summary>
        public ToolHost Tools => _tools;

        /// <summary>The pending preview (tests).</summary>
        internal PendingPreview Preview => _preview;

        // The overlay draws whichever design is open; a save opened or closed under it swaps it (and closes the tool).
        void FollowDesign()
        {
            var design = Design;
            if (design == _drawn || _resort == null) return;
            _tools?.Close();
            _designOverlay?.Dispose();
            _drawn = design;
            _designOverlay = new DesignOverlay(_resort.Root.transform, _resort.Surface, HighlightMaterial, design);
        }

        DesignHistory _drawn;

        void StartTools()
        {
            if (Hud == null || _resort == null || TitleMode || _tools != null) return;
            _design = new DesignHistory(DesignDocument.Empty(DesignSeed(_resort.Manifest.PackageId)));
            _preview = new PendingPreview(_resort.Root.transform, "Tool preview", _resort.Surface, HighlightMaterial);
            _trailPaint = new TrailPaintMap();
            var cli = System.Environment.GetCommandLineArgs();
            int style = System.Array.IndexOf(cli, "-paintstyle");   // review captures: a, b or c
            if (style >= 0 && style + 1 < cli.Length && cli[style + 1].Length > 0)
                _trailPaint.Style = Mathf.Max(0, "abc".IndexOf(char.ToLowerInvariant(cli[style + 1][0])));
            _preview.Cleared += _trailPaint.Clear;   // a tool's close or commit takes its paint off the ground too
            FollowDesign();
            _tools = new ToolHost(_ => new ToolContext(Design, _preview, _resort.Surface, _resort.Core)
            {
                Earthwork = Ground, Lines = new TrailLineSnap(() => Ground?.Earthwork), Paint = _trailPaint,
                // Task 09: lifts build on the edited ground; the edit session starts the first time a tool asks for it.
                EditedGround = () =>
                {
                    if (_edits == null && !_editsStarting && _resort != null)
                    {
                        _editsStarting = true;
                        StartCoroutine(StartEditsWhenReady(Array.Empty<string>(), -1, -1));
                    }
                    return _edits?.Pipeline.Heights;
                },
                ToGround = p =>
                {
                    var a = _resort.Frame.ToAlbers((float)p.XMetres, (float)p.YMetres);
                    return (a.X, a.Y);
                },
                BottomGhost = _ghosts[0] ??= Lifts != null ? new Presentation.Lifts.TerminalGhost(_resort.Root.transform, Lifts) : null,
                LiftUnderPointer = () => _hoveredId,
                HoldLift = id => _heldLiftId = id,
                TopGhost = _ghosts[1] ??= Lifts != null ? new Presentation.Lifts.TerminalGhost(_resort.Root.transform, Lifts) : null,
            }, Hud.OpenWindow);
            _tools.ActiveChanged += Hud.SetActiveTool;
            _tools.Notice += text => Hud.Toast(text);
            Hud.ToolChosen += id => _tools.Choose(id);
            Hud.ToolboxLeft += CancelTool;
        }

        // A tray closing or changing tab cancels the armed tool at once, saying so if it had work (CF-01; P3-06 Q8).
        void CancelTool()
        {
            if (_tools == null || !_tools.Armed) return;
            if (_tools.Active.HasWork) Hud.Toast(_tools.Active.Name + " discarded");
            _tools.Close();
        }

        /// <summary>A design's seed (SH-04) from the package's id: FNV-1a, the same on every machine.</summary>
        static ulong DesignSeed(string packageId)
        {
            ulong h = 14695981039346656037UL;
            foreach (char c in packageId ?? "")
            {
                h ^= c;
                h *= 1099511628211UL;
            }
            return h == 0 ? 1 : h;
        }

        // The terminals being placed (task 09, LT19): made once per area, shared by every New lift.
        readonly Presentation.Lifts.TerminalGhost[] _ghosts = new Presentation.Lifts.TerminalGhost[2];

        void StopTools()
        {
            for (int i = 0; i < _ghosts.Length; i++)
            {
                _ghosts[i]?.Dispose();
                _ghosts[i] = null;
            }
            _tools?.Close();
            _preview?.Dispose();
            _trailPaint?.Dispose();
            _trailPaint = null;
            _designOverlay?.Dispose();
            DisposeTrailLayer();
            _tools = null;
            _preview = null;
            _designOverlay = null;
            _drawn = null;
            KeyBindings.ClearClaims();
        }

        /// <summary>Esc's step for the armed tool (CF-07): false when no tool is armed.</summary>
        bool EscapeTool() => _tools != null && _tools.Escape();

        /// <summary>
        /// The tray's letters and the design's undo (from HandleKeys, when no UI has the keyboard). Returns true when a key
        /// was used here.
        /// </summary>
        void HandleToolKeys(Keyboard keys)
        {
            if (_tools == null || Hud == null) return;
            if (Hud.ToolboxOpen)
            {
                foreach (var action in TrayLetters)
                {
                    if (!KeyBindings.Pressed(keys, action)) continue;
                    string id = Hud.ToolFor(action);
                    if (id == null) continue;
                    if (ToolCatalog.IsLive(id)) _tools.Choose(id);
                    else Hud.Toast("That tool comes in Phase 3");
                }
                if (_tools.Armed && KeyBindings.Pressed(keys, GameAction.ToolWindowKeys) && _tools.Context?.Window != null)
                    Hud.TakeToolKeyboard(_tools.Context.Window.FirstControl);
            }
            if (_tools.Armed) return;
            // Undo and redo of committed work (P3-06 Q5); heights and trees follow through the terrain session (P3-05, TE2).
            if (KeyBindings.Pressed(keys, GameAction.Undo))
            {
                var design = Design;
                string label = design.UndoLabel;
                Hud.Toast(design.Undo() ? "Undid " + Lower(label) : "Nothing to undo");
            }
            else if (KeyBindings.Pressed(keys, GameAction.Redo))
            {
                var design = Design;
                string label = design.RedoLabel;
                Hud.Toast(design.Redo() ? "Redid " + Lower(label) : "Nothing to redo");
            }
        }

        static readonly GameAction[] TrayLetters = { GameAction.ToolNew, GameAction.ToolEdit, GameAction.ToolRemove };

        static string Lower(string label) => string.IsNullOrEmpty(label) ? "" : char.ToLowerInvariant(label[0]) + label.Substring(1);

        /// <summary>Once a frame: the armed tool gets the pointer's ground point, the left button and its keys.</summary>
        void TickTools()
        {
            if (_tools == null) return;
            FollowDesign();
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            // A tool lives in the open tray (P3-06 Q4: T or Esc closes it); photo mode and the title close it too.
            if (_tools.Armed && (_photo || TitleMode || Hud == null || !Hud.ToolboxOpen)) _tools.Close();
            if (_tools.Armed && cam != null && !_toolScripted) _tools.Tick(ReadToolInput(cam));
            if (cam != null)
            {
                _preview?.Follow(cam);
                _trailPaint?.Flush();
                _designOverlay?.Follow(cam);
            }
        }

        // ---------- captures (AppFlow.UiCapture): a tool driven to a step without a mouse ----------

        /// <summary>
        /// For captures: the armed tool gets a click (or a drag point) at a fraction of the screen, as if the pointer were
        /// there. True when the pointer met the ground.
        /// </summary>
        internal bool PointForCapture(Vector2 fraction, bool click, bool drag = false, bool release = false)
        {
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (_tools == null || !_tools.Armed || cam == null) return false;
            var saved = _pointer;
            _pointer = fraction;
            var input = ReadToolInput(cam);
            _pointer = saved;
            input.Click = click;
            input.Drag = drag;
            input.Release = release;
            input.Keys = ToolKeys.None;
            _tools.Tick(input);
            return input.HasGround;
        }

        /// <summary>For captures: tool keys for one tick; waits (blocking) for any running analysis first.</summary>
        internal void KeysForCapture(ToolKeys keys)
        {
            if (_tools == null || !_tools.Armed) return;
            for (int i = 0; i < 400 && _tools.Context.Running > 0; i++)
            {
                System.Threading.Thread.Sleep(5);
                _tools.Tick(default);
            }
            _tools.Tick(new ToolInput { Keys = keys });
        }

        /// <summary>
        /// For captures: New lift to its review. Tries pointer spots over the view until a bottom, then a top, can sit.
        /// </summary>
        internal bool LiftReviewForCapture()
        {
            if (_tools == null || !_tools.Choose(NewLiftTool.ToolId) || !(_tools.Active is NewLiftTool lift)) return false;
            for (int step = 0; step < 2; step++)
                for (float y = 0.62f; y >= 0.2f && lift.CurrentStep == step; y -= 0.02f)
                    for (float x = step == 0 ? 0.62f : 0.5f; x <= 0.92f && lift.CurrentStep == step; x += 0.02f)
                        PointForCapture(new Vector2(x, step == 0 ? y : 1.2f - y), click: true);
            if (lift.CurrentStep != 2) return false;
            KeysForCapture(ToolKeys.Next);
            return lift.CurrentStep == 3;
        }

        /// <summary>
        /// For captures (task 09, LT19–LT22): New lift with the bottom terminal's ghost turned 30° and raised 5 ft over a spot
        /// that can take it; with <paramref name="top"/>, the bottom placed and the top's ghost aimed up the hill, 3 ft up.
        /// </summary>
        internal bool LiftGhostForCapture(bool top)
        {
            _groundForCapture = null;
            if (_tools == null || !_tools.Choose(NewLiftTool.ToolId) || !(_tools.Active is NewLiftTool lift)) return false;
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            if (!CoreSpot(new Vector2(0.55f, 0.4f), out var at)) return false;
            var saved = _pointer;
            _pointer = at;
            var g = ReadToolInput(cam).Ground;
            _pointer = saved;
            for (int i = 0; i < 6; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.TurnLeft });
            for (int i = 0; i < 5; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.LevelUp });
            if (!top)
            {
                _groundForCapture = g;
                Camera.Frame(g, 48);
                Camera.SetAngles(Camera.Yaw + 35, 30);
                return true;
            }
            _tools.Tick(GroundInput(g, click: true));
            if (lift.CurrentStep != 1) return false;
            for (int i = 0; i < 3; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.LevelUp });
            // The top 260 m up the fall line from the bottom, framed from the side.
            float hx = _resort.Surface.HeightAt(g.x + 10, g.z) - _resort.Surface.HeightAt(g.x - 10, g.z);
            float hz = _resort.Surface.HeightAt(g.x, g.z + 10) - _resort.Surface.HeightAt(g.x, g.z - 10);
            var up = new Vector3(hx, 0, hz).normalized;
            var t = g + up * 260;
            t.y = _resort.Surface.HeightAt(t.x, t.z);
            _groundForCapture = t;
            Camera.Frame(t, 75);
            Camera.SetAngles(Mathf.Atan2(up.x, up.z) * Mathf.Rad2Deg + 30, 48);
            return true;
        }

        /// <summary>For captures (task 09): New lift's menu on a Monta gondola, 8 person, with the Cabin dropdown open.</summary>
        internal bool LiftMenuForCapture()
        {
            if (_tools == null || !_tools.Choose(NewLiftTool.ToolId) || !(_tools.Active is NewLiftTool lift)) return false;
            lift.Choose(LiftCatalogIds.Of("gondola-8", "monta"));
            lift.CarrierMenu.Show();
            return true;
        }

        /// <summary>
        /// For captures (task 09, LF-04): New lift by hand at its Towers step, framed from the side; then
        /// <see cref="LiftByHandPlaceForCapture"/> places towers once the ground is ready.
        /// </summary>
        internal bool LiftByHandBeginForCapture()
        {
            if (!LiftGhostForCapture(true) || !(_tools.Active is NewLiftTool lift) || _groundForCapture == null) return false;
            var top = _groundForCapture.Value;
            lift.ChooseTowers(TowerMode.ByHand);
            _tools.Tick(GroundInput(top, click: true));
            if (lift.CurrentStep != 2) return false;
            _byHandTop = top;
            return true;
        }

        Vector3 _byHandTop;

        /// <summary>For captures: once the tower in hand is ready, <paramref name="towers"/> placed (each slid up to where it's
        /// green), and the tower in hand left 60% up the line. False until the ground is ready.</summary>
        internal bool LiftByHandPlaceForCapture(int towers)
        {
            if (!(_tools?.Active is NewLiftTool lift) || lift.HandStatus == null) return false;
            for (int n = lift.Placed.Count; n < towers; n++)
            {
                // From the first slot up (as a player slides it), to the first place it's green.
                for (int i = 0; i < 400; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.SlideBack, Shift = true });
                for (int i = 0; i < 400 && !lift.HandStatus.Ok; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.SlideOn });
                if (!lift.HandStatus.Ok) return true;
                _tools.Tick(new ToolInput { Keys = ToolKeys.Next });
            }
            // The camera beside the line.
            var b = DesignFrame.ToLocal(lift.Bottom);
            var start = new Vector3(b.x, 0, b.y);
            var dir = (_byHandTop - start); dir.y = 0; dir.Normalize();
            // The next tower in hand slid to its first green place, the pointer on it.
            for (int i = 0; i < 400; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.SlideBack, Shift = true });
            for (int i = 0; i < 400 && !lift.HandStatus.Ok; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.SlideOn });
            for (int i = 0; i < 6; i++) _tools.Tick(new ToolInput { Keys = ToolKeys.SlideOn });   // a little past it, where it may still be green
            var at = start + dir * (float)lift.HandAlong;
            at.y = _resort.Surface.HeightAt(at.x, at.z);
            _groundForCapture = at;
            var mid = Vector3.Lerp(start, _byHandTop, 0.5f);
            mid.y = _resort.Surface.HeightAt(mid.x, mid.z);
            Camera.Frame(mid, 330);
            Camera.SetAngles(Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + 70, 52);
            return true;
        }

        // For captures: the ground the armed tool sees each frame instead of the pointer's (a fixed spot while the shot waits).
        Vector3? _groundForCapture;

        internal void ClearGroundForCapture() => _groundForCapture = null;

        ToolInput GroundInput(Vector3 g, bool click)
        {
            g.y = _resort.Surface.HeightAt(g.x, g.z);
            return new ToolInput { HasGround = true, Ground = g, Point = DesignFrame.ToDesign(g), InCore = _resort.Core.Contains(new Vector2(g.x, g.z)), Click = click };
        }

        /// <summary>For captures: New trail painted between two spots in the core, head and tail set, at its review.</summary>
        internal bool TrailReviewForCapture()
        {
            if (_tools == null || !_tools.Choose(NewTrailTool.ToolId) || !(_tools.Active is NewTrailTool trail)) return false;
            if (!CoreSpot(new Vector2(0.62f, 0.62f), out var from) || !CoreSpot(from + new Vector2(0.04f, -0.22f), out var to)) return false;
            PointForCapture(from, click: true, drag: true);
            for (int i = 1; i <= 30; i++) PointForCapture(Vector2.Lerp(from, to, i / 30f), click: false, drag: true);
            PointForCapture(to, click: false, release: true);
            KeysForCapture(ToolKeys.Next);
            if (trail.CurrentStep != 1) return false;
            PointForCapture(Vector2.Lerp(from, to, 0.03f), click: true);
            PointForCapture(Vector2.Lerp(from, to, 0.97f), click: true);
            KeysForCapture(ToolKeys.Next);
            return trail.CurrentStep == 2;
        }

        // The first screen spot near a fraction (searching outward) whose ground lies in the core box.
        bool CoreSpot(Vector2 near, out Vector2 fraction)
        {
            var cam = Camera.GetComponent<UnityEngine.Camera>();
            for (int ring = 0; ring < 12; ring++)
                for (int k = 0; k < Mathf.Max(1, ring * 8); k++)
                {
                    float a = k * Mathf.PI * 2 / Mathf.Max(1, ring * 8);
                    fraction = near + ring * 0.025f * new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                    var saved = _pointer;
                    _pointer = fraction;
                    var input = ReadToolInput(cam);
                    _pointer = saved;
                    if (input.HasGround && _resort.Core.Contains(new Vector2(input.Ground.x - 40, input.Ground.z - 40)) && _resort.Core.Contains(new Vector2(input.Ground.x + 40, input.Ground.z + 40))) return true;
                }
            fraction = default;
            return false;
        }

        ToolInput ReadToolInput(UnityEngine.Camera cam)
        {
            if (_groundForCapture.HasValue) return GroundInput(_groundForCapture.Value, false);
            var input = new ToolInput();
            var mouse = Mouse.current;
            var keys = Keyboard.current;
            Vector2? screen = _pointer.HasValue ? new Vector2(_pointer.Value.x * Screen.width, _pointer.Value.y * Screen.height)
                : mouse != null ? mouse.position.ReadValue() : (Vector2?)null;
            if (screen.HasValue)
            {
                input.Screen = screen.Value;
                input.OverPanel = PointerOverUi(screen.Value);
                if (!input.OverPanel)
                {
                    var ray = cam.ScreenPointToRay(screen.Value);
                    float hit = Camera.GroundAlong(ray, 30000);
                    if (!float.IsNaN(hit))
                    {
                        var g = ray.GetPoint(hit);
                        float y = _resort.Surface.HeightAt(g.x, g.z);
                        if (!float.IsNaN(y))
                        {
                            input.HasGround = true;
                            input.Ground = new Vector3(g.x, y, g.z);
                            input.Point = DesignFrame.ToDesign(input.Ground);
                            input.InCore = _resort.Core.Contains(new Vector2(g.x, g.z));
                            // One pixel's worth of ground at this distance: snapping reaches at least 6 of them.
                            input.MetresPerPixel = hit * 2 * Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, cam.pixelHeight);
                        }
                    }
                }
            }
            if (mouse != null)
            {
                var left = mouse.leftButton;
                if (left.wasPressedThisFrame && !input.OverPanel) { _pressOnMap = true; input.Click = true; }
                input.Drag = _pressOnMap && left.isPressed;
                if (_pressOnMap && left.wasReleasedThisFrame) { input.Release = true; _pressOnMap = false; }
            }
            if (keys != null && !UiHasKeyboard())
            {
                input.Erase = KeyBindings.Held(keys, GameAction.Erase);
                if (KeyBindings.Pressed(keys, GameAction.ToolNext)) input.Keys |= ToolKeys.Next;
                if (KeyBindings.Pressed(keys, GameAction.BreakGround)) input.Keys |= ToolKeys.BreakGround;
                if (KeyBindings.Pressed(keys, GameAction.BrushNarrower)) input.Keys |= ToolKeys.BrushNarrower;
                if (KeyBindings.Pressed(keys, GameAction.BrushWider)) input.Keys |= ToolKeys.BrushWider;
                if (KeyBindings.Pressed(keys, GameAction.UndoStroke)) input.Keys |= ToolKeys.UndoStroke;
                if (KeyBindings.Pressed(keys, GameAction.TowerUp)) input.Keys |= ToolKeys.TowerUp;
                if (KeyBindings.Pressed(keys, GameAction.TowerDown)) input.Keys |= ToolKeys.TowerDown;
                if (KeyBindings.Pressed(keys, GameAction.DeleteSelected)) input.Keys |= ToolKeys.Delete;
                // P3-09: the terminal in hand (LT19, LT21).
                if (KeyBindings.PressedAnyShift(keys, GameAction.TerminalTurnLeft)) input.Keys |= ToolKeys.TurnLeft;
                if (KeyBindings.PressedAnyShift(keys, GameAction.TerminalTurnRight)) input.Keys |= ToolKeys.TurnRight;
                if (KeyBindings.Pressed(keys, GameAction.TerminalLevelUp)) input.Keys |= ToolKeys.LevelUp;
                if (KeyBindings.Pressed(keys, GameAction.TerminalLevelDown)) input.Keys |= ToolKeys.LevelDown;
                if (KeyBindings.PressedAnyShift(keys, GameAction.TowerSlideBack)) input.Keys |= ToolKeys.SlideBack;
                if (KeyBindings.PressedAnyShift(keys, GameAction.TowerSlideOn)) input.Keys |= ToolKeys.SlideOn;
                input.Shift = keys.shiftKey.isPressed;
                if (input.Shift && mouse != null && KeyBindings.IsClaimed(GameAction.TerminalTurnLeft) && !input.OverPanel)
                {
                    float scroll = mouse.scroll.ReadValue().y;
                    if (Mathf.Abs(scroll) > 0.01f) input.TurnNotches = Mathf.Abs(scroll) >= 60 ? scroll / 120f : Mathf.Sign(scroll);
                }
            }
            return input;
        }
    }
}
