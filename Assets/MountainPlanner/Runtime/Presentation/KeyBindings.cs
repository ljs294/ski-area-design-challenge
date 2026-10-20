using System;
using System.Text;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.Utilities;

namespace MountainPlanner.Presentation
{
    /// <summary>Everything a key does in the mountain view (docs/plans/controls-key-map.md). Esc is fixed and not here.</summary>
    public enum GameAction
    {
        MoveForward, MoveBack, MoveLeft, MoveRight, RotateLeft, RotateRight, TiltUp, TiltDown,
        ZoomIn, ZoomOut, RiseOrZoomIn, SinkOrZoomOut, FreeFly, ResetView,
        Toolbox, Analysis, Pause, Speed1, Speed2, Speed3, Speed4, Units, HideUi, PhotoMode, PhotoCapture, DeveloperPanel,
        LayerSnow, LayerTrees, InfoContours, InfoSlope, InfoExposure, InfoDepth, InfoConditions,
        PhotoHideBar, PhotoEarlier, PhotoLater, PhotoDayBack, PhotoDayOn, PhotoBarKeys,
        Save,
        // P3-06: the Toolbox. The tray's letters, then the keys a construction tool reads while it's armed.
        ToolNew, ToolEdit, ToolRemove,
        ToolNext, BreakGround, BrushNarrower, BrushWider, Erase, UndoStroke, TowerUp, TowerDown, DeleteSelected, ToolWindowKeys,
        Undo, Redo,
        // P3-08: the Trails map layer (TL6).
        LayerTrails,
    }

    [Flags]
    public enum KeyMods { None = 0, Shift = 1, Ctrl = 2, Alt = 4 }

    /// <summary>One key with the modifiers held with it (Shift 1).</summary>
    public readonly struct KeyChord : IEquatable<KeyChord>
    {
        public readonly Key Key;
        public readonly KeyMods Mods;
        public KeyChord(Key key, KeyMods mods = KeyMods.None) { Key = key; Mods = mods; }
        public bool IsNone => Key == Key.None;
        public bool Equals(KeyChord o) => Key == o.Key && Mods == o.Mods;
        public override bool Equals(object obj) => obj is KeyChord o && Equals(o);
        public override int GetHashCode() => ((int)Key << 3) | (int)Mods;
        public override string ToString() => IsNone ? "" : (Mods == KeyMods.None ? "" : Mods.ToString().Replace(", ", "+") + "+") + Key;

        public static bool TryParse(string text, out KeyChord chord)
        {
            chord = default;
            if (string.IsNullOrEmpty(text)) return true;
            var mods = KeyMods.None;
            string[] parts = text.Split('+');
            for (int i = 0; i < parts.Length - 1; i++)
            {
                if (!Enum.TryParse(parts[i], out KeyMods m)) return false;
                mods |= m;
            }
            if (!Enum.TryParse(parts[parts.Length - 1], out Key key) || key == Key.None) return false;
            chord = new KeyChord(key, mods);
            return true;
        }
    }

    /// <summary>
    /// The key map, rebindable (Settings › Controls; owner, 2026-10-08; task P2-05). Each action has two slots,
    /// the defaults being the key map's. The viewer and camera ask <see cref="Pressed"/> or <see cref="Held"/>
    /// instead of reading keys themselves; neither allocates.
    ///
    /// Rules kept from the key map: Esc backs out and can't be rebound; Shift held with a movement key makes it
    /// faster; while the Toolbox tray is open (<c>letters</c> false) letter keys belong to the tools. A key taken
    /// by another action in the same context swaps places with it, so no two actions ever share a key.
    /// </summary>
    public static class KeyBindings
    {
        public const int Slots = 2;

        /// <summary>
        /// When an action works: in the normal view, in photo mode, or both; <see cref="Tray"/> while the Toolbox tray
        /// is open (its letters, and the keys of the armed tool: a tool is only ever armed with the tray open);
        /// <see cref="Always"/> for the camera, whose keys work at all times (owner, 2026-10-19: TL19).
        /// </summary>
        [Flags]
        public enum Context { Normal = 1, Photo = 2, Both = 3, Tray = 4, Always = 7 }

        public readonly struct Info
        {
            public readonly string Group, Label;
            public readonly Context When;
            public readonly KeyChord First, Second;
            /// <summary>Held like a modifier (Erase is Shift), so Shift, Ctrl or Alt alone may be its key.</summary>
            public readonly bool Hold;
            /// <summary>
            /// Live only while the armed tool claims it (the brush keys while painting, the tower keys with a tower in
            /// hand): it then shadows any other action on its key, so it may share one (Page Up zooms otherwise).
            /// </summary>
            public readonly bool WhileClaimed;
            public Info(string group, string label, Context when, KeyChord first, KeyChord second = default, bool hold = false,
                        bool whileClaimed = false)
            {
                Group = group; Label = label; When = when; First = first; Second = second; Hold = hold; WhileClaimed = whileClaimed;
            }
        }

        /// <summary>Every action, in <see cref="GameAction"/> order: its group and label on the Controls page, and its defaults.</summary>
        public static readonly Info[] Actions =
        {
            new Info("Camera", "Move forward", Context.Always, new KeyChord(Key.W), new KeyChord(Key.UpArrow)),
            new Info("Camera", "Move back", Context.Always, new KeyChord(Key.S), new KeyChord(Key.DownArrow)),
            new Info("Camera", "Move left", Context.Always, new KeyChord(Key.A), new KeyChord(Key.LeftArrow)),
            new Info("Camera", "Move right", Context.Always, new KeyChord(Key.D), new KeyChord(Key.RightArrow)),
            new Info("Camera", "Rotate left", Context.Always, new KeyChord(Key.Q)),
            new Info("Camera", "Rotate right", Context.Always, new KeyChord(Key.E)),
            new Info("Camera", "Tilt up", Context.Always, new KeyChord(Key.R)),
            new Info("Camera", "Tilt down", Context.Always, new KeyChord(Key.F)),
            new Info("Camera", "Zoom in", Context.Always, new KeyChord(Key.Equals), new KeyChord(Key.NumpadPlus)),
            new Info("Camera", "Zoom out", Context.Always, new KeyChord(Key.Minus), new KeyChord(Key.NumpadMinus)),
            new Info("Camera", "Zoom in · free-fly: rise", Context.Always, new KeyChord(Key.PageUp)),
            new Info("Camera", "Zoom out · free-fly: sink", Context.Always, new KeyChord(Key.PageDown)),
            new Info("Camera", "Free-fly", Context.Always, new KeyChord(Key.C)),
            new Info("Camera", "Reset the view", Context.Always, new KeyChord(Key.Home)),
            new Info("Game", "Toolbox", Context.Normal, new KeyChord(Key.T)),
            new Info("Game", "Analysis", Context.Normal, new KeyChord(Key.Tab)),
            new Info("Game", "Pause", Context.Normal, new KeyChord(Key.Space)),
            new Info("Game", "Speed 1", Context.Normal, new KeyChord(Key.Digit1)),
            new Info("Game", "Speed 2", Context.Normal, new KeyChord(Key.Digit2)),
            new Info("Game", "Speed 3", Context.Normal, new KeyChord(Key.Digit3)),
            new Info("Game", "Speed 4", Context.Normal, new KeyChord(Key.Digit4)),
            new Info("Game", "Units", Context.Normal, new KeyChord(Key.U)),
            new Info("Game", "Hide the UI", Context.Normal, new KeyChord(Key.H)),
            new Info("Game", "Photo mode", Context.Both, new KeyChord(Key.P)),
            new Info("Game", "Photo mode: save a photo", Context.Photo, new KeyChord(Key.F12), new KeyChord(Key.Space)),
            new Info("Game", "Developer panel", Context.Both, new KeyChord(Key.F1)),
            new Info("Layers", "Snow", Context.Normal, new KeyChord(Key.Digit1, KeyMods.Shift)),
            new Info("Layers", "Trees", Context.Normal, new KeyChord(Key.Digit3, KeyMods.Shift)),
            new Info("Layers", "Contours", Context.Normal, new KeyChord(Key.Digit6, KeyMods.Shift)),
            new Info("Layers", "Slope angle", Context.Normal, new KeyChord(Key.Digit7, KeyMods.Shift)),
            new Info("Layers", "Slope exposure", Context.Normal, new KeyChord(Key.Digit8, KeyMods.Shift)),
            new Info("Layers", "Snow depth", Context.Normal, new KeyChord(Key.Digit9, KeyMods.Shift)),
            new Info("Layers", "Snow conditions", Context.Normal, new KeyChord(Key.Digit0, KeyMods.Shift)),
            // Photo mode's bar (task P2-07): H hides it, [ and ] move the sun a quarter hour, with Shift a day.
            new Info("Photo mode", "Hide the photo bar", Context.Photo, new KeyChord(Key.H)),
            new Info("Photo mode", "Earlier", Context.Photo, new KeyChord(Key.LeftBracket)),
            new Info("Photo mode", "Later", Context.Photo, new KeyChord(Key.RightBracket)),
            new Info("Photo mode", "A day back", Context.Photo, new KeyChord(Key.LeftBracket, KeyMods.Shift)),
            new Info("Photo mode", "A day on", Context.Photo, new KeyChord(Key.RightBracket, KeyMods.Shift)),
            // P3-01: the arrows move the camera, so Tab hands the bar the keyboard; Esc gives it back.
            new Info("Photo mode", "Use the photo bar with the keys", Context.Photo, new KeyChord(Key.Tab)),
            // P3-04: the design's save (the menu's Save game, the bar's save symbol).
            new Info("Game", "Save", Context.Normal, new KeyChord(Key.S, KeyMods.Ctrl)),
            // P3-06: the tray's letters (the Lifts and Trails tabs), then the tools' own keys (TF-09, LF-09; SH-07).
            new Info("Toolbox", "New", Context.Tray, new KeyChord(Key.N)),
            new Info("Toolbox", "Edit", Context.Tray, new KeyChord(Key.M)),   // E rotates the camera (TL19)
            new Info("Toolbox", "Remove", Context.Tray, new KeyChord(Key.X)),
            new Info("Building", "Next step", Context.Tray, new KeyChord(Key.Enter), new KeyChord(Key.NumpadEnter)),
            new Info("Building", "Break ground", Context.Tray, new KeyChord(Key.Enter, KeyMods.Ctrl), new KeyChord(Key.NumpadEnter, KeyMods.Ctrl)),
            new Info("Building", "Brush narrower", Context.Tray, new KeyChord(Key.LeftBracket), whileClaimed: true),
            new Info("Building", "Brush wider", Context.Tray, new KeyChord(Key.RightBracket), whileClaimed: true),
            new Info("Building", "Erase while painting (hold)", Context.Tray, new KeyChord(Key.LeftShift), new KeyChord(Key.RightShift), hold: true, whileClaimed: true),
            new Info("Building", "Undo a stroke", Context.Tray, new KeyChord(Key.Z, KeyMods.Ctrl), whileClaimed: true),
            new Info("Building", "Tower up 5 ft", Context.Tray, new KeyChord(Key.PageUp), whileClaimed: true),
            new Info("Building", "Tower down 5 ft", Context.Tray, new KeyChord(Key.PageDown), whileClaimed: true),
            new Info("Building", "Delete the selected", Context.Tray, new KeyChord(Key.Delete)),
            // As the photo bar's Tab (P3-01): the tool window takes the keyboard; Esc gives it back.
            new Info("Building", "Use the tool window with the keys", Context.Tray, new KeyChord(Key.Tab)),
            new Info("Game", "Undo", Context.Normal, new KeyChord(Key.Z, KeyMods.Ctrl)),
            new Info("Game", "Redo", Context.Normal, new KeyChord(Key.Y, KeyMods.Ctrl), new KeyChord(Key.Z, KeyMods.Ctrl | KeyMods.Shift)),
            // P3-08: the Trails map layer (owner TL6).
            new Info("Layers", "Trails", Context.Normal, new KeyChord(Key.Digit4, KeyMods.Shift)),
        };

        /// <summary>Keys no action may take: Esc backs out, Enter finishes a line, the modifiers on their own.</summary>
        public static bool Reserved(Key key) =>
            key == Key.Escape || key == Key.Enter || key == Key.NumpadEnter || IsModifier(key);

        /// <summary>
        /// Whether this action may not take the key: Esc never; Enter only a tool's key (Enter is Next step, TF-09);
        /// Shift, Ctrl or Alt alone only an action held like a modifier (Erase).
        /// </summary>
        public static bool Refuses(GameAction action, Key key)
        {
            if (key == Key.Escape || key == Key.ContextMenu) return true;
            var info = Actions[(int)action];
            if (key == Key.Enter || key == Key.NumpadEnter) return info.When != Context.Tray;
            if (IsModifier(key)) return !info.Hold;
            return false;
        }

        static bool IsModifier(Key key) =>
            key == Key.LeftShift || key == Key.RightShift || key == Key.LeftCtrl || key == Key.RightCtrl ||
            key == Key.LeftAlt || key == Key.RightAlt || key == Key.LeftMeta || key == Key.RightMeta || key == Key.ContextMenu;

        static readonly KeyChord[] Bound = new KeyChord[Actions.Length * Slots];
        static bool _loaded;

        /// <summary>Raised after any binding changes, so the HUD can redraw its key captions.</summary>
        public static event Action Changed;

        public static int Count => Actions.Length;

        public static KeyChord Get(GameAction action, int slot)
        {
            Load();
            return Bound[(int)action * Slots + slot];
        }

        public static KeyChord Default(GameAction action, int slot) => slot == 0 ? Actions[(int)action].First : Actions[(int)action].Second;

        // ---------- the armed tool's keys win (P3-06) ----------

        // The tool keys the armed tool is using now. Any other action on the same key waits while the claim lasts:
        // Page Up and Page Down move a tower in hand instead of zooming (LF-09), Ctrl+Z undoes a stroke instead of
        // the last command, and a tool key someone bound to Space doesn't also pause.
        static readonly bool[] Claimed = new bool[Actions.Length];
        static int _claims;

        /// <summary>Marks a tool key as in use (or not) by the armed tool. The tool host clears every claim on disarm.</summary>
        public static void Claim(GameAction action, bool on)
        {
            if (Claimed[(int)action] == on) return;
            Claimed[(int)action] = on;
            _claims += on ? 1 : -1;
        }

        public static bool IsClaimed(GameAction action) => Claimed[(int)action];

        public static void ClearClaims()
        {
            Array.Clear(Claimed, 0, Claimed.Length);
            _claims = 0;
        }

        // Another action's key is shadowed while a claimed tool key uses the same key.
        static bool Shadowed(int action, KeyChord chord)
        {
            if (_claims == 0 || Claimed[action]) return false;
            for (int a = 0; a < Claimed.Length; a++)
            {
                if (!Claimed[a]) continue;
                if (Bound[a * Slots].Key == chord.Key || Bound[a * Slots + 1].Key == chord.Key) return true;
            }
            return false;
        }

        // ---------- reading the keyboard (per frame, no allocation) ----------

        static KeyMods ModsOf(Keyboard k) =>
            (k.shiftKey.isPressed ? KeyMods.Shift : 0) | (k.ctrlKey.isPressed ? KeyMods.Ctrl : 0) | (k.altKey.isPressed ? KeyMods.Alt : 0);

        static bool IsLetter(Key key) => key >= Key.A && key <= Key.Z;

        /// <summary>The action's key went down this frame with exactly its modifiers. <paramref name="letters"/> false skips letter keys (the Toolbox tray is open).</summary>
        public static bool Pressed(Keyboard keyboard, GameAction action, bool letters = true)
        {
            if (keyboard == null) return false;
            Load();
            var mods = ModsOf(keyboard);
            int i = (int)action * Slots;
            for (int s = 0; s < Slots; s++)
            {
                var c = Bound[i + s];
                if (c.IsNone || c.Mods != mods || (!letters && IsLetter(c.Key)) || Shadowed((int)action, c)) continue;
                if (keyboard[c.Key].wasPressedThisFrame) return true;
            }
            return false;
        }

        /// <summary>The action's key is down. Shift is free (it means faster) unless the binding itself has Shift; Ctrl and Alt must match.</summary>
        public static bool Held(Keyboard keyboard, GameAction action, bool letters = true)
        {
            if (keyboard == null) return false;
            Load();
            var mods = ModsOf(keyboard);
            int i = (int)action * Slots;
            for (int s = 0; s < Slots; s++)
            {
                var c = Bound[i + s];
                if (c.IsNone || (!letters && IsLetter(c.Key)) || Shadowed((int)action, c)) continue;
                var need = c.Mods & ~KeyMods.Shift;
                if ((mods & ~KeyMods.Shift) != need || ((c.Mods & KeyMods.Shift) != 0 && (mods & KeyMods.Shift) == 0)) continue;
                if (keyboard[c.Key].isPressed) return true;
            }
            return false;
        }

        // ---------- captions ----------

        /// <summary>A binding as the HUD and Settings show it: "Shift 1", "Page Up", "↑".</summary>
        public static string Caption(KeyChord chord)
        {
            if (chord.IsNone) return "";
            var sb = new StringBuilder();
            if ((chord.Mods & KeyMods.Ctrl) != 0) sb.Append("Ctrl ");
            if ((chord.Mods & KeyMods.Alt) != 0) sb.Append("Alt ");
            if ((chord.Mods & KeyMods.Shift) != 0) sb.Append("Shift ");
            sb.Append(KeyName(chord.Key));
            return sb.ToString();
        }

        public static string Caption(GameAction action, int slot) => Caption(Get(action, slot));

        /// <summary>The first binding's caption, or the second's when the first is empty ("" with none).</summary>
        public static string Caption(GameAction action)
        {
            var c = Get(action, 0);
            return Caption(c.IsNone ? Get(action, 1) : c);
        }

        static string KeyName(Key key)
        {
            switch (key)
            {
                case Key.UpArrow: return "↑";
                case Key.DownArrow: return "↓";
                case Key.LeftArrow: return "←";
                case Key.RightArrow: return "→";
                case Key.Equals: return "+";
                case Key.Minus: return "−";
                case Key.NumpadPlus: return "Num +";
                case Key.NumpadMinus: return "Num −";
                case Key.PageUp: return "Page Up";
                case Key.PageDown: return "Page Down";
                case Key.Space: return "Space";
                case Key.Backquote: return "`";
                case Key.Quote: return "'";
                case Key.Semicolon: return ";";
                case Key.Comma: return ",";
                case Key.Period: return ".";
                case Key.Slash: return "/";
                case Key.Backslash: return "\\";
                case Key.LeftBracket: return "[";
                case Key.RightBracket: return "]";
                case Key.Enter: return "Enter";
                case Key.NumpadEnter: return "Num Enter";
                case Key.Delete: return "Delete";
                case Key.LeftShift: return "Shift";
                case Key.RightShift: return "Right Shift";
                case Key.LeftCtrl: return "Ctrl";
                case Key.RightCtrl: return "Right Ctrl";
                case Key.LeftAlt: return "Alt";
                case Key.RightAlt: return "Right Alt";
            }
            if (key >= Key.Digit1 && key <= Key.Digit0) return key == Key.Digit0 ? "0" : ((int)(key - Key.Digit1) + 1).ToString();
            if (key >= Key.Numpad0 && key <= Key.Numpad9) return "Num " + (int)(key - Key.Numpad0);
            // The letter on the player's own keyboard layout (an AZERTY player sees A where QWERTY has Q).
            var keyboard = Keyboard.current;
            if (keyboard != null && IsLetter(key))
            {
                string shown = keyboard[key].displayName;
                if (!string.IsNullOrEmpty(shown)) return shown.ToUpperInvariant();
            }
            return key.ToString();
        }

        // ---------- changing bindings ----------

        /// <summary>
        /// Binds a slot. Another action in an overlapping context that had the same key gets this slot's old key in
        /// its place (a swap); returns that action, or null. Reserved keys are refused (returns null, nothing changes).
        /// </summary>
        public static GameAction? Bind(GameAction action, int slot, KeyChord chord, bool remember = true)
        {
            Load();
            if (!chord.IsNone && Refuses(action, chord.Key)) return null;
            int at = (int)action * Slots + slot;
            var old = Bound[at];
            if (old.Equals(chord)) return null;
            GameAction? swapped = null;
            if (!chord.IsNone)
            {
                for (int i = 0; i < Bound.Length; i++)
                {
                    if (i == at || !Bound[i].Equals(chord)) continue;
                    var other = (GameAction)(i / Slots);
                    if (other == action) { Bound[i] = old; continue; }   // the action's own other slot: swap the two
                    if ((Actions[(int)other].When & Actions[(int)action].When) == 0) continue;
                    if (Actions[(int)other].WhileClaimed || Actions[(int)action].WhileClaimed) continue;   // it shadows, never swaps
                    Bound[i] = old;
                    swapped = other;
                }
            }
            Bound[at] = chord;
            if (remember) Save();
            Changed?.Invoke();
            return swapped;
        }

        /// <summary>Every action back to the key map's keys.</summary>
        public static void ResetAll(bool remember = true)
        {
            for (int a = 0; a < Actions.Length; a++)
            {
                Bound[a * Slots] = Actions[a].First;
                Bound[a * Slots + 1] = Actions[a].Second;
            }
            _loaded = true;
            if (remember) Save();
            Changed?.Invoke();
        }

        public static bool IsDefault
        {
            get
            {
                Load();
                for (int a = 0; a < Actions.Length; a++)
                    if (!Bound[a * Slots].Equals(Actions[a].First) || !Bound[a * Slots + 1].Equals(Actions[a].Second)) return false;
                return true;
            }
        }

        // ---------- listening for the next key ----------

        public enum Outcome { Bound, Swapped, Cleared, Cancelled, Reserved }

        static IDisposable _listening;

        public static bool Listening => _listening != null;

        /// <summary>
        /// Waits for the next key (with whatever modifiers are held) and binds it to the slot. Esc cancels;
        /// Backspace or Delete clears the second slot (the first keeps its key). <paramref name="done"/> gets the
        /// outcome and, after a swap, the action that gave its key up.
        /// </summary>
        public static void Listen(GameAction action, int slot, bool remember, Action<Outcome, GameAction?> done)
        {
            StopListening();
            _listening = InputSystem.onAnyButtonPress
                .Where(c => c is KeyControl k && (!IsModifier(k.keyCode) || Actions[(int)action].Hold))
                .CallOnce(c =>
                {
                    _listening = null;
                    var key = ((KeyControl)c).keyCode;
                    var keyboard = c.device as Keyboard;
                    if (key == Key.Escape) { done?.Invoke(Outcome.Cancelled, null); return; }
                    if (key == Key.Backspace || key == Key.Delete)
                    {
                        if (slot == 0) { done?.Invoke(Outcome.Cancelled, null); return; }
                        Bind(action, slot, default, remember);
                        done?.Invoke(Outcome.Cleared, null);
                        return;
                    }
                    if (Refuses(action, key)) { done?.Invoke(Outcome.Reserved, null); return; }
                    // A key held like a modifier (Erase) is bound alone, without itself as a modifier.
                    var mods = IsModifier(key) || keyboard == null ? KeyMods.None : ModsOf(keyboard);
                    var swapped = Bind(action, slot, new KeyChord(key, mods), remember);
                    done?.Invoke(swapped.HasValue ? Outcome.Swapped : Outcome.Bound, swapped);
                });
        }

        public static void StopListening()
        {
            _listening?.Dispose();
            _listening = null;
        }

        // ---------- the store ----------

        const string StoreKey = "Keys";

        static void Load()
        {
            if (_loaded) return;
            ResetAll(remember: false);
            // "Action=First,Second;..." for the actions that differ from the key map; unknown names and keys are skipped.
            string text = SettingsStore.GetString(StoreKey, "");
            foreach (string entry in text.Split(';'))
            {
                int eq = entry.IndexOf('=');
                if (eq <= 0 || !Enum.TryParse(entry.Substring(0, eq), out GameAction action) || !Enum.IsDefined(typeof(GameAction), action)) continue;
                string[] slots = entry.Substring(eq + 1).Split(',');
                for (int s = 0; s < Slots && s < slots.Length; s++)
                    if (KeyChord.TryParse(slots[s], out var chord) && (chord.IsNone || !Refuses(action, chord.Key))) Bound[(int)action * Slots + s] = chord;
            }
        }

        /// <summary>Forgets what was read, so the next use reads the store again (tests).</summary>
        internal static void Reload() => _loaded = false;

        static void Save()
        {
            var sb = new StringBuilder();
            for (int a = 0; a < Actions.Length; a++)
            {
                var first = Bound[a * Slots];
                var second = Bound[a * Slots + 1];
                if (first.Equals(Actions[a].First) && second.Equals(Actions[a].Second)) continue;
                if (sb.Length > 0) sb.Append(';');
                sb.Append((GameAction)a).Append('=').Append(first).Append(',').Append(second);
            }
            SettingsStore.SetString(StoreKey, sb.ToString());
        }
    }
}
