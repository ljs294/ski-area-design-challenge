using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Hud
{
    /// <summary>
    /// A floating window (task P2-02, the mockup's .pn): a solid panel with a 38 px head that drags it, kept on the
    /// screen (its left edge 0 to the width less 120, its top 0 to the height less 60), one at a time. The tools of
    /// Phase 3 open their panels in it; Phase 2 has none yet.
    /// </summary>
    public sealed class HudWindow : VisualElement
    {
        static HudWindow _open;

        public readonly VisualElement Head, Body;
        public readonly Label Title;
        Vector2 _grab;
        bool _dragging;

        public HudWindow(string title, float width = 344)
        {
            name = "window";
            AddToClassList("mp-win");
            AddToClassList("pn");
            style.width = width;
            style.left = 10;
            style.top = 10;
            Head = new VisualElement { name = "window-head" };
            Head.AddToClassList("mp-head");
            Title = new Label(title) { pickingMode = PickingMode.Ignore };
            Title.AddToClassList("mp-title");
            Head.Add(Title);
            var spacer = new VisualElement { pickingMode = PickingMode.Ignore };
            spacer.AddToClassList("mp-spacer");
            Head.Add(spacer);
            var x = new Button(RequestClose) { name = "window-x", text = "✕", tooltip = "Close (Esc)" };
            x.AddToClassList("mp-x");
            Head.Add(x);
            Add(Head);
            Body = new VisualElement { name = "window-body" };
            Add(Body);
            Head.RegisterCallback<PointerDownEvent>(OnDown);
            Head.RegisterCallback<PointerMoveEvent>(OnMove);
            Head.RegisterCallback<PointerUpEvent>(OnUp);
        }

        /// <summary>Opens <paramref name="window"/> in <paramref name="layer"/>, closing any other.</summary>
        public static void Open(VisualElement layer, HudWindow window)
        {
            if (_open != null && _open != window) _open.Close();
            _open = window;
            layer.Add(window);
        }

        public static HudWindow Current => _open;

        /// <summary>
        /// Asked before ✕ or Esc closes the window (P3-06): a tool with drawn work answers false the first time and asks
        /// "Discard it?" (CF-06, CF-07). Null closes at once.
        /// </summary>
        public Func<bool> CloseRequested;

        /// <summary>Raised once the window is gone, however it closed (another window opened, ✕, Esc).</summary>
        public event Action Closed;

        /// <summary>✕ and Esc: closes unless <see cref="CloseRequested"/> holds it open.</summary>
        public void RequestClose()
        {
            if (CloseRequested != null && !CloseRequested()) return;
            Close();
        }

        public void Close()
        {
            if (_open == this) _open = null;
            bool was = hierarchy.parent != null;
            RemoveFromHierarchy();
            if (was) Closed?.Invoke();
        }

        /// <summary>Moves the window to a stage position, kept on the screen.</summary>
        public void MoveTo(Vector2 position)
        {
            var bounds = hierarchy.parent?.contentRect ?? new Rect(0, 0, 1280, 720);
            style.left = Mathf.Clamp(position.x, 0, Mathf.Max(0, bounds.width - 120));
            style.top = Mathf.Clamp(position.y, 0, Mathf.Max(0, bounds.height - 60));
        }

        public Vector2 Position => new Vector2(resolvedStyle.left, resolvedStyle.top);

        /// <summary>The player dragged it: automatic placement leaves it where it was put (P3-06).</summary>
        public bool MovedByHand { get; private set; }

        void OnDown(PointerDownEvent e)
        {
            if (e.button != 0 || e.target is Button || OwnsItsClicks(e.target as VisualElement)) return;
            _dragging = true;
            _grab = (Vector2)e.position - Position;
            Head.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        /// <summary>
        /// Marks a head element that takes its own clicks (the tool window's renamable name): a press on it never starts a
        /// drag, so its ClickEvent arrives (task 4.26 BB13: capturing the pointer for the drag swallowed the click, so the
        /// New trail and Edit trail names could not be renamed).
        /// </summary>
        public const string NoDragClass = "mp-head--nodrag";

        /// <summary>Whether <paramref name="t"/> is, or sits inside, a text field or a <see cref="NoDragClass"/> element.</summary>
        bool OwnsItsClicks(VisualElement t)
        {
            for (; t != null && t != Head; t = t.parent)
                if (t is TextField || t.ClassListContains(NoDragClass) || t.ClassListContains(InlineName.FieldClass)) return true;
            return false;
        }

        void OnMove(PointerMoveEvent e)
        {
            if (!_dragging || !Head.HasPointerCapture(e.pointerId)) return;
            MovedByHand = true;
            MoveTo((Vector2)e.position - _grab);
        }

        void OnUp(PointerUpEvent e)
        {
            if (!_dragging) return;
            _dragging = false;
            Head.ReleasePointer(e.pointerId);
        }
    }
}
