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
            var x = new Button(Close) { name = "window-x", text = "✕", tooltip = "Close (Esc)" };
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

        public void Close()
        {
            if (_open == this) _open = null;
            RemoveFromHierarchy();
        }

        /// <summary>Moves the window to a stage position, kept on the screen.</summary>
        public void MoveTo(Vector2 position)
        {
            var bounds = hierarchy.parent?.contentRect ?? new Rect(0, 0, 1280, 720);
            style.left = Mathf.Clamp(position.x, 0, Mathf.Max(0, bounds.width - 120));
            style.top = Mathf.Clamp(position.y, 0, Mathf.Max(0, bounds.height - 60));
        }

        public Vector2 Position => new Vector2(resolvedStyle.left, resolvedStyle.top);

        void OnDown(PointerDownEvent e)
        {
            if (e.button != 0 || e.target is Button) return;
            _dragging = true;
            _grab = (Vector2)e.position - Position;
            Head.CapturePointer(e.pointerId);
            e.StopPropagation();
        }

        void OnMove(PointerMoveEvent e)
        {
            if (!_dragging || !Head.HasPointerCapture(e.pointerId)) return;
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
