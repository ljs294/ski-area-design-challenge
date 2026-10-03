using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>Hit tests a screen point against a UI Toolkit document, so the camera can leave input over a panel alone.</summary>
    public static class PanelPointer
    {
        /// <summary>
        /// True when the pointer (screen pixels, origin bottom-left) is over something pickable in this document
        /// other than its root: a shown panel, button or modal backdrop. Hidden and picking-mode Ignore elements don't count.
        /// </summary>
        public static bool IsOver(VisualElement root, Vector2 screen)
        {
            if (root?.panel == null || root.resolvedStyle.display == DisplayStyle.None) return false;
            var p = RuntimePanelUtils.ScreenToPanel(root.panel, new Vector2(screen.x, Screen.height - screen.y));
            var picked = root.panel.Pick(p);
            return picked != null && picked != root && picked != root.panel.visualTree;
        }
    }
}
