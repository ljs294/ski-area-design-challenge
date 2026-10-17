using MountainPlanner.Domain.Design;
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
        DesignOverlay _designOverlay;
        bool _pressOnMap;

        /// <summary>The session's design (task 04 loads and saves it; until then each opening starts empty).</summary>
        public DesignHistory Design => _design;

        /// <summary>The Toolbox's tool host (tests and captures).</summary>
        public ToolHost Tools => _tools;

        /// <summary>The pending preview (tests).</summary>
        internal PendingPreview Preview => _preview;

        /// <summary>
        /// Task 04's hook: replaces the session's design (a loaded save). The armed tool closes and the overlay follows
        /// the new history.
        /// </summary>
        public void SetDesign(DesignHistory design)
        {
            if (design == null || _resort == null || TitleMode) return;
            _tools?.Close();
            _designOverlay?.Dispose();
            _design = design;
            _designOverlay = new DesignOverlay(_resort.Root.transform, _resort.Surface, HighlightMaterial, _design);
        }

        void StartTools()
        {
            if (Hud == null || _resort == null || TitleMode || _tools != null) return;
            _design = new DesignHistory(DesignDocument.Empty(DesignSeed(_resort.Manifest.PackageId)));
            _preview = new PendingPreview(_resort.Root.transform, "Tool preview", _resort.Surface, HighlightMaterial);
            _designOverlay = new DesignOverlay(_resort.Root.transform, _resort.Surface, HighlightMaterial, _design);
            _tools = new ToolHost(_ => new ToolContext(_design, _preview, _resort.Surface, _resort.Core), Hud.OpenWindow);
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

        void StopTools()
        {
            _tools?.Close();
            _preview?.Dispose();
            _designOverlay?.Dispose();
            _tools = null;
            _preview = null;
            _designOverlay = null;
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
            // Undo and redo of committed work (P3-06 Q5); heights and trees follow once task 05 lands.
            if (KeyBindings.Pressed(keys, GameAction.Undo))
            {
                string label = _design.UndoLabel;
                Hud.Toast(_design.Undo() ? "Undid " + Lower(label) : "Nothing to undo");
            }
            else if (KeyBindings.Pressed(keys, GameAction.Redo))
            {
                string label = _design.RedoLabel;
                Hud.Toast(_design.Redo() ? "Redid " + Lower(label) : "Nothing to redo");
            }
        }

        static readonly GameAction[] TrayLetters = { GameAction.ToolNew, GameAction.ToolEdit, GameAction.ToolRemove };

        static string Lower(string label) => string.IsNullOrEmpty(label) ? "" : char.ToLowerInvariant(label[0]) + label.Substring(1);

        /// <summary>Once a frame: the armed tool gets the pointer's ground point, the left button and its keys.</summary>
        void TickTools()
        {
            if (_tools == null) return;
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            // A tool lives in the open tray (P3-06 Q4: T or Esc closes it); photo mode and the title close it too.
            if (_tools.Armed && (_photo || TitleMode || Hud == null || !Hud.ToolboxOpen)) _tools.Close();
            if (_tools.Armed && cam != null) _tools.Tick(ReadToolInput(cam));
            if (cam != null)
            {
                _preview?.Follow(cam);
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
            }
            return input;
        }
    }
}
