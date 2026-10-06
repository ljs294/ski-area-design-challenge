using System.Collections.Generic;
using MountainPlanner.UI.Hud;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// See-through panels (owner's experiment, 2026-10-06, task P2-02): each panel's colour is drawn on a plate behind its
    /// contents at <see cref="HudPreferences.PanelOpacity"/>, so the mountain shows through while words, symbols and
    /// hairlines stay solid. Hover and "on" colours are drawn on the plate too. UI Toolkit blends in linear light: the
    /// same opacity shows more of the map than it would in a browser.
    /// </summary>
    public sealed partial class MountainHud
    {
        readonly List<VisualElement> _plates = new List<VisualElement>();

        void WireGlass()
        {
            foreach (string name in new[] { "bar", "tr-sketch", "tr-layers", "tr-menu", "menu", "layers", "legend", "toolbox", "analysis", "rstats" })
                AddPlate(_root.Q(name));
            foreach (var item in _toastItems) AddPlate(item);
            HudPreferences.Changed += ApplyOpacity;
            ApplyOpacity();
        }

        void AddPlate(VisualElement panel)
        {
            if (panel == null) return;
            var plate = new VisualElement { pickingMode = PickingMode.Ignore };
            plate.AddToClassList("hud-plate");
            panel.AddToClassList("glassy");
            panel.Insert(0, plate);
            // The plate takes its panel's rounded corners, inside the hairline.
            panel.RegisterCallback<GeometryChangedEvent>(_ =>
            {
                var s = panel.resolvedStyle;
                plate.style.borderTopLeftRadius = Mathf.Max(0, s.borderTopLeftRadius - s.borderLeftWidth);
                plate.style.borderTopRightRadius = Mathf.Max(0, s.borderTopRightRadius - s.borderRightWidth);
                plate.style.borderBottomLeftRadius = Mathf.Max(0, s.borderBottomLeftRadius - s.borderLeftWidth);
                plate.style.borderBottomRightRadius = Mathf.Max(0, s.borderBottomRightRadius - s.borderRightWidth);
            });
            plate.style.opacity = HudPreferences.PanelOpacity / 100f;
            _plates.Add(plate);
        }

        void ApplyOpacity()
        {
            float opacity = HudPreferences.PanelOpacity / 100f;
            foreach (var plate in _plates) plate.style.opacity = opacity;
            // See-through, a panel reads lighter over snow than its solid colour: the bar's rules take the stronger hairline.
            _root.Q("hud").EnableInClassList("hud--glass", opacity < 0.999f);
        }
    }
}
