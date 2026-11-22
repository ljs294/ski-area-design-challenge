using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Domain.Design;
using MountainPlanner.UI.Tools;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.UIElements;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.26 (BB12, BB13, BB14): <c>-typecheck &lt;out.txt&gt;</c> with <c>-drawnresort</c> types into the game's name
    /// fields the way a player does: real mouse clicks and key presses queued on the Input System's own devices, so the
    /// HUD, the camera and the tools all see them. For each field it clicks the name, types a new one key by key, presses
    /// Enter and reads back what the game kept; it also checks that the camera didn't move and no tool key fired while
    /// typing, and logs the slowest frame while typing. One line per check (PASS or FAIL), then it quits 0 (1 on a FAIL).
    /// Not a scripted run in <see cref="BeginScriptedRun"/>'s sense: that would silence the very keys it tests.
    /// </summary>
    public sealed partial class MountainViewer
    {
        readonly List<string> _typeLines = new List<string>();
        bool _typeFailed;

        void StartTypeCheck(string[] args)
        {
            int i = Array.IndexOf(args, "-typecheck");
            if (i < 0 || i + 1 >= args.Length) return;
            StartCoroutine(TypeCheck(Path.GetFullPath(args[i + 1])));
        }

        void TypeResult(bool ok, string what)
        {
            if (!ok) _typeFailed = true;
            string line = (ok ? "PASS " : "FAIL ") + what;
            _typeLines.Add(line);
            Debug.Log("[TypeCheck] " + line);
        }

        IEnumerator TypeCheck(string outPath)
        {
            // The area fully open, as the other captures wait for it.
            for (int k = 0; k < 120_000; k++)
            {
                if (_resort != null && _resort.CoverReady.IsCompleted) EnsureTerrainEdits();
                if (_resort != null && _resort.CoverReady.IsCompleted && _edits != null && !WorkBusy && Hud != null && Design != null && _tools != null) break;
                yield return null;
            }
            for (int k = 0; k < 120; k++) yield return null;
            if (Keyboard.current == null) InputSystem.AddDevice<Keyboard>();
            if (Mouse.current == null) InputSystem.AddDevice<Mouse>();
            var root = Hud.Root;

            // 1. New trail: the tool window's name.
            Hud.SetToolbox(true);
            Hud.SetToolboxTab("trails");
            yield return Frames(5);
            _tools.Choose(NewTrailTool.ToolId);
            yield return Frames(10);
            yield return TypeInto(root, "toolwin-name", "toolwin-name-field", "Powder Run", "new trail window");
            var title = root.Q<Label>("toolwin-name");
            TypeResult(title != null && title.text == "Powder Run", $"new trail: the window's name reads \"{title?.text}\"");
            Hud.SetToolbox(false);
            yield return Frames(10);

            // 2. A trail's card.
            var trail = Design.Document.Trails.FirstOrDefault(t => t.Stage == WorkStage.Built);
            if (trail == null) TypeResult(false, "no built trail on this design");
            else
            {
                Hud.OpenTrailCardFor(TrailRowOf(trail.Id.Value, 0));
                yield return Frames(10);
                yield return TypeInto(root, "trail-card-name", "trail-card-name-field", "Bear Trap", "trail card");
                var now = Design.Document.FindTrail(trail.Id);
                TypeResult(now != null && now.Name == "Bear Trap", $"trail card: the trail is named \"{now?.Name}\"");

                // 3. Edit trail, from the card's Edit: the tool window's name renames the picked trail.
                yield return Frames(5);
                if (root.Q<Label>("trail-card-name")?.panel == null || root.Q("trail-card")?.ClassListContains("hidden") == true) Hud.OpenTrailCardFor(TrailRowOf(trail.Id.Value, 0));
                yield return Frames(10);
                yield return ClickElement(root.Q("trail-card-edit"));
                yield return Frames(30);
                TypeResult(_tools.ActiveId == EditTrailTool.ToolId, $"trail card Edit opens Edit trail (active: {_tools.ActiveId ?? "none"})");
                for (int k = 0; k < 600 && _tools.Context != null && _tools.Context.Running > 0; k++) yield return null;
                yield return Frames(10);
                yield return TypeInto(root, "toolwin-name", "toolwin-name-field", "Lower Bear", "edit trail window");
                now = Design.Document.FindTrail(trail.Id);
                TypeResult(now != null && now.Name == "Lower Bear", $"edit trail: the trail is named \"{now?.Name}\"");
                Hud.SetToolbox(false);
                yield return Frames(10);
            }

            // 4. A lift's card: its name, then its plate left blank (BB14) and typed.
            var lift = Design.Document.Lifts.FirstOrDefault(l => l.Stage == WorkStage.Built);
            if (lift == null) TypeResult(false, "no built lift on this design");
            else
            {
                Hud.OpenLiftCardFor(lift.Id.Value);
                yield return Frames(10);
                yield return TypeInto(root, "lift-card-name", "lift-card-name-field", "Summit Six", "lift card");
                var l2 = Design.Document.FindLift(lift.Id);
                TypeResult(l2 != null && l2.Name == "Summit Six", $"lift card: the lift is named \"{l2?.Name}\"");
                string plateBefore = l2?.Plate ?? "";
                yield return TypeInto(root, "lift-card-plate", "lift-card-plate-field", plateBefore.Length > 0 ? null : "", "lift plate left as it was");
                var chip = root.Q<Label>("lift-card-plate");
                TypeResult(chip != null && !chip.ClassListContains("hidden") && chip.resolvedStyle.display != DisplayStyle.None,
                           $"lift plate left as it was: the chip still shows (\"{chip?.text}\")");
                yield return TypeInto(root, "lift-card-plate", "lift-card-plate-field", "7", "lift plate");
                l2 = Design.Document.FindLift(lift.Id);
                TypeResult(l2 != null && l2.Plate == "7" && chip != null && !chip.ClassListContains("hidden"), $"lift plate: the lift's plate is \"{l2?.Plate}\"");
            }

            File.WriteAllText(outPath, string.Join("\n", _typeLines) + "\n", new UTF8Encoding(false));
            Debug.Log($"[TypeCheck] {(_typeFailed ? "FAILED" : "passed")}: {outPath}");
            Application.Quit(_typeFailed ? 1 : 0);
        }

        static IEnumerator Frames(int n)
        {
            for (int k = 0; k < n; k++) yield return null;
        }

        /// <summary>
        /// Clicks <paramref name="nameLabel"/>, checks its field opened with the keyboard, selects what's there (Ctrl+A),
        /// types <paramref name="text"/> key by key (null: types nothing) and presses Enter.
        /// </summary>
        IEnumerator TypeInto(VisualElement root, string nameLabel, string fieldName, string text, string what)
        {
            var label = root.Q(nameLabel);
            if (label == null || label.panel == null) { TypeResult(false, $"{what}: no {nameLabel} on screen"); yield break; }
            var picked = label.panel.Pick(label.worldBound.center);
            Debug.Log($"[TypeCheck] {what}: {nameLabel} at {label.worldBound}, picking {label.pickingMode}, hidden {label.ClassListContains("hidden")}, the click lands on {Describe(picked)}");
            yield return ClickElement(label);
            yield return Frames(5);
            var field = root.Q<TextField>(fieldName);
            bool open = field != null && field.panel != null;
            bool focused = open && root.panel.focusController.focusedElement is VisualElement f && (f == field || field.Contains(f));
            TypeResult(open && focused, $"{what}: a click opens the field with the keyboard (open {open}, focused {focused})");
            if (!open) yield break;

            // A second click inside the field (to place the caret) must not close it.
            yield return ClickElement(field);
            yield return Frames(3);
            bool still = field.panel != null;
            TypeResult(still, $"{what}: a click inside the field keeps it open");
            if (!still) yield break;

            Vector3 camBefore = Camera != null ? Camera.Target : Vector3.zero;
            string toolBefore = _tools.ActiveId;
            yield return Chord(Key.A, ctrl: true);
            float worst = 0;
            int frames = 0;
            if (text != null)
            {
                if (text.Length == 0) yield return Chord(Key.Backspace);
                foreach (char c in text)
                {
                    yield return TypeChar(c, d => { worst = Mathf.Max(worst, d); frames++; });
                    if (field.panel == null) break;
                }
            }
            bool kept = field.panel != null;
            string typed = field.value;
            TypeResult(kept && (text == null || typed == text), $"{what}: typing \"{text}\" reads \"{typed}\" (field open {kept})");
            Vector3 camAfter = Camera != null ? Camera.Target : Vector3.zero;
            TypeResult((camAfter - camBefore).sqrMagnitude < 0.01f, $"{what}: the camera stayed put while typing (moved {(camAfter - camBefore).magnitude:0.00} m)");
            TypeResult(_tools.ActiveId == toolBefore, $"{what}: no tool key fired while typing (tool {toolBefore ?? "none"} -> {_tools.ActiveId ?? "none"})");
            TypeResult(worst < 0.1f, $"{what}: slowest frame while typing {worst * 1000:0} ms over {frames} frames");
            yield return Chord(Key.Enter);
            yield return Frames(5);
            TypeResult(root.Q<TextField>(fieldName)?.panel == null, $"{what}: Enter closes the field");
            var shown = root.Q(nameLabel);
            TypeResult(shown != null && !shown.ClassListContains("hidden"), $"{what}: the name shows again after Enter");
        }

        /// <summary>A real click (press and release) at the middle of <paramref name="e"/> on screen.</summary>
        IEnumerator ClickElement(VisualElement e)
        {
            if (e == null || e.panel == null) yield break;
            Vector2 screen = PanelToScreen(e.panel, e.worldBound.center);
            var mouse = Mouse.current;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screen });
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screen }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, true));
            yield return null;
            yield return null;
            InputSystem.QueueStateEvent(mouse, new MouseState { position = screen }.WithButton(UnityEngine.InputSystem.LowLevel.MouseButton.Left, false));
            yield return null;
            yield return null;
        }

        /// <summary>Screen pixels (origin bottom-left) of a panel point: the inverse of RuntimePanelUtils.ScreenToPanel.</summary>
        static Vector2 PanelToScreen(IPanel panel, Vector2 p)
        {
            Vector2 a = RuntimePanelUtils.ScreenToPanel(panel, Vector2.zero);
            Vector2 b = RuntimePanelUtils.ScreenToPanel(panel, new Vector2(1000, 1000));
            Vector2 scale = (b - a) / 1000f;
            Vector2 top = new Vector2((p.x - a.x) / scale.x, (p.y - a.y) / scale.y);   // screen, origin top-left
            return new Vector2(top.x, Screen.height - top.y);
        }

        IEnumerator Chord(Key key, bool ctrl = false, bool shift = false)
        {
            var kb = Keyboard.current;
            var down = new List<Key> { key };
            if (ctrl) down.Add(Key.LeftCtrl);
            if (shift) down.Add(Key.LeftShift);
            InputSystem.QueueStateEvent(kb, new KeyboardState(down.ToArray()));
            KeyCode code = key == Key.Enter ? KeyCode.Return : key == Key.Backspace ? KeyCode.Backspace : key == Key.Escape ? KeyCode.Escape
                : key >= Key.A && key <= Key.Z ? KeyCode.A + (key - Key.A) : KeyCode.None;
            SendUiKey('\0', code, (ctrl ? EventModifiers.Control : EventModifiers.None) | (shift ? EventModifiers.Shift : EventModifiers.None));
            yield return null;
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            yield return null;
        }

        /// <summary>
        /// The key as UI Toolkit receives it from the OS: a KeyDownEvent to whatever has the focus. Input System device events
        /// (queued beside it) reach the camera and the tools but not a panel's text input, which reads the OS's own events.
        /// </summary>
        void SendUiKey(char c, KeyCode code, EventModifiers mods)
        {
            var focused = Hud?.Root?.panel?.focusController?.focusedElement as VisualElement;
            if (focused == null) return;
            focused = focused.Q<TextElement>() ?? focused;   // the text editor's handlers sit on the field's text element
            using (var e = KeyDownEvent.GetPooled(c, code, mods))
            {
                e.target = focused;
                focused.SendEvent(e);
            }
            if (c != '\0') return;
            using (var up = KeyUpEvent.GetPooled(c, code, mods)) { up.target = focused; focused.SendEvent(up); }
        }

        static string Describe(VisualElement e)
        {
            var sb = new StringBuilder();
            for (int k = 0; e != null && k < 4; k++, e = e.parent)
                sb.Append(k > 0 ? " < " : "").Append(string.IsNullOrEmpty(e.name) ? e.GetType().Name : e.name);
            return sb.Length > 0 ? sb.ToString() : "nothing";
        }

        /// <summary>One character as a player types it: its key goes down with the text event, then up.</summary>
        IEnumerator TypeChar(char c, Action<float> frame)
        {
            var kb = Keyboard.current;
            Key key = c == ' ' ? Key.Space
                : char.IsLetter(c) ? Key.A + (char.ToLowerInvariant(c) - 'a')
                : char.IsDigit(c) ? (c == '0' ? Key.Digit0 : Key.Digit1 + (c - '1'))
                : Key.None;
            bool shift = char.IsUpper(c);
            var down = new List<Key>();
            if (key != Key.None) down.Add(key);
            if (shift) down.Add(Key.LeftShift);
            InputSystem.QueueStateEvent(kb, new KeyboardState(down.ToArray()));
            SendUiKey(c, KeyCode.None, shift ? EventModifiers.Shift : EventModifiers.None);
            yield return null;
            frame(Time.unscaledDeltaTime);
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            yield return null;
            frame(Time.unscaledDeltaTime);
        }
    }
}
