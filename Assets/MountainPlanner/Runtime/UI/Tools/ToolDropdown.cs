using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// A dropdown in a tool window (task 09; the mockup's .ldd: "every choice is a dropdown", owner 2026-10-19): a button
    /// showing the choice with a caret, and under it a small menu of the options with a tick on the chosen one; a greyed
    /// option ("Chairworks (comes later)") can't be picked. The menu opens over the window (never pushing it), closes on a
    /// pick, a click elsewhere or Esc, and every option is a button the keyboard reaches. Quiet dropdowns are the Towers
    /// row's plainer pair. The options can change after it's made (the Chair row follows the Type).
    /// </summary>
    public sealed class ToolDropdown
    {
        public readonly Button Button;
        readonly Label _caption;
        readonly VisualElement _host;
        readonly Action<int> _picked;
        readonly string _name;
        VisualElement _menu;
        string[] _options = Array.Empty<string>();
        bool[] _disabled;
        int _chosen;

        internal ToolDropdown(string name, VisualElement host, bool quiet, Action<int> picked)
        {
            _name = name;
            _host = host;
            _picked = picked;
            Button = new Button(Toggle) { name = "toolwin-dd-" + name };
            Button.AddToClassList("tw-dd");
            if (quiet) Button.AddToClassList("tw-dd--quiet");
            _caption = new Label { name = "toolwin-dd-" + name + "-value", pickingMode = PickingMode.Ignore };
            _caption.AddToClassList("tw-dd__value");
            Button.Add(_caption);
            var caret = new Label("▾") { pickingMode = PickingMode.Ignore };
            caret.AddToClassList("tw-dd__caret");
            Button.Add(caret);
        }

        public bool Open => _menu != null;
        public int Chosen => _chosen;
        public string Caption => _caption.text;

        /// <summary>The options (and which are greyed), the chosen one, and the caption (the chosen option's when null).</summary>
        public void Set(string[] options, int chosen, bool[] disabled = null, string caption = null)
        {
            _options = options ?? Array.Empty<string>();
            _disabled = disabled;
            _chosen = chosen;
            _caption.text = caption ?? (chosen >= 0 && chosen < _options.Length ? _options[chosen] : "");
            if (_menu != null) { Close(); Toggle(); }
        }

        /// <summary>Opens the menu as a click on the button would (the keyboard's Enter, tests).</summary>
        public void Show() { if (_menu == null) Toggle(); }

        void Toggle()
        {
            if (_menu != null) { Close(); return; }
            Opened?.Invoke(this);
            _menu = new VisualElement { name = "toolwin-dd-" + _name + "-menu" };
            _menu.AddToClassList("tw-menu");
            if (Button.ClassListContains("tw-dd--quiet")) _menu.AddToClassList("tw-menu--quiet");
            Button first = null;
            for (int i = 0; i < _options.Length; i++)
            {
                int at = i;
                bool off = _disabled != null && i < _disabled.Length && _disabled[i];
                var item = new Button(() => { if (!off) Pick(at); }) { name = $"toolwin-dd-{_name}-{i}" };
                item.AddToClassList("tw-menu__item");
                if (off) { item.AddToClassList("tw-menu__item--dis"); item.focusable = false; }
                var tick = new Label(i == _chosen ? "✓" : "") { pickingMode = PickingMode.Ignore };
                tick.AddToClassList("tw-menu__tick");
                item.Add(tick);
                var text = new Label(_options[i]) { pickingMode = PickingMode.Ignore };
                text.AddToClassList("tw-menu__label");
                item.Add(text);
                item.RegisterCallback<KeyDownEvent>(e => { if (e.keyCode == KeyCode.Escape) { Close(); Button.Focus(); e.StopPropagation(); } });
                _menu.Add(item);
                if (!off && (first == null || i == _chosen)) first = item;
            }
            // Over the window, just under the button, as wide as it (a quiet one as wide as it needs).
            var at0 = _host.WorldToLocal(new Vector2(Button.worldBound.xMin, Button.worldBound.yMax));
            float width = Button.worldBound.width;
            _menu.style.left = float.IsNaN(at0.x) ? 0 : at0.x;
            _menu.style.top = float.IsNaN(at0.y) ? 0 : at0.y + 3;
            if (!float.IsNaN(width)) _menu.style.minWidth = width;
            _host.Add(_menu);
            _host.panel?.visualTree.RegisterCallback<PointerDownEvent>(Outside, TrickleDown.TrickleDown);
            if (Button.focusController?.focusedElement == Button) first?.Focus();
        }

        void Outside(PointerDownEvent e)
        {
            if (_menu == null) return;
            if (e.target is VisualElement v && (_menu.Contains(v) || Button.Contains(v) || v == _menu || v == Button)) return;
            Close();
        }

        void Pick(int i)
        {
            Close();
            Button.Focus();
            _picked?.Invoke(i);
        }

        /// <summary>Closes the menu; true if it was open.</summary>
        public bool Close()
        {
            if (_menu == null) return false;
            _host.panel?.visualTree.UnregisterCallback<PointerDownEvent>(Outside, TrickleDown.TrickleDown);
            _menu.RemoveFromHierarchy();
            _menu = null;
            return true;
        }

        /// <summary>Picks an option as a click on it would (tests and captures).</summary>
        public void Choose(int i)
        {
            if (i < 0 || i >= _options.Length || (_disabled != null && i < _disabled.Length && _disabled[i])) return;
            Close();
            _picked?.Invoke(i);
        }

        internal event Action<ToolDropdown> Opened;
        internal static void CloseAll(List<ToolDropdown> all) { foreach (var d in all) d.Close(); }
    }
}
