using System.Collections.Generic;
using MountainPlanner.Presentation.Lifts;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// LD3 (owner, 2026-11-01: towers "clearly numbered"): T1…Tn over each tower of the lift being built or edited, the same
    /// numbers the profile strip, hints and problems use, the selected one strong. Drawn in the HUD's map-label layer, behind
    /// the panels; labels are pooled and only moved each frame (0.3 §8: nothing allocated per frame).
    /// </summary>
    public sealed class TowerNumberOverlay
    {
        const float Width = 48, Height = 20;
        const float Above = 4;   // metres over the head's pivot the number stands
        static readonly List<string> Names = new List<string>();

        readonly VisualElement _layer;
        readonly List<Label> _labels = new List<Label>();
        bool _shown = true;

        /// <summary>Adds its own full-screen layer just above <paramref name="contourLayer"/>.</summary>
        public TowerNumberOverlay(VisualElement contourLayer)
        {
            _layer = new VisualElement { name = "tower-numbers", pickingMode = PickingMode.Ignore };
            _layer.AddToClassList("contour-labels");
            var parent = contourLayer.parent;
            parent.Insert(parent.IndexOf(contourLayer) + 1, _layer);
        }

        static string Name(int i)
        {
            while (Names.Count <= i) Names.Add("T" + (Names.Count + 1));
            return Names[i];
        }

        /// <summary>
        /// Once a frame: numbers over <paramref name="view"/>'s towers (null or <paramref name="visible"/> false: hidden),
        /// <paramref name="selected"/> (a tower index, or anything else for none) strong.
        /// </summary>
        public void Update(Camera camera, LiftView view, int selected, bool visible)
        {
            int n = visible && camera != null && view != null ? view.TowerCount : 0;
            if (n == 0)
            {
                if (_shown) { _layer.style.display = DisplayStyle.None; _shown = false; }
                return;
            }
            if (!_shown) { _layer.style.display = DisplayStyle.Flex; _shown = true; }
            while (_labels.Count < n)
            {
                var label = new Label(Name(_labels.Count)) { pickingMode = PickingMode.Ignore, usageHints = UsageHints.DynamicTransform };
                label.AddToClassList("tower-number");
                _layer.Add(label);
                _labels.Add(label);
            }
            for (int i = 0; i < _labels.Count; i++)
            {
                var label = _labels[i];
                Vector3 at = default;
                bool on = i < n && view.TowerTop(i, out var top) && (at = camera.WorldToScreenPoint(top + Vector3.up * Above)).z > 0;
                if (!on)
                {
                    if (label.style.display != DisplayStyle.None) label.style.display = DisplayStyle.None;
                    continue;
                }
                var local = _layer.WorldToLocal(RuntimePanelUtils.ScreenToPanel(_layer.panel, new Vector2(at.x, Screen.height - at.y)));
                label.style.translate = new Translate(local.x - Width / 2, local.y - Height);
                label.EnableInClassList("tower-number--sel", i == selected);
                if (label.style.display != DisplayStyle.Flex) label.style.display = DisplayStyle.Flex;
            }
        }
    }
}
