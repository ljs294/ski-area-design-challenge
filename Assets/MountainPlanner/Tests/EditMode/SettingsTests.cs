using System;
using System.Linq;
using MountainPlanner.Presentation;
using MountainPlanner.UI;
using MountainPlanner.UI.Flow;
using MountainPlanner.World;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Settings (S8; task P2-05): the graphics options over the presets, persistence across launches, the key
    /// bindings, the FOV rule, the display lists and the Auto tree-detail timing. Every test writes under its own
    /// store prefix, so the player's own settings are never touched.
    /// </summary>
    public sealed class SettingsTests
    {
        const string TestPrefix = "MountainPlannerTest.P2-05.";
        static readonly string[] Keys = { "Graphics", "VSync", "FrameCap", "FieldOfView", "Keys", "InvertZoom", "ZoomSpeed", "TreeDetailAuto", "LibraryFolder", "Offline" };
        int _vsync, _frameRate;

        [SetUp]
        public void SetUp()
        {
            _vsync = QualitySettings.vSyncCount;
            _frameRate = Application.targetFrameRate;
            SettingsStore.Prefix = TestPrefix;
            Forget();
            ReloadAll();
        }

        [TearDown]
        public void TearDown()
        {
            Forget();
            SettingsStore.Prefix = SettingsStore.DefaultPrefix;
            ReloadAll();
            QualitySettings.vSyncCount = _vsync;
            Application.targetFrameRate = _frameRate;
        }

        static void Forget()
        {
            foreach (string k in Keys) PlayerPrefs.DeleteKey(TestPrefix + k);
            PlayerPrefs.Save();
        }

        /// <summary>What a fresh launch does: every setting reads the store again.</summary>
        static void ReloadAll()
        {
            FramePacing.Reload();
            ViewFov.Reload();
            KeyBindings.Reload();
            CameraOptions.Reload();
            DataPreferences.Reload();
        }

        // ---------- graphics options ----------

        [Test]
        public void EveryPresetIsItsOwnPresetAndAnyChangeIsCustom()
        {
            foreach (QualityPreset p in Enum.GetValues(typeof(QualityPreset)))
            {
                var o = GraphicsOptions.For(p);
                Assert.That(o.Preset, Is.EqualTo(p), $"{p} reads as itself");
                Assert.That(o.WithTrees(TreeDetail.Auto).Preset, Is.EqualTo(p), $"{p} with Auto trees is still {p}");
                Assert.That(o.Level, Is.EqualTo(p), $"{p} sits on its own quality level");
                var changed = o;
                changed.RenderScalePercent = 95;
                Assert.That(changed.Preset, Is.Null, $"{p} with another render scale is Custom");
            }
            Assert.That(GraphicsOptions.Default.Preset, Is.EqualTo(QualityPreset.High));
            Assert.That(GraphicsOptions.Default.Trees, Is.EqualTo(TreeDetail.Auto));
        }

        [Test]
        public void PresetsMatchB1()
        {
            // The approved table (B1; docs/plans/phase1-benchmark-report.md).
            var low = GraphicsOptions.For(QualityPreset.Low);
            Assert.That((low.RenderScalePercent, low.Antialiasing, low.ShadowDistance, low.TerrainShading), Is.EqualTo((80, Antialiasing.Off, 60, false)));
            var ultra = GraphicsOptions.For(QualityPreset.Ultra);
            Assert.That((ultra.Antialiasing, ultra.ShadowDistance, GraphicsOptions.LodBias(ultra.Trees)), Is.EqualTo((Antialiasing.Msaa4, 250, 3f)));
            Assert.That(GraphicsOptions.LodBias(GraphicsOptions.For(QualityPreset.Medium).Trees), Is.EqualTo(1.5f));
            Assert.That(GraphicsOptions.LodBias(GraphicsOptions.For(QualityPreset.High).Trees), Is.EqualTo(2f));
        }

        [Test]
        public void PickingAPresetKeepsAutoTreeDetail()
        {
            var o = GraphicsOptions.Default.WithPreset(QualityPreset.Low);
            Assert.That(o.Trees, Is.EqualTo(TreeDetail.Auto));
            Assert.That(o.Preset, Is.EqualTo(QualityPreset.Low));
            Assert.That(GraphicsOptions.For(QualityPreset.High).WithPreset(QualityPreset.Low).Trees, Is.EqualTo(TreeDetail.Low));
        }

        [Test]
        public void OutOfRangeValuesAreClamped()
        {
            var o = new GraphicsOptions { RenderScalePercent = 999, ShadowDistance = 3, Antialiasing = (Antialiasing)42, Shadows = (ShadowLevel)(-1), Trees = (TreeDetail)9 }.Clamped();
            Assert.That(o.RenderScalePercent, Is.EqualTo(GraphicsOptions.MaxRenderScale));
            Assert.That(o.ShadowDistance, Is.EqualTo(GraphicsOptions.MinShadowDistance));
            Assert.That(o.Antialiasing, Is.EqualTo(Antialiasing.Msaa4));
            Assert.That(o.Shadows, Is.EqualTo(ShadowLevel.Off));
            Assert.That(o.Trees, Is.EqualTo(TreeDetail.Ultra));
        }

        [Test]
        public void ConfigureWritesTheOptionsIntoACopyOfTheAsset()
        {
            int level = Array.IndexOf(QualitySettings.names, "High");
            var original = QualitySettings.GetRenderPipelineAssetAt(level) as UniversalRenderPipelineAsset;
            Assert.That(original, Is.Not.Null, "High has a URP asset");
            var copy = UnityEngine.Object.Instantiate(original);
            try
            {
                var o = GraphicsOptions.For(QualityPreset.High);
                o.RenderScalePercent = 70;
                o.Antialiasing = Antialiasing.Msaa2;
                o.ShadowDistance = 320;
                QualityPresets.Configure(copy, o);
                Assert.That(copy.renderScale, Is.EqualTo(0.7f).Within(1e-5f));
                Assert.That(copy.msaaSampleCount, Is.EqualTo(2));
                Assert.That(copy.shadowDistance, Is.EqualTo(320f));
                o.Shadows = ShadowLevel.Off;
                QualityPresets.Configure(copy, o);
                Assert.That(copy.shadowDistance, Is.EqualTo(0f), "Off draws no sun shadows");
                Assert.That(original.renderScale, Is.EqualTo(1f), "the project's asset is untouched");
                Assert.That(original.shadowDistance, Is.EqualTo(150f));
            }
            finally { UnityEngine.Object.DestroyImmediate(copy); }
        }

        // ---------- persistence across launches ----------

        [Test]
        public void SettingsPersistAcrossLaunches()
        {
            var graphics = GraphicsOptions.For(QualityPreset.Medium);
            graphics.RenderScalePercent = 120;
            graphics.Antialiasing = Antialiasing.Smaa;
            graphics.Textures = TextureQuality.Half;
            QualityPresets.Save(graphics);
            FramePacing.SetVSync(true);
            FramePacing.SetCap(144);
            ViewFov.Set(52);
            CameraOptions.SetInvertZoom(true);
            CameraOptions.SetSpeed(ZoomSpeed.Fast);
            KeyBindings.Bind(GameAction.Toolbox, 0, new KeyChord(Key.B, KeyMods.Ctrl));
            DataPreferences.SetOffline(true);

            ReloadAll();   // a new launch

            Assert.That(QualityPresets.Saved(), Is.EqualTo(graphics));
            Assert.That(FramePacing.VSync, Is.True);
            Assert.That(FramePacing.Cap, Is.EqualTo(144));
            Assert.That(ViewFov.Vertical, Is.EqualTo(52));
            Assert.That(CameraOptions.InvertZoom, Is.True);
            Assert.That(CameraOptions.Speed, Is.EqualTo(ZoomSpeed.Fast));
            Assert.That(KeyBindings.Get(GameAction.Toolbox, 0), Is.EqualTo(new KeyChord(Key.B, KeyMods.Ctrl)));
            Assert.That(KeyBindings.Caption(GameAction.Toolbox), Is.EqualTo("Ctrl B"));
            Assert.That(DataPreferences.Offline, Is.True);
        }

        [Test]
        public void AFreshInstallHasTheDefaults()
        {
            Assert.That(QualityPresets.Saved(), Is.EqualTo(GraphicsOptions.Default));
            Assert.That(FramePacing.VSync, Is.False);
            Assert.That(FramePacing.Cap, Is.EqualTo(0));
            Assert.That(ViewFov.Vertical, Is.EqualTo(60));
            Assert.That(CameraOptions.InvertZoom, Is.False);
            Assert.That(KeyBindings.IsDefault, Is.True);
            Assert.That(DataPreferences.LibraryFolder, Is.EqualTo(""));
        }

        [Test]
        public void ABrokenStoreFallsBackToTheDefaults()
        {
            SettingsStore.SetString("Graphics", "{not json");
            SettingsStore.SetString("Keys", "Nonsense=Q;MoveForward=Escape,Banana;Toolbox=Shift+Ctrl+K,");
            SettingsStore.SetInt("FieldOfView", 500);
            SettingsStore.SetInt("FrameCap", 77);
            ReloadAll();
            Assert.That(QualityPresets.Saved(), Is.EqualTo(GraphicsOptions.Default));
            Assert.That(KeyBindings.Get(GameAction.MoveForward, 0), Is.EqualTo(new KeyChord(Key.W)), "Esc can't be bound; the default stays");
            Assert.That(KeyBindings.Get(GameAction.Toolbox, 0), Is.EqualTo(new KeyChord(Key.K, KeyMods.Shift | KeyMods.Ctrl)));
            Assert.That(ViewFov.Vertical, Is.EqualTo(ViewFov.Max));
            Assert.That(FramePacing.Cap, Is.EqualTo(0));
        }

        // ---------- keys ----------

        [Test]
        public void DefaultsAreTheKeyMap()
        {
            Assert.That(KeyBindings.Actions.Length, Is.EqualTo(Enum.GetValues(typeof(GameAction)).Length), "every action has an entry");
            Assert.That(KeyBindings.Caption(GameAction.LayerSnow), Is.EqualTo("Shift 1"));
            Assert.That(KeyBindings.Caption(GameAction.InfoConditions), Is.EqualTo("Shift 0"));
            Assert.That(KeyBindings.Caption(GameAction.Analysis), Is.EqualTo("Tab"));
            Assert.That(KeyBindings.Caption(GameAction.MoveForward, 1), Is.EqualTo("↑"));
            Assert.That(KeyBindings.Caption(GameAction.SinkOrZoomOut), Is.EqualTo("Page Down"));
            Assert.That(KeyBindings.Caption(GameAction.ZoomOut), Is.EqualTo("−"));
            // No two actions that can both be live share a key.
            var seen = new System.Collections.Generic.Dictionary<KeyChord, GameAction>();
            for (int a = 0; a < KeyBindings.Count; a++)
                for (int s = 0; s < KeyBindings.Slots; s++)
                {
                    var c = KeyBindings.Get((GameAction)a, s);
                    if (c.IsNone) continue;
                    if (seen.TryGetValue(c, out var other))
                        Assert.That(KeyBindings.Actions[a].When & KeyBindings.Actions[(int)other].When, Is.EqualTo((KeyBindings.Context)0),
                                    $"{(GameAction)a} and {other} share {c}");
                    else seen[c] = (GameAction)a;
                }
        }

        [Test]
        public void BindingATakenKeySwapsIt()
        {
            // H belongs to Hide the UI; giving it to the Toolbox moves T to Hide the UI.
            var swapped = KeyBindings.Bind(GameAction.Toolbox, 0, new KeyChord(Key.H));
            Assert.That(swapped, Is.EqualTo(GameAction.HideUi));
            Assert.That(KeyBindings.Get(GameAction.Toolbox, 0), Is.EqualTo(new KeyChord(Key.H)));
            Assert.That(KeyBindings.Get(GameAction.HideUi, 0), Is.EqualTo(new KeyChord(Key.T)));
            // Pause works only in the normal view and saving a photo only in photo mode, so they may share a key
            // (Space is both by default).
            Assert.That(KeyBindings.Bind(GameAction.Pause, 0, new KeyChord(Key.F12)), Is.Null);
            Assert.That(KeyBindings.Get(GameAction.PhotoCapture, 0), Is.EqualTo(new KeyChord(Key.F12)));
            // The camera works in both, so a camera key does move.
            Assert.That(KeyBindings.Bind(GameAction.PhotoCapture, 0, new KeyChord(Key.Q)), Is.EqualTo(GameAction.RotateLeft));
        }

        [Test]
        public void ShiftedKeysAreTheirOwnKeys()
        {
            Assert.That(KeyBindings.Bind(GameAction.Speed1, 0, new KeyChord(Key.Digit1)), Is.Null, "1 and Shift 1 are different keys");
            Assert.That(KeyBindings.Bind(GameAction.Speed1, 0, new KeyChord(Key.Digit3, KeyMods.Shift)), Is.EqualTo(GameAction.LayerTrees));
        }

        [Test]
        public void EscEnterAndModifiersCantBeBound()
        {
            foreach (var key in new[] { Key.Escape, Key.Enter, Key.LeftShift, Key.RightCtrl, Key.LeftAlt })
            {
                Assert.That(KeyBindings.Bind(GameAction.Units, 0, new KeyChord(key)), Is.Null);
                Assert.That(KeyBindings.Get(GameAction.Units, 0), Is.EqualTo(new KeyChord(Key.U)), $"{key} refused");
            }
        }

        [Test]
        public void ResetPutsTheKeyMapBack()
        {
            KeyBindings.Bind(GameAction.MoveForward, 0, new KeyChord(Key.I));
            KeyBindings.Bind(GameAction.MoveForward, 1, default);
            Assert.That(KeyBindings.IsDefault, Is.False);
            KeyBindings.ResetAll();
            Assert.That(KeyBindings.IsDefault, Is.True);
            KeyBindings.Reload();
            Assert.That(KeyBindings.IsDefault, Is.True, "and remembers it");
        }

        [Test]
        public void ChordsRoundTripThroughText()
        {
            foreach (var c in new[] { new KeyChord(Key.W), new KeyChord(Key.Digit6, KeyMods.Shift), new KeyChord(Key.F5, KeyMods.Ctrl | KeyMods.Alt), default })
            {
                Assert.That(KeyChord.TryParse(c.ToString(), out var back), Is.True, c.ToString());
                Assert.That(back, Is.EqualTo(c));
            }
            Assert.That(KeyChord.TryParse("Shift+Nope", out _), Is.False);
        }

        // ---------- field of view ----------

        [Test]
        public void TheVerticalAngleStaysAndWideScreensSeeMoreAcross()
        {
            Assert.That(ViewFov.Horizontal(60, 16f / 9f), Is.EqualTo(91.5f).Within(0.1f));
            Assert.That(ViewFov.Horizontal(60, 2560f / 1080f), Is.EqualTo(107.7f).Within(0.2f));   // the owner's 21:9
            Assert.That(ViewFov.Horizontal(60, 3440f / 1440f), Is.EqualTo(108.1f).Within(0.2f));
            Assert.That(ViewFov.Horizontal(60, 32f / 9f), Is.EqualTo(128.1f).Within(0.2f));
            ViewFov.Set(10, remember: false);
            Assert.That(ViewFov.Vertical, Is.EqualTo(ViewFov.Min));
        }

        // ---------- display ----------

        static Resolution R(int w, int h) => new Resolution { width = w, height = h };

        [Test]
        public void AWindowOffersEveryShapeThatFits()
        {
            var display = new Vector2Int(2560, 1080);
            var modes = new[] { R(1280, 720), R(1920, 1080), R(2560, 1080), R(800, 600) };
            var windowed = UiDisplay.Resolutions(true, modes, display, new Vector2Int(1920, 810));
            Assert.That(windowed, Has.Member(new Vector2Int(2560, 1080)));
            Assert.That(windowed, Has.Member(new Vector2Int(1600, 900)));
            Assert.That(windowed, Has.Member(new Vector2Int(1920, 810)), "the current window is listed");
            Assert.That(windowed, Has.No.Member(new Vector2Int(3440, 1440)), "too tall for this screen");
            Assert.That(windowed, Has.No.Member(new Vector2Int(800, 600)), "too small for the UI");
            var full = UiDisplay.Resolutions(false, modes, display, display);
            Assert.That(full, Is.EqualTo(new[] { new Vector2Int(1280, 720), new Vector2Int(1920, 1080), new Vector2Int(2560, 1080) }));
            Assert.That(full.Select(s => UiDisplay.Shape(s.x, s.y)), Is.EqualTo(new[] { "16:9", "16:9", "21:9" }));
            Assert.That(UiDisplay.Shape(5120, 1440), Is.EqualTo("32:9"));
            Assert.That(UiDisplay.Shape(3440, 1440), Is.EqualTo("21:9"));
        }

        [Test]
        public void FramePacingFollowsTheChoicesUnlessHeld()
        {
            FramePacing.SetVSync(false);
            FramePacing.SetCap(60);
            Assert.That(Application.targetFrameRate, Is.EqualTo(60));
            FramePacing.Hold();
            Assert.That(Application.targetFrameRate, Is.EqualTo(-1), "benchmarks run uncapped");
            FramePacing.Hold();
            FramePacing.Release();
            Assert.That(Application.targetFrameRate, Is.EqualTo(-1), "holds count");
            FramePacing.Release();
            Assert.That(Application.targetFrameRate, Is.EqualTo(60));
            FramePacing.SetVSync(true);
            Assert.That(QualitySettings.vSyncCount, Is.EqualTo(1));
        }

        // ---------- Auto tree detail ----------

        static float Run(TreeDetailTiming timing, Func<float, float> frameMsAtBias)
        {
            int guard = 0;
            while (!timing.Feed(frameMsAtBias(timing.Bias)) && ++guard < 10000) { }
            return timing.Chosen;
        }

        [Test]
        public void AFastPcGetsUltraTreesAfterOnePass()
        {
            var t = new TreeDetailTiming();
            Assert.That(Run(t, _ => 6f), Is.EqualTo(3f));
            Assert.That(float.IsNaN(t.P95[1]), Is.True, "stopped at the first bias that fit");
        }

        [Test]
        public void ASlowerPcStepsDownToTheFirstBiasThatFits()
        {
            // 16 ms at bias 3, 13 at 2, 10 at 1.5: Medium.
            Assert.That(Run(new TreeDetailTiming(), b => b >= 3 ? 16f : b >= 2 ? 13f : b >= 1.5f ? 10f : 8f), Is.EqualTo(1.5f));
            Assert.That(TreeDetailTiming.DetailOf(1.5f), Is.EqualTo(TreeDetail.Medium));
        }

        [Test]
        public void APcThatFitsNothingGetsLow()
        {
            Assert.That(Run(new TreeDetailTiming(), _ => 40f), Is.EqualTo(1f));
        }

        [Test]
        public void TheP95IgnoresAFewHitches()
        {
            var samples = Enumerable.Repeat(8f, TreeDetailTiming.SampleFrames).ToArray();
            samples[3] = 90f;   // one hitch in 45 is above the 95th percentile
            Assert.That(TreeDetailTiming.Percentile95(samples), Is.EqualTo(8f));
        }

        [Test]
        public void ADisconnectedDesktopIsNotTimed()
        {
            Assert.That(TreeDetailTiming.DisplayTrustworthy(640, 480), Is.False);
            Assert.That(TreeDetailTiming.DisplayTrustworthy(2560, 1080), Is.True);
        }

        [Test]
        public void TheResultIsKeptPerCardAndScreenSize()
        {
            string here = TreeDetailTiming.Machine("RTX 4070", 2560, 1080);
            Assert.That(TreeDetailTiming.Remembered(here), Is.EqualTo(0f));
            TreeDetailTiming.Remember(here, 2f);
            Assert.That(TreeDetailTiming.Remembered(here), Is.EqualTo(2f));
            Assert.That(TreeDetailTiming.Remembered(TreeDetailTiming.Machine("RTX 4070", 3440, 1440)), Is.EqualTo(0f), "another screen size times again");
            Assert.That(TreeDetailTiming.Remembered(TreeDetailTiming.Machine("RTX 2060", 2560, 1080)), Is.EqualTo(0f), "another card times again");
            TreeDetailTiming.Forget();
            Assert.That(TreeDetailTiming.Remembered(here), Is.EqualTo(0f));
        }
    }
}
