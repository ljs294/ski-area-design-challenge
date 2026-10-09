using System;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace MountainPlanner.Presentation
{
    /// <summary>The four quality presets (0.4 S8; task 15). Medium is the minimum-spec target (T13), High the reference.</summary>
    public enum QualityPreset { Low, Medium, High, Ultra }

    /// <summary>
    /// One Unity quality level per preset (ProjectSettings/QualitySettings.asset), each with its own URP asset in
    /// Assets/Settings (anti-aliasing, render scale, shadow distance, cascades and resolution, soft shadows) and its
    /// own LOD bias, which sets how far the trees keep their detailed models. On top of that, each preset picks the
    /// terrain's detail (<see cref="TerrainDetail"/>) and whether the terrain's sky occlusion and distant shadows
    /// (<see cref="FarTerrainShadow"/>) are on.
    ///
    /// Settings › Graphics (task P2-05) applies a full <see cref="GraphicsOptions"/> set, live: a preset's own set
    /// runs on the preset's quality level and URP asset untouched (so benchmarks measure exactly B1), and any other
    /// set runs on a copy of the asset whose shadows it uses, with the other options written into the copy. The
    /// project's assets are never edited.
    /// </summary>
    public static class QualityPresets
    {
        const string OptionsKey = "Graphics";

        /// <summary>The preset in effect: the options' preset, or under Custom the level they sit on.</summary>
        public static QualityPreset Current { get; private set; } = QualityPreset.High;

        /// <summary>The options applied last.</summary>
        public static GraphicsOptions Options { get; private set; } = GraphicsOptions.For(QualityPreset.High);

        /// <summary>The LOD bias Auto tree detail measured on this PC (<see cref="TreeDetailTiming"/>), or 0 until it has.</summary>
        public static float AutoLodBias { get; private set; }

        /// <summary>Raised after options are applied, for the parts that live in the scene (the terrain's shading).</summary>
        public static event Action Applied;

        static readonly UniversalRenderPipelineAsset[] Copies = new UniversalRenderPipelineAsset[4];

        /// <summary>The quality level's name in QualitySettings, which is the preset's name.</summary>
        public static string LevelName(QualityPreset preset) => preset.ToString();

        public static TerrainDetail Terrain(QualityPreset preset) => preset switch
        {
            QualityPreset.Low => TerrainDetail.Low,
            QualityPreset.Medium => TerrainDetail.Medium,
            QualityPreset.Ultra => TerrainDetail.Ultra,
            _ => TerrainDetail.High,
        };

        /// <summary>Sky occlusion from the terrain (beauty pass, item 2) and distant terrain shadows: off on Low only.</summary>
        public static bool TerrainShading(QualityPreset preset) => preset != QualityPreset.Low;

        /// <summary>Reads -quality low|medium|high|ultra from the command line; <paramref name="fallback"/> when it's absent or unknown.</summary>
        public static QualityPreset FromArgs(string[] args, QualityPreset fallback = QualityPreset.High)
        {
            int i = Array.IndexOf(args, "-quality");
            return i >= 0 && i + 1 < args.Length && Enum.TryParse(args[i + 1], true, out QualityPreset p) ? p : fallback;
        }

        /// <summary>Switches Unity to the preset's quality level (and so its URP asset and LOD bias). Call before a mountain opens.</summary>
        public static void Apply(QualityPreset preset) => Apply(GraphicsOptions.For(preset));

        /// <summary>
        /// The saved options (Default until the player chooses), read once at start. A -quality on the command line
        /// replaces them for that run, without changing what's saved.
        /// </summary>
        public static GraphicsOptions Saved()
        {
            string json = SettingsStore.GetString(OptionsKey, "");
            if (json.Length == 0) return GraphicsOptions.Default;
            try { return JsonUtility.FromJson<GraphicsOptions>(json).Clamped(); }
            catch (ArgumentException) { return GraphicsOptions.Default; }
        }

        public static void Save(GraphicsOptions options) => SettingsStore.SetString(OptionsKey, JsonUtility.ToJson(options));

        /// <summary>Applies options live and, unless <paramref name="remember"/> is false, saves them.</summary>
        public static void Choose(GraphicsOptions options, bool remember = true)
        {
            Apply(options);
            if (remember) Save(Options);
        }

        /// <summary>Puts a full set of options into Unity, live: the quality level, its URP asset (or a copy), cameras and terrain.</summary>
        public static void Apply(GraphicsOptions options)
        {
            options = options.Clamped();
            var level = options.Level;
            int index = Array.IndexOf(QualitySettings.names, LevelName(level));
            if (index < 0)
            {
                Debug.LogWarning($"[QualityPresets] No quality level named {LevelName(level)}; keeping {QualitySettings.names[QualitySettings.GetQualityLevel()]}.");
                return;
            }
            if (QualitySettings.GetQualityLevel() != index) QualitySettings.SetQualityLevel(index, applyExpensiveChanges: true);
            var original = QualitySettings.GetRenderPipelineAssetAt(index) as UniversalRenderPipelineAsset;
            var own = GraphicsOptions.For(level);
            bool exact = options.WithTrees(own.Trees) == own;   // tree detail lives outside the asset
            if (original != null)
            {
                if (exact) QualitySettings.renderPipeline = original;
                else
                {
                    var copy = Copies[(int)level];
                    if (copy == null)
                    {
                        copy = Copies[(int)level] = UnityEngine.Object.Instantiate(original);
                        copy.name = original.name + " (Settings)";
                        copy.hideFlags = HideFlags.DontSave;
                    }
                    Configure(copy, options);
                    QualitySettings.renderPipeline = copy;
                }
            }
            QualitySettings.antiAliasing = GraphicsOptions.MsaaSamples(options.Antialiasing);
            QualitySettings.lodBias = options.Trees == TreeDetail.Auto && AutoLodBias > 0 ? AutoLodBias
                                    : GraphicsOptions.LodBias(options.Trees == TreeDetail.Auto ? own.Trees : options.Trees);
            QualitySettings.globalTextureMipmapLimit = (int)options.Textures;
            FramePacing.Reapply();   // a level switch resets V-Sync to the level's own
            Options = options;
            Current = options.Preset ?? level;
            foreach (var camera in Camera.allCameras) ApplyTo(camera);
            foreach (var terrain in UnityEngine.Terrain.activeTerrains) TerrainDetailSettings.Apply(terrain, options.Terrain);
            Applied?.Invoke();
        }

        /// <summary>Writes the options a URP asset carries (render scale, MSAA, shadows) into a copy of one.</summary>
        public static void Configure(UniversalRenderPipelineAsset asset, GraphicsOptions options)
        {
            asset.renderScale = options.RenderScalePercent / 100f;
            asset.msaaSampleCount = GraphicsOptions.MsaaSamples(options.Antialiasing);
            // URP has no public switch for the sun's shadows; a distance of 0 (inside the near plane) draws none.
            asset.shadowDistance = options.Shadows == ShadowLevel.Off ? 0f : options.ShadowDistance;
        }

        /// <summary>The camera's own anti-aliasing (FXAA and SMAA are post-processing on the camera; MSAA lives in the URP asset).</summary>
        public static void ApplyTo(Camera camera)
        {
            if (camera == null || !camera.TryGetComponent(out UniversalAdditionalCameraData data)) return;
            data.antialiasing = Options.Antialiasing switch
            {
                Antialiasing.Fxaa => AntialiasingMode.FastApproximateAntialiasing,
                Antialiasing.Smaa => AntialiasingMode.SubpixelMorphologicalAntiAliasing,
                _ => AntialiasingMode.None,
            };
        }

        /// <summary>Auto tree detail's measured LOD bias (0 forgets it); applied at once if Auto is on.</summary>
        public static void SetAutoLodBias(float bias)
        {
            AutoLodBias = bias;
            if (Options.Trees == TreeDetail.Auto && bias > 0) QualitySettings.lodBias = bias;
        }
    }
}
