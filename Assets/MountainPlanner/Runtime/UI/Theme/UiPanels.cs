using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// Puts every UI Toolkit document on the shared look (task P2-01): the theme (<see cref="UiPreferences.Theme"/>,
    /// Theme-Dark.tss or Theme-Light.tss with Base.uss) and the UI scale, applied to a runtime copy of each
    /// document's panel settings so the assets in the project never change; plus the keyboard focus ring and
    /// arrow-key navigation (<see cref="UiFocus"/>).
    /// The panels scale with the screen height from a 1080p reference, so a 21:9 or 32:9 screen gets more
    /// width, never smaller type. The UI scale shrinks or grows that reference.
    /// </summary>
    public static class UiPanels
    {
        public const string ResourceFolder = "MountainPlannerUI/";
        public const int ReferenceWidth = 1920, ReferenceHeight = 1080;

        static ThemeStyleSheet _dark, _light;
        /// <summary>Each panel-settings asset's runtime copy (several documents on one asset share one copy, as they shared the asset).</summary>
        static readonly Dictionary<PanelSettings, PanelSettings> Copies = new Dictionary<PanelSettings, PanelSettings>();
        static readonly List<PanelSettings> Live = new List<PanelSettings>();
        static bool _listening;

        /// <summary>Every runtime panel settings in use (captures point them at a render texture).</summary>
        public static IReadOnlyList<PanelSettings> All => Live;

        /// <summary>The runtime copy of a panel-settings asset, themed and scaled (made once per asset).</summary>
        public static PanelSettings Runtime(PanelSettings asset)
        {
            if (asset == null) return null;
            if (Live.Contains(asset)) return asset;
            Live.RemoveAll(p => p == null);
            if (Copies.TryGetValue(asset, out var copy) && copy != null) return copy;
            copy = Object.Instantiate(asset);
            copy.name = asset.name + " (runtime)";
            Copies[asset] = copy;
            Live.Add(copy);
            Apply(copy);
            if (!_listening)
            {
                _listening = true;
                UiPreferences.Changed += ApplyAll;
            }
            return copy;
        }

        /// <summary>
        /// Moves a document onto its panel's runtime copy and adds the focus ring and keyboard navigation to its
        /// root. Safe to call more than once.
        /// </summary>
        public static void Adopt(UIDocument document)
        {
            if (document == null) return;
            var settings = Runtime(document.panelSettings);
            if (settings != null && document.panelSettings != settings) document.panelSettings = settings;
            if (document.rootVisualElement != null) UiFocus.Prepare(document.rootVisualElement);
        }

        public static ThemeStyleSheet ThemeSheet(UiTheme theme)
        {
            if (_dark == null) _dark = Resources.Load<ThemeStyleSheet>(ResourceFolder + "Theme-Dark");
            if (_light == null) _light = Resources.Load<ThemeStyleSheet>(ResourceFolder + "Theme-Light");
            return theme == UiTheme.Light ? _light : _dark;
        }

        /// <summary>The reference resolution for a UI scale: 150% lays the UI out on a 720-unit-high screen, 50% on 2160.</summary>
        public static Vector2Int Reference(int scalePercent) =>
            new Vector2Int(Mathf.RoundToInt(ReferenceWidth * 100f / scalePercent), Mathf.RoundToInt(ReferenceHeight * 100f / scalePercent));

        static void Apply(PanelSettings settings)
        {
            var sheet = ThemeSheet(UiPreferences.Theme);
            if (sheet != null && settings.themeStyleSheet != sheet) settings.themeStyleSheet = sheet;
            settings.scaleMode = PanelScaleMode.ScaleWithScreenSize;
            settings.screenMatchMode = PanelScreenMatchMode.MatchWidthOrHeight;
            settings.match = 1;   // by height: wider screens get more room, not smaller type
            var reference = Reference(UiPreferences.ScalePercent);
            if (settings.referenceResolution != reference) settings.referenceResolution = reference;
        }

        static void ApplyAll()
        {
            Live.RemoveAll(p => p == null);
            foreach (var settings in Live) Apply(settings);
        }
    }
}
