using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The ultrawide rule (decision E6; task P2-01): a full-height column, centred, at most 16:9 of its parent.
    /// Full-screen panels and modals go inside it, so on a 21:9 or 32:9 screen they keep their 16:9 shape in the
    /// middle instead of stretching or drifting to the far edges; the HUD and docked cards stay on the screen's
    /// edges. On 16:9 and narrower screens it is simply the whole width. It resizes only when its parent does.
    /// </summary>
    [UxmlElement]
    public partial class StageColumn : VisualElement
    {
        public const float Aspect = 16f / 9f;

        VisualElement _watched;

        public StageColumn()
        {
            AddToClassList("ui-stage");
            pickingMode = PickingMode.Ignore;
            RegisterCallback<AttachToPanelEvent>(_ => Watch(hierarchy.parent));
            RegisterCallback<DetachFromPanelEvent>(_ => Watch(null));
        }

        /// <summary>The column's width for a parent of this size: the whole width, up to 16:9 of the height.</summary>
        public static float WidthFor(float parentWidth, float parentHeight) => Mathf.Min(parentWidth, parentHeight * Aspect);

        void Watch(VisualElement parent)
        {
            _watched?.UnregisterCallback<GeometryChangedEvent>(OnParentGeometry);
            _watched = parent;
            if (parent == null) return;
            parent.RegisterCallback<GeometryChangedEvent>(OnParentGeometry);
            Fit(parent.layout);
        }

        void OnParentGeometry(GeometryChangedEvent e) => Fit(e.newRect);

        void Fit(Rect parent)
        {
            if (float.IsNaN(parent.width) || float.IsNaN(parent.height)) return;
            float width = WidthFor(parent.width, parent.height);
            style.width = width;
            style.left = (parent.width - width) / 2;
        }
    }
}
