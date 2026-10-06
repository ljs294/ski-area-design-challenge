using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Keyboard use of every screen (0.4 §1 and §5; task P2-01), per document root:
    /// <list type="bullet">
    /// <item>a focus ring: one frame laid over whatever has keyboard focus, shown once a navigation key is used and
    /// hidden again by a click, so mouse players never see it;</item>
    /// <item>arrow keys move focus to the nearest control in that direction (Tab and Shift+Tab keep UI Toolkit's
    /// order; Enter and Space press). Controls that use the arrows themselves keep them: text fields and sliders
    /// across, and anything marked <c>ui-own-arrows</c> (the picker's map, the library rows);</item>
    /// <item>modals (<see cref="OpenModal"/>): focus stays inside the top one and goes back where it was when it
    /// closes.</item>
    /// </list>
    /// Nothing here runs per frame except a 10 Hz check that keeps the ring on a control that moves (a scrolled
    /// list); it allocates nothing.
    /// </summary>
    public static class UiFocus
    {
        public const string OwnArrowsClass = "ui-own-arrows";
        public const string DefaultFocusClass = "ui-default-focus";
        const string KeysClass = "ui-keys", RingClass = "ui-focus-ring", RingOnClass = "ui-focus-ring--on";
        const float RingOutset = 3;

        sealed class RootState
        {
            public VisualElement Root, Ring;
            public VisualElement Focused;
            public Rect Shown;
            public readonly List<(VisualElement Modal, Focusable Before)> Modals = new List<(VisualElement, Focusable)>();
        }

        static readonly Dictionary<VisualElement, RootState> Roots = new Dictionary<VisualElement, RootState>();
        static readonly List<VisualElement> Candidates = new List<VisualElement>();

        /// <summary>Adds the ring and the key handling to a document root (once).</summary>
        public static void Prepare(VisualElement root)
        {
            if (root == null || Roots.ContainsKey(root)) return;
            var state = new RootState { Root = root };
            state.Ring = new VisualElement { name = "ui-focus-ring", pickingMode = PickingMode.Ignore };
            state.Ring.AddToClassList(RingClass);
            root.Add(state.Ring);
            Roots[root] = state;
            root.RegisterCallback<NavigationMoveEvent>(e => OnMove(state, e), TrickleDown.TrickleDown);
            root.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Tab) SetKeys(state, true); }, TrickleDown.TrickleDown);
            root.RegisterCallback<PointerDownEvent>(_ => SetKeys(state, false), TrickleDown.TrickleDown);
            root.RegisterCallback<FocusInEvent>(e => OnFocusIn(state, e), TrickleDown.TrickleDown);
            root.RegisterCallback<FocusOutEvent>(e => { if (e.relatedTarget == null) { state.Focused = null; UpdateRing(state); } }, TrickleDown.TrickleDown);
            root.RegisterCallback<DetachFromPanelEvent>(_ => Roots.Remove(root));
            root.schedule.Execute(() => UpdateRing(state)).Every(100);
        }

        /// <summary>True once a navigation key was used in this document (until the next click).</summary>
        public static bool KeyboardActive(VisualElement inDocument)
        {
            var state = StateOf(inDocument);
            return state != null && state.Root.ClassListContains(KeysClass);
        }

        /// <summary>Shows the ring from now on, as a navigation key does (tests and scripted tours).</summary>
        public static void UseKeyboard(VisualElement inDocument)
        {
            var state = StateOf(inDocument);
            if (state != null) SetKeys(state, true);
        }

        /// <summary>
        /// Opens a modal: focus moves to <paramref name="first"/> (or the modal's default control) and stays inside
        /// it until <see cref="CloseModal"/>, which gives focus back to whatever had it before.
        /// </summary>
        public static void OpenModal(VisualElement modal, Focusable first = null)
        {
            var state = StateOf(modal);
            if (state != null && state.Modals.FindIndex(m => m.Modal == modal) < 0) state.Modals.Add((modal, state.Focused));
            FocusSoon(modal, first);
        }

        /// <summary>
        /// Focuses <paramref name="first"/> (or the scope's default control) once a screen that was just shown has
        /// its style: a hidden element can't take focus, and showing one takes effect on the next layout.
        /// </summary>
        public static void FocusSoon(VisualElement scope, Focusable first = null)
        {
            if (scope == null) return;
            // Tries each frame until a control takes focus (at most about half a second): a screen shown this
            // frame has no resolved style yet, and on the first frames after a scene loads the panel may not either.
            bool done = false;
            int tries = 0;
            scope.schedule.Execute(() =>
            {
                var target = first ?? FirstIn(scope);
                if (target is VisualElement e && e.panel != null && IsShown(e) && e.canGrabFocus)
                {
                    target.Focus();
                    done = e.panel.focusController?.focusedElement == target;
                }
            }).Every(16).Until(() => done || ++tries > 30);
        }

        /// <summary>Closes a modal opened with <see cref="OpenModal"/> and restores the focus it took.</summary>
        public static void CloseModal(VisualElement modal)
        {
            var state = StateOf(modal);
            if (state == null) return;
            int i = state.Modals.FindIndex(m => m.Modal == modal);
            if (i < 0) return;
            var before = state.Modals[i].Before;
            state.Modals.RemoveAt(i);
            bool focusInside = state.Focused != null && modal.Contains(state.Focused);
            if (!focusInside && state.Focused != null) return;   // focus already moved on
            if (before is VisualElement b && b.panel != null && IsShown(b) && b.canGrabFocus) b.Focus();
            else if (state.Focused != null) ((Focusable)state.Focused).Blur();
        }

        /// <summary>The control that should take focus when a screen opens: one marked ui-default-focus, else the first shown.</summary>
        public static Focusable FirstIn(VisualElement scope)
        {
            if (scope == null) return null;
            VisualElement first = null, marked = null;
            scope.Query<VisualElement>().ForEach(e =>
            {
                if (marked != null || !CanTake(e)) return;
                if (e.ClassListContains(DefaultFocusClass)) marked = e;
                else if (first == null) first = e;
            });
            return marked ?? first;
        }

        /// <summary>True when an element and all its ancestors are displayed and visible.</summary>
        public static bool IsShown(VisualElement e)
        {
            for (var x = e; x != null; x = x.hierarchy.parent)
                if (x.resolvedStyle.display == DisplayStyle.None || !x.visible) return false;
            return true;
        }

        static bool CanTake(VisualElement e) =>
            e.focusable && e.canGrabFocus && e.enabledInHierarchy && e.tabIndex >= 0 && e.pickingMode == PickingMode.Position && IsShown(e);

        static RootState StateOf(VisualElement e)
        {
            for (var x = e; x != null; x = x.hierarchy.parent)
                if (Roots.TryGetValue(x, out var state)) return state;
            return null;
        }

        static void SetKeys(RootState state, bool on)
        {
            if (state.Root.ClassListContains(KeysClass) == on) return;
            state.Root.EnableInClassList(KeysClass, on);
            UpdateRing(state);
        }

        static void OnFocusIn(RootState state, FocusInEvent e)
        {
            var target = e.target as VisualElement;
            // A modal keeps focus inside it: anything else (Tab past its last control) goes back to its first.
            if (state.Modals.Count > 0 && target != null)
            {
                var modal = state.Modals[state.Modals.Count - 1].Modal;
                if (!modal.Contains(target) && IsShown(modal))
                {
                    var first = FirstIn(modal);
                    if (first != null) modal.schedule.Execute(() => first.Focus());
                }
            }
            state.Focused = target;
            UpdateRing(state);
        }

        static void UpdateRing(RootState state)
        {
            var f = state.Focused;
            bool show = f != null && f.panel != null && state.Root.ClassListContains(KeysClass) && IsShown(f);
            Rect r = default;
            if (show)
            {
                var bound = f.worldBound;
                var origin = state.Root.worldBound.position;
                r = new Rect(bound.x - origin.x - RingOutset, bound.y - origin.y - RingOutset, bound.width + 2 * RingOutset, bound.height + 2 * RingOutset);
                show = bound.width > 0 && bound.height > 0 && !float.IsNaN(bound.x);
            }
            state.Ring.EnableInClassList(RingOnClass, show);
            if (!show || r == state.Shown) return;
            state.Shown = r;
            state.Ring.style.left = r.x;
            state.Ring.style.top = r.y;
            state.Ring.style.width = r.width;
            state.Ring.style.height = r.height;
            state.Ring.BringToFront();
        }

        static void OnMove(RootState state, NavigationMoveEvent e)
        {
            SetKeys(state, true);
            var current = state.Focused != null && state.Focused.panel != null && IsShown(state.Focused) ? state.Focused : null;
            var d = e.direction;
            if (d == NavigationMoveEvent.Direction.None || d == NavigationMoveEvent.Direction.Next || d == NavigationMoveEvent.Direction.Previous) return;
            bool across = d == NavigationMoveEvent.Direction.Left || d == NavigationMoveEvent.Direction.Right;
            if (current != null && OwnsArrows(current, across)) return;

            var scope = state.Modals.Count > 0 ? state.Modals[state.Modals.Count - 1].Modal : state.Root;
            var next = current == null ? FirstIn(scope) as VisualElement : Nearest(scope, current, d);
            e.StopPropagation();
            current?.focusController?.IgnoreEvent(e);
            if (next != null) next.Focus();
        }

        /// <summary>Text fields and sliders keep left and right; ui-own-arrows controls keep all four.</summary>
        static bool OwnsArrows(VisualElement e, bool across)
        {
            for (var x = e; x != null; x = x.hierarchy.parent)
            {
                if (x.ClassListContains(OwnArrowsClass)) return true;
                if (across && (x is TextField || x is BaseSlider<float> || x is BaseSlider<int>)) return true;
            }
            return false;
        }

        /// <summary>The nearest control ahead in a direction: distance along it plus twice the sideways offset.</summary>
        static VisualElement Nearest(VisualElement scope, VisualElement from, NavigationMoveEvent.Direction d)
        {
            var a = from.worldBound;
            var dir = d switch
            {
                NavigationMoveEvent.Direction.Up => Vector2.down,     // panel y grows downwards
                NavigationMoveEvent.Direction.Down => Vector2.up,
                NavigationMoveEvent.Direction.Left => Vector2.left,
                _ => Vector2.right,
            };
            Candidates.Clear();
            // Ancestors are left out (a row isn't "below" its own button); a row's own buttons are in (Right reaches them).
            scope.Query<VisualElement>().ForEach(e => { if (e != from && CanTake(e) && !e.Contains(from)) Candidates.Add(e); });
            VisualElement best = null;
            float bestScore = float.MaxValue;
            foreach (var c in Candidates)
            {
                var b = c.worldBound;
                // The gap between the two boxes along the direction (edges, not centres, so a wide row and a narrow
                // button compare fairly); where they overlap on that axis, how far the centre moves instead.
                float along = dir.x > 0 ? b.xMin - a.xMax : dir.x < 0 ? a.xMin - b.xMax : dir.y > 0 ? b.yMin - a.yMax : a.yMin - b.yMax;
                if (along < -2)
                {
                    float centre = Vector2.Dot(b.center - a.center, dir);
                    if (centre < 4) continue;   // not ahead
                    along = 0;
                }
                float side = dir.x != 0
                    ? Mathf.Max(0, Mathf.Max(b.yMin - a.yMax, a.yMin - b.yMax))
                    : Mathf.Max(0, Mathf.Max(b.xMin - a.xMax, a.xMin - b.xMax));
                float centreSide = dir.x != 0 ? Mathf.Abs(b.center.y - a.center.y) : Mathf.Abs(b.center.x - a.center.x);
                float score = Mathf.Max(0, along) + 2 * side + 0.1f * centreSide;
                if (score < bestScore) { bestScore = score; best = c; }
            }
            Candidates.Clear();
            return best;
        }
    }
}
