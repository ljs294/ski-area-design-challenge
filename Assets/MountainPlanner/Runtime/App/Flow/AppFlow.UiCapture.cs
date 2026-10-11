using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Presentation;
using MountainPlanner.UI;
using MountainPlanner.UI.Flow;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -uicapture &lt;folder&gt; (task P2-01): every UI Toolkit screen at 1920×1080, 2560×1080, 3440×1440 and 5120×1440,
    /// in both themes at 100%, and at 50% and 150% at 1920×1080. Each picture is drawn off screen at its full size
    /// (the scene and each panel into their own render textures, composited in linear light as the GPU blends
    /// them), so a 32:9 picture comes out of any monitor. Alongside the pictures, report.txt lists every visible
    /// text or control that falls off its screen or is cut short, and every full-screen column or modal that isn't
    /// centred: an empty report is the "nothing clipped, stretched or off-centre" check.
    /// Options: -uionly name,name (a subset of the screens), -uisizes 1920x1080,... and -uicompare (also
    /// captures the real window once and reports how far the off-screen composite differs from it).
    /// It needs a library with a mountain in it (the title's demo); it downloads nothing.
    /// </summary>
    public sealed partial class AppFlow
    {
        static readonly Vector2Int[] DefaultSizes = { new(1920, 1080), new(2560, 1080), new(3440, 1440), new(5120, 1440) };

        string[] _uiOnly;
        Vector2Int[] _uiSizes = DefaultSizes;
        int _uiBase = 100;
        readonly StringBuilder _uiReport = new StringBuilder();
        int _uiShots, _uiProblems;

        void StartUiCaptureIfAsked(string[] args)
        {
            int i = Array.IndexOf(args, "-uicapture");
            if (i < 0 || i + 1 >= args.Length) return;
            int only = Array.IndexOf(args, "-uionly");
            if (only >= 0 && only + 1 < args.Length) _uiOnly = args[only + 1].Split(',');
            int sizes = Array.IndexOf(args, "-uisizes");
            if (sizes >= 0 && sizes + 1 < args.Length)
                _uiSizes = args[sizes + 1].Split(',').Select(s => s.Split('x')).Select(p => new Vector2Int(int.Parse(p[0]), int.Parse(p[1]))).ToArray();
            int scale = Array.IndexOf(args, "-uibase");   // -uibase 85: the main pictures at that UI scale instead of 100%
            if (scale >= 0 && scale + 1 < args.Length && int.TryParse(args[scale + 1], out int percent)) _uiBase = percent;
            StartCoroutine(UiCapture(Path.GetFullPath(args[i + 1]), Array.IndexOf(args, "-uicompare") >= 0));
        }

        bool Wanted(string screen) => _uiOnly == null || Array.IndexOf(_uiOnly, screen) >= 0;

        IEnumerator UiCapture(string folder, bool compare)
        {
            Directory.CreateDirectory(folder);
            var choice = UiPreferences.Choice;
            int scale = UiPreferences.ScalePercent;
            string demo = TitleBackground();
            _driftHeld = true;   // the title's drift holds at its first view, so every picture frames the same (task P2-03)
            _driftSeconds = 0;
            yield return WaitForMountain(300);
            yield return Wait(4);   // ground cover and trees paint in

            if (compare) yield return CompareWithWindow(folder);

            if (Wanted("s1-title")) yield return EachLook(folder, "s1-title");

            if (Wanted("s2-load-area") || Wanted("s2-manage-areas") || Wanted("s11-confirm") || Wanted("s2-rename") || Wanted("s2-free-space"))
            {
                _mode = LibraryMode.Load;
                Controller.MyResorts();
                yield return Wait(1.5f);   // the sizes are measured on a worker thread
                if (Wanted("s2-load-area")) yield return EachLook(folder, "s2-load-area");
                Controller.Escape();
                _mode = LibraryMode.Manage;
                Controller.MyResorts();
                yield return Wait(1.5f);
                if (Wanted("s2-manage-areas")) yield return EachLook(folder, "s2-manage-areas");
                var row = Screens.SelectedRow;
                if (Wanted("s11-confirm") && row?.Entry != null)
                {
                    Screens.ConfirmDelete(row);   // only asks; the capture never confirms
                    yield return Wait(0.3f);
                    yield return EachLook(folder, "s11-confirm");
                    Screens.CloseConfirm();
                }
                if (Wanted("s2-rename") && row != null && row.CanRename)
                {
                    Screens.OpenRename(row);   // task P2-04; closed without renaming
                    yield return Wait(0.3f);
                    yield return EachLook(folder, "s2-rename");
                    Screens.ClosePrompt();
                }
                if (Wanted("s2-free-space") && _library != null && _library.FreeableBytes > 0)
                {
                    Screens.ConfirmFreeSpace();   // only asks
                    yield return Wait(0.3f);
                    yield return EachLook(folder, "s2-free-space");
                    Screens.CloseConfirm();
                }
                Controller.Escape();
            }

            // S8: every page (task P2-05); the first keeps its old name. Nothing chosen here is remembered.
            string[] pages = { "s8-settings", "s8-settings-units", "s8-settings-graphics", "s8-settings-display", "s8-settings-controls", "s8-settings-data" };
            for (int page = 0; page < pages.Length; page++)
            {
                if (!Wanted(pages[page])) continue;
                Screens.Settings.Remember = false;
                Screens.ShowSettings();
                Screens.Settings.ShowPage(page);
                yield return Wait(page == SettingsWindow.DataPage ? 1.5f : 0.3f);   // the Data page measures the library first
                yield return EachLook(folder, pages[page]);
                Screens.CloseOverlay();
                Screens.Settings.Remember = true;
            }
            if (Wanted("s9-credits"))
            {
                Screens.ShowCredits(Credits());
                yield return Wait(0.3f);
                yield return EachLook(folder, "s9-credits");
                Screens.CloseOverlay();
            }

            if (Wanted("s4-download") || Wanted("s4-pill"))
            {
                // A made-up download, drawn by the real card: nothing is fetched.
                var stages = new[] { "Terrain", "Terrain surroundings", "Imagery", "Ground cover", "Roads", "Building" };
                var vm = new DownloadViewModel();
                vm.Start("Crystal Mountain", 2);
                vm.Apply(new DownloadStatus
                {
                    Name = "Crystal Mountain", SizeKm = 2, Stages = stages, StageIndex = 3, StageCount = stages.Length, Stage = stages[2],
                    Overall = 0.41, Bytes = 182_400_000, BytesPerSecond = 6_100_000, SecondsRemaining = 80,
                    Detail = "Imagery: downloading tile 3 of 6 · 35%",
                });
                Screens.RenderDownload(vm);
                Screens.ShowDownloadCard(true, true, false);
                yield return Wait(0.3f);
                if (Wanted("s4-download")) yield return EachLook(folder, "s4-download");
                Screens.ShowDownloadCard(false, true, false);
                yield return Wait(0.3f);
                if (Wanted("s4-pill")) yield return EachLook(folder, "s4-pill");
                Screens.ShowDownloadCard(false, false, false);
            }
            yield return CaptureDownloadStates(folder);   // task P2-06: S4's other states, S11's dialogs (AppFlow.UiCapture.Downloads.cs)

            if (Wanted("s5-quality") && demo != null)
            {
                Controller.DownloadFinished(demo);   // the card as it shows after a download, for the demo package
                yield return Wait(0.5f);
                yield return EachLook(folder, "s5-quality");
                Controller.QualityBackToLibrary();
                Controller.Escape();
            }

            if (Wanted("s3-picker") && Picker != null)
            {
                Controller.NewResort();
                yield return Wait(0.5f);
                Picker.PlaceAt(new MountainPlanner.Domain.Geo.GeoPoint(43.59, -110.83));
                yield return Wait(6);   // map tiles, coverage and the estimate (offline, the map says so)
                yield return EachLook(folder, "s3-picker");
                Controller.PickerCancelled();
            }

            if ((HudStates.Any(h => Wanted("s6-" + h.Name)) || Wanted("s7-menu") || Wanted("s8-settings-game")) && demo != null)
            {
                Controller.Open(demo);
                yield return Wait(1);
                yield return WaitForMountain(600);
                yield return Wait(4);
                var hud = _viewer != null ? _viewer.Hud : null;
                var units = MountainPlanner.Presentation.DisplayUnits.Current;
                MountainPlanner.Presentation.DisplayUnits.Set(MountainPlanner.Domain.Measure.UnitSystem.Imperial, remember: false);   // as the mockup
                // The HUD's states (task P2-02), each as the mockup's #demo=p2,<flags> shows it (tools/ui-parity/states.mjs),
                // with the pointer on the mountain, as the mockup's is (on the face with an info layer on).
                if (hud != null)
                    foreach (var state in HudStates)
                    {
                        if (!Wanted("s6-" + state.Name)) continue;
                        _viewer.SetPointerForCapture(state.Info ? new Vector2(742 / 1280f, 1 - 440 / 720f) : new Vector2(640 / 1280f, 1 - 500 / 720f));
                        state.Set(hud, _viewer, true);
                        yield return Wait(0.5f);
                        yield return EachLook(folder, "s6-" + state.Name);
                        state.Set(hud, _viewer, false);
                        yield return Wait(0.3f);
                    }
                _viewer?.SetPointerForCapture(null);
                MountainPlanner.Presentation.DisplayUnits.Set(units, remember: false);
                if (hud != null && Wanted("s7-menu"))
                {
                    hud.ToggleMenu();
                    yield return Wait(0.3f);
                    yield return EachLook(folder, "s7-menu");
                    hud.ToggleMenu();
                }
                if (Wanted("s8-settings-game"))
                {
                    Screens.ShowSettings();
                    yield return Wait(0.3f);
                    yield return EachLook(folder, "s8-settings-game");
                    Screens.CloseOverlay();
                }
            }

            UiPreferences.SetChoice(choice, remember: false);
            UiPreferences.SetScale(scale, remember: false);
            _uiReport.Insert(0, $"UI capture: {_uiShots} pictures, {_uiProblems} layout problems.\n");
            File.WriteAllText(Path.Combine(folder, "report.txt"), _uiReport.ToString());
            Debug.Log($"[AppFlow] UI capture finished: {_uiShots} pictures, {_uiProblems} layout problems, {folder}");
            Quit();
        }

        /// <summary>
        /// The HUD states the capture walks (task P2-02): the names of tools/ui-parity/states.mjs, each set up as the mockup's
        /// flags set it (on: true) and put back (on: false). Info marks the states whose pointer reads an info layer.
        /// </summary>
        static readonly (string Name, bool Info, Action<MountainHud, MountainViewer, bool> Set)[] HudStates =
        {
            ("hud", false, (h, v, on) => { }),
            ("float", false, (h, v, on) => MountainPlanner.UI.Hud.HudPreferences.SetDocked(!on, remember: false)),
            ("menu", false, (h, v, on) => { if (h.MenuOpen != on) h.ToggleMenu(); }),
            ("layers", false, (h, v, on) => { if (h.LayersOpen != on) h.ToggleLayers(); }),
            ("slope", true, (h, v, on) => v.SetLayerForCapture(MapLayers.SlopeAngle, on)),
            ("exposure", true, (h, v, on) => v.SetLayerForCapture(MapLayers.Exposure, on)),
            ("depth", true, (h, v, on) => v.SetLayerForCapture(MapLayers.SnowDepth, on)),
            ("contours", false, (h, v, on) => v.SetLayerForCapture(MapLayers.Contours, on)),
            ("slope-contours", true, (h, v, on) =>
            {
                v.SetLayerForCapture(MapLayers.SlopeAngle, on);
                v.SetLayerForCapture(MapLayers.Contours, on);
                if (h.LayersOpen != on) h.ToggleLayers();
            }),
            ("tray-lifts", false, (h, v, on) => { h.SetToolboxTab("lifts"); h.SetToolbox(on); }),
            ("tray-trails", false, (h, v, on) => { h.SetToolboxTab(on ? "trails" : "lifts"); h.SetToolbox(on); }),
            ("tray-snow", false, (h, v, on) => { h.SetToolboxTab(on ? "snow" : "lifts"); h.SetToolbox(on); }),
            ("tray-infra", false, (h, v, on) => { h.SetToolboxTab(on ? "infra" : "lifts"); h.SetToolbox(on); }),
            ("analysis", false, (h, v, on) => { h.SetAnalysisTab("overview"); h.SetAnalysis(on); }),
            ("analysis-lifts", false, (h, v, on) => { h.SetAnalysisTab(on ? "lifts" : "overview"); h.SetAnalysis(on); }),
            ("analysis-weather", false, (h, v, on) => { h.SetAnalysisTab(on ? "weather" : "overview"); h.SetAnalysis(on); }),
            ("analysis-finances", false, (h, v, on) => { h.SetAnalysisTab(on ? "finances" : "overview"); h.SetAnalysis(on); }),
            ("rstats", false, (h, v, on) => h.SetStats(on)),
            // Task P2-06: a background download in the bar, as the mockup's demo=dl, dlwait and dlfail.
            ("dl", false, (h, v, on) => h.SetDownload(on ? "Downloading · 41%" : null, "Crystal Mountain", 0.41f, "")),
            ("dlwait", false, (h, v, on) => h.SetDownload(on ? "Waiting for connection · 41%" : null, "Crystal Mountain", 0.41f, "pill--waiting")),
            ("dlfail", false, (h, v, on) => h.SetDownload(on ? "Download stopped · 41%" : null, "Crystal Mountain", 0.41f, "pill--failed")),
        };

        /// <summary>One screen in both themes at every size (100%), then at 50% and 150% at 1920×1080.</summary>
        IEnumerator EachLook(string folder, string screen)
        {
            foreach (var theme in new[] { UiThemeChoice.Dark, UiThemeChoice.Light })
            {
                UiPreferences.SetChoice(theme, remember: false);
                UiPreferences.SetScale(_uiBase, remember: false);
                string t = theme.ToString().ToLowerInvariant();
                foreach (var size in _uiSizes) yield return OffscreenShot(folder, $"{screen}_{t}_{size.x}x{size.y}", size.x, size.y);
                foreach (int scale in new[] { 50, 150 })
                {
                    UiPreferences.SetScale(scale, remember: false);
                    yield return OffscreenShot(folder, $"{screen}_{t}_1920x1080_{scale}", 1920, 1080);
                }
                UiPreferences.SetScale(100, remember: false);
            }
        }

        /// <summary>The scene and every panel drawn at w×h off screen, composited, saved, and checked for layout problems.</summary>
        IEnumerator OffscreenShot(string folder, string name, int w, int h, Action<Color32[]> sink = null)
        {
            var cam = UnityEngine.Camera.main;
            var panels = UiPanels.All.Where(p => p != null).OrderBy(p => p.sortingOrder).ToList();
            var scene = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB);
            var layers = panels.Select(_ => new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32, RenderTextureReadWrite.sRGB)).ToList();
            if (cam != null) cam.targetTexture = scene;
            for (int i = 0; i < panels.Count; i++)
            {
                panels[i].targetTexture = layers[i];
                panels[i].clearColor = true;
                panels[i].colorClearValue = Color.clear;
            }
            for (int f = 0; f < 4; f++) yield return null;   // layout at the new size (the 16:9 columns refit on the next frame)
            yield return new WaitForEndOfFrame();

            var pixels = Read(scene, w, h);
            foreach (var layer in layers) Over(Read(layer, w, h), pixels);
            CheckLayout(name);
            if (name.EndsWith("_1920x1080", StringComparison.Ordinal)) WriteLayout(folder, name);

            if (cam != null) cam.targetTexture = null;
            foreach (var p in panels)
            {
                p.targetTexture = null;
                p.clearColor = false;
            }
            scene.Release();
            foreach (var layer in layers) layer.Release();

            if (sink != null) sink(pixels);
            else
            {
                var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
                tex.SetPixels32(pixels);
                File.WriteAllBytes(Path.Combine(folder, name + ".png"), tex.EncodeToPNG());
                Destroy(tex);
                _uiShots++;
            }
            yield return null;
        }

        static Color32[] Read(RenderTexture rt, int w, int h)
        {
            var was = RenderTexture.active;
            RenderTexture.active = rt;
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.ReadPixels(new Rect(0, 0, w, h), 0, 0, false);
            tex.Apply(false);
            RenderTexture.active = was;
            var pixels = tex.GetPixels32();
            Destroy(tex);
            return pixels;
        }

        static float[] _toLinear;
        static byte[] _toSrgb;

        /// <summary>
        /// A panel layer over the picture so far, in linear light as the GPU blends: the layer holds premultiplied
        /// colour (UI Toolkit drew it over a cleared, transparent target), stored sRGB-encoded.
        /// </summary>
        static void Over(Color32[] layer, Color32[] under)
        {
            if (_toLinear == null)
            {
                _toLinear = new float[256];
                for (int i = 0; i < 256; i++) _toLinear[i] = Mathf.GammaToLinearSpace(i / 255f);
                _toSrgb = new byte[4096];
                for (int i = 0; i < 4096; i++) _toSrgb[i] = (byte)Mathf.RoundToInt(Mathf.LinearToGammaSpace(i / 4095f) * 255);
            }
            for (int i = 0; i < under.Length; i++)
            {
                var top = layer[i];
                if (top.a == 0) continue;
                var bottom = under[i];
                float keep = 1 - top.a / 255f;
                under[i] = new Color32(Mix(top.r, bottom.r, keep), Mix(top.g, bottom.g, keep), Mix(top.b, bottom.b, keep), 255);
            }
        }

        static byte Mix(byte top, byte bottom, float keep) =>
            _toSrgb[Mathf.Clamp(Mathf.RoundToInt((_toLinear[top] + _toLinear[bottom] * keep) * 4095), 0, 4095)];

        /// <summary>
        /// Every visible text and control inside its screen and not cut short; every 16:9 column at the centre and
        /// 16:9 wide; every modal's panel centred in its column.
        /// </summary>
        void CheckLayout(string shot)
        {
            foreach (var document in FindObjectsByType<UIDocument>(FindObjectsSortMode.None))
            {
                var root = document.rootVisualElement;
                if (root == null || root.panel == null || !document.isActiveAndEnabled) continue;
                var screen = root.worldBound;
                root.Query<VisualElement>().ForEach(e =>
                {
                    if (e == root || !UiFocus.IsShown(e) || e.resolvedStyle.opacity <= 0.01f) return;
                    var b = e.worldBound;
                    if (b.width < 1 || b.height < 1 || float.IsNaN(b.x)) return;
                    bool control = e.focusable && e.pickingMode == PickingMode.Position && e.canGrabFocus;
                    var text = e as TextElement;
                    bool hasText = text != null && !string.IsNullOrEmpty(text.text);
                    if (!control && !hasText) return;
                    if (InsideScroll(e)) return;   // scrolled content may sit outside its viewport
                    if (InsideContourLabels(e)) return;   // labels on the map run off its edges, as the map does
                    if (b.xMin < screen.xMin - 1 || b.yMin < screen.yMin - 1 || b.xMax > screen.xMax + 1 || b.yMax > screen.yMax + 1)
                        Problem(shot, $"{Describe(e)} is off the screen ({b.xMin:F0},{b.yMin:F0} to {b.xMax:F0},{b.yMax:F0} on {screen.width:F0}×{screen.height:F0})");
                    if (hasText && text.resolvedStyle.whiteSpace != WhiteSpace.Normal)
                    {
                        var need = text.MeasureTextSize(text.text, 0, VisualElement.MeasureMode.Undefined, 0, VisualElement.MeasureMode.Undefined);
                        if (need.x > text.contentRect.width + 1.5f)
                            Problem(shot, $"{Describe(e)} is cut short: \"{text.text}\" needs {need.x:F0} of {text.contentRect.width:F0}");
                    }
                });
                root.Query<ScrollView>().ForEach(scroll =>
                {
                    if (!UiFocus.IsShown(scroll) || scroll.contentContainer.childCount == 0) return;
                    float over = scroll.contentContainer.layout.width - scroll.contentViewport.layout.width;
                    if (over > 1) Problem(shot, $"{Describe(scroll)} scrolls sideways: its content is {over:F0} wider than it");
                });
                root.Query<StageColumn>().ForEach(column =>
                {
                    if (!UiFocus.IsShown(column)) return;
                    var parent = column.hierarchy.parent.worldBound;
                    var b = column.worldBound;
                    float want = StageColumn.WidthFor(parent.width, parent.height);
                    if (Mathf.Abs(b.width - want) > 1 || Mathf.Abs(b.center.x - parent.center.x) > 1)
                        Problem(shot, $"{Describe(column)} is {b.width:F0} wide at {b.center.x:F0}, not {want:F0} at {parent.center.x:F0}");
                    if (!column.hierarchy.parent.ClassListContains("modal")) return;
                    foreach (var child in column.Children())
                    {
                        if (!UiFocus.IsShown(child)) continue;
                        var c = child.worldBound;
                        if (Mathf.Abs(c.center.x - b.center.x) > 2 || Mathf.Abs(c.center.y - b.center.y) > 2)
                            Problem(shot, $"{Describe(child)} isn't centred in its column ({c.center.x:F0},{c.center.y:F0} vs {b.center.x:F0},{b.center.y:F0})");
                    }
                });
            }
        }

        /// <summary>
        /// The named parts on screen in this shot, for the HUD parity check against the mockup (tools/ui-parity,
        /// task P2-02): each shown element with a name, its box on the 1280×720 stage, its colours (0-255, as styled),
        /// type size and text. The mockup's data-ui names are the game's element names.
        /// </summary>
        void WriteLayout(string folder, string shot)
        {
            var sb = new StringBuilder("{\"shot\":\"").Append(shot).Append("\",\"parts\":{");
            var seen = new HashSet<string>();
            var hud = _viewer != null && _viewer.Hud != null ? _viewer.Hud.Document : null;
            var documents = FindObjectsByType<UIDocument>(FindObjectsSortMode.None).OrderBy(d => d == hud ? 0 : 1);
            foreach (var document in documents)
            {
                var root = document.rootVisualElement;
                if (root == null || root.panel == null || !document.isActiveAndEnabled) continue;
                var screen = root.panel.visualTree.worldBound;
                float k = screen.height > 0 ? 720f / screen.height : 1;
                root.Query<VisualElement>().ForEach(e =>
                {
                    if (string.IsNullOrEmpty(e.name) || !UiFocus.IsShown(e) || e.resolvedStyle.opacity <= 0.01f) return;
                    var b = e.worldBound;
                    if (b.width < 0.5f || b.height < 0.5f || float.IsNaN(b.x) || !seen.Add(e.name)) return;
                    var s = e.resolvedStyle;
                    var bg = s.backgroundColor;
                    // A see-through HUD panel draws its colour on a plate (MountainHud.Glass.cs): report that colour.
                    if (e.ClassListContains("glassy") && e.childCount > 0 && e[0].ClassListContains("hud-plate"))
                    {
                        bg = e[0].resolvedStyle.backgroundColor;
                        bg.a *= e[0].resolvedStyle.opacity;
                    }
                    if (seen.Count > 1) sb.Append(',');
                    sb.Append('"').Append(e.name).Append("\":{");
                    sb.AppendFormat(CultureInfo.InvariantCulture, "\"x\":{0:F2},\"y\":{1:F2},\"w\":{2:F2},\"h\":{3:F2}",
                        (b.x - screen.x) * k, (b.y - screen.y) * k, b.width * k, b.height * k);
                    sb.Append(",\"bg\":").Append(Rgba(bg)).Append(",\"color\":").Append(Rgba(s.color));
                    sb.Append(",\"border\":").Append(s.borderTopWidth > 0 ? Rgba(s.borderTopColor) : "null");
                    sb.AppendFormat(CultureInfo.InvariantCulture, ",\"fontSize\":{0:F1}", s.fontSize);
                    if (e is TextElement t && t.childCount == 0) sb.Append(",\"text\":\"").Append(Escape(t.text)).Append('"');
                    sb.Append('}');
                });
            }
            sb.Append("}}");
            File.WriteAllText(Path.Combine(folder, shot + ".layout.json"), sb.ToString());
        }

        static string Rgba(Color c) => string.Format(CultureInfo.InvariantCulture, "[{0},{1},{2},{3}]",
            Mathf.RoundToInt(c.r * 255), Mathf.RoundToInt(c.g * 255), Mathf.RoundToInt(c.b * 255), Mathf.RoundToInt(c.a * 255));

        static string Escape(string text) => (text ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"").Replace('\n', ' ').Trim();

        static bool InsideContourLabels(VisualElement e)
        {
            for (var x = e.hierarchy.parent; x != null; x = x.hierarchy.parent)
                if (x.name == "contour-labels") return true;
            return false;
        }

        static bool InsideScroll(VisualElement e)
        {
            for (var x = e.hierarchy.parent; x != null; x = x.hierarchy.parent)
                if (x is ScrollView) return true;
            return false;
        }

        static string Describe(VisualElement e)
        {
            string id = !string.IsNullOrEmpty(e.name) ? "#" + e.name : e.GetClasses().FirstOrDefault() is string c ? "." + c : e.GetType().Name;
            var parent = e.hierarchy.parent;
            while (parent != null && string.IsNullOrEmpty(parent.name)) parent = parent.hierarchy.parent;
            return parent != null ? $"{id} (in #{parent.name})" : id;
        }

        void Problem(string shot, string what)
        {
            _uiProblems++;
            _uiReport.Append(shot).Append(": ").AppendLine(what);
        }

        /// <summary>The window as the player sees it against the off-screen composite at the window's size.</summary>
        IEnumerator CompareWithWindow(string folder)
        {
            int w = Screen.width, h = Screen.height;
            yield return new WaitForEndOfFrame();
            var real = ScreenCapture.CaptureScreenshotAsTexture();
            var window = real.GetPixels32();
            File.WriteAllBytes(Path.Combine(folder, "compare_window.png"), real.EncodeToPNG());
            Destroy(real);
            Color32[] composite = null;
            yield return OffscreenShot(folder, "compare", w, h, p => composite = p);
            var tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false);
            tex.SetPixels32(composite);
            File.WriteAllBytes(Path.Combine(folder, "compare_offscreen.png"), tex.EncodeToPNG());
            Destroy(tex);
            double sum = 0;
            int n = Math.Min(window.Length, composite.Length);
            for (int i = 0; i < n; i++)
                sum += Math.Abs(window[i].r - composite[i].r) + Math.Abs(window[i].g - composite[i].g) + Math.Abs(window[i].b - composite[i].b);
            string line = string.Format(CultureInfo.InvariantCulture, "Off-screen composite vs the window at {0}×{1}: mean difference {2:F2} of 255 per channel (the drift holds still while capturing, so this is the composite alone)", w, h, sum / (3.0 * n));
            _uiReport.AppendLine(line);
            Debug.Log("[AppFlow] " + line);
        }
    }
}
