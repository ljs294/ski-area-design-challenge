using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Hud
{
    /// <summary>
    /// One of the mockup's symbols (task P2-02): its layers (<see cref="HudIconLayers"/>, vector images made from the
    /// mockup by tools/ui-parity/extract-icons.mjs) stacked to fill the element, with a tool badge (new, edit, remove)
    /// over them. As in the mockup, drawn layers take the element's text colour (USS <c>color</c>, the mockup's
    /// currentColor) and knock-outs the colour behind the icon (the mockup's --knock: the nearest background).
    /// Colours follow state changes through <see cref="RetintAll"/>, which the HUD calls after anything that restyles
    /// (hover, a theme or state change); it only writes a tint that changed, and allocates nothing.
    /// </summary>
    [UxmlElement]
    public partial class HudIcon : VisualElement
    {
        static readonly Dictionary<string, VectorImage> Images = new Dictionary<string, VectorImage>();
        static readonly List<HudIcon> Live = new List<HudIcon>();

        readonly List<VisualElement> _drawn = new List<VisualElement>(4), _knocks = new List<VisualElement>(4);
        string _icon, _badge;
        Color _tint = Color.clear, _knock = Color.clear;

        [UxmlAttribute]
        public string Icon
        {
            get => _icon;
            set { _icon = value; Build(); }
        }

        /// <summary>A tool badge over the symbol: new, edit or remove (the mockup's b-* symbols).</summary>
        [UxmlAttribute]
        public string Badge
        {
            get => _badge;
            set { _badge = value; Build(); }
        }

        public HudIcon()
        {
            pickingMode = PickingMode.Ignore;
            AddToClassList("hud-icon");
            RegisterCallback<AttachToPanelEvent>(_ => { Live.Add(this); schedule.Execute(Retint); });
            RegisterCallback<DetachFromPanelEvent>(_ => Live.Remove(this));
            RegisterCallback<CustomStyleResolvedEvent>(_ => Retint());
        }

        public HudIcon(string icon, string badge = null) : this()
        {
            _badge = badge;
            Icon = icon;
        }

        void Build()
        {
            Clear();
            _drawn.Clear();
            _knocks.Clear();
            AddLayers(_icon);
            if (!string.IsNullOrEmpty(_badge)) AddLayers("b-" + _badge);
            _tint = _knock = Color.clear;
            Retint();
        }

        void AddLayers(string symbol)
        {
            if (string.IsNullOrEmpty(symbol) || !HudIconLayers.All.TryGetValue(symbol, out var layers)) return;
            foreach (var layer in layers)
            {
                var image = Image(layer.Image);
                if (image == null) continue;
                var e = new VisualElement { pickingMode = PickingMode.Ignore };
                e.AddToClassList(layer.Knock ? "hud-icon__knock" : "hud-icon__drawn");
                e.style.position = Position.Absolute;
                e.style.left = e.style.top = e.style.right = e.style.bottom = 0;
                e.style.backgroundImage = Background.FromVectorImage(image);
                e.style.backgroundSize = new BackgroundSize(BackgroundSizeType.Contain);
                Add(e);
                (layer.Knock ? _knocks : _drawn).Add(e);
            }
        }

        static VectorImage Image(string name)
        {
            if (!Images.TryGetValue(name, out var image))
            {
                image = Resources.Load<VectorImage>("HudIcons/" + name);
                if (image == null) Debug.LogWarning($"[HudIcon] no vector image HudIcons/{name}");
                Images[name] = image;
            }
            return image;
        }

        /// <summary>Takes the text colour and the colour behind, if either changed.</summary>
        public void Retint()
        {
            if (panel == null) return;
            var tint = resolvedStyle.color;
            var knock = Behind();
            if (tint != _tint)
            {
                _tint = tint;
                foreach (var e in _drawn) e.style.unityBackgroundImageTintColor = tint;
            }
            if (knock != _knock)
            {
                _knock = knock;
                foreach (var e in _knocks) e.style.unityBackgroundImageTintColor = knock;
            }
        }

        Color Behind()
        {
            for (var x = hierarchy.parent; x != null; x = x.hierarchy.parent)
            {
                var c = x.resolvedStyle.backgroundColor;
                if (c.a > 0.5f) return c;
            }
            return Color.clear;
        }

        /// <summary>Every icon on screen checks its colours (after hover, a theme change or a state change).</summary>
        public static void RetintAll()
        {
            for (int i = 0; i < Live.Count; i++) Live[i].Retint();
        }
    }
}
