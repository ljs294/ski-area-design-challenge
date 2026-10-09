using System;
using MountainPlanner.World;

namespace MountainPlanner.Presentation
{
    public enum Antialiasing { Off, Fxaa, Smaa, Msaa2, Msaa4 }

    /// <summary>The shadow levels are the presets' shadows (B1): map size, cascades and soft shadows together.</summary>
    public enum ShadowLevel { Off, Low, Medium, High, Ultra }

    /// <summary>How far the trees keep their detailed models (the LOD bias, F4); Auto picks one from a timing (task P2-05).</summary>
    public enum TreeDetail { Auto, Low, Medium, High, Ultra }

    /// <summary>Texture resolution: full, or every texture's top one or two mip levels skipped.</summary>
    public enum TextureQuality { Full, Half, Quarter }

    /// <summary>
    /// Settings › Graphics (0.4 S8, decision U3; task P2-05): every option the menu offers, over the four presets
    /// (B1). A preset is a full set of options; changing one option makes the set Custom (<see cref="Preset"/> null).
    /// Engine-agnostic values only; <see cref="QualityPresets.Apply(GraphicsOptions)"/> puts them into Unity.
    /// </summary>
    [Serializable]
    public struct GraphicsOptions : IEquatable<GraphicsOptions>
    {
        public const int MinRenderScale = 50, MaxRenderScale = 200, RenderScaleStep = 5;
        public const int MinShadowDistance = 50, MaxShadowDistance = 400, ShadowDistanceStep = 10;

        public int RenderScalePercent;
        public Antialiasing Antialiasing;
        public ShadowLevel Shadows;
        public int ShadowDistance;
        public TerrainDetail Terrain;
        /// <summary>The terrain's sky occlusion and distant shadows (<see cref="FarTerrainShadow"/>).</summary>
        public bool TerrainShading;
        public TreeDetail Trees;
        public TextureQuality Textures;

        /// <summary>The options a preset stands for (B1; the benchmark report's table).</summary>
        public static GraphicsOptions For(QualityPreset preset) => preset switch
        {
            QualityPreset.Low => new GraphicsOptions { RenderScalePercent = 80, Antialiasing = Antialiasing.Off, Shadows = ShadowLevel.Low, ShadowDistance = 60, Terrain = TerrainDetail.Low, TerrainShading = false, Trees = TreeDetail.Low, Textures = TextureQuality.Full },
            QualityPreset.Medium => new GraphicsOptions { RenderScalePercent = 100, Antialiasing = Antialiasing.Msaa2, Shadows = ShadowLevel.Medium, ShadowDistance = 100, Terrain = TerrainDetail.Medium, TerrainShading = true, Trees = TreeDetail.Medium, Textures = TextureQuality.Full },
            QualityPreset.Ultra => new GraphicsOptions { RenderScalePercent = 100, Antialiasing = Antialiasing.Msaa4, Shadows = ShadowLevel.Ultra, ShadowDistance = 250, Terrain = TerrainDetail.Ultra, TerrainShading = true, Trees = TreeDetail.Ultra, Textures = TextureQuality.Full },
            _ => new GraphicsOptions { RenderScalePercent = 100, Antialiasing = Antialiasing.Msaa4, Shadows = ShadowLevel.High, ShadowDistance = 150, Terrain = TerrainDetail.High, TerrainShading = true, Trees = TreeDetail.High, Textures = TextureQuality.Full },
        };

        /// <summary>The options until the player changes them: High (the reference look), with Auto tree detail.</summary>
        public static GraphicsOptions Default => For(QualityPreset.High).WithTrees(TreeDetail.Auto);

        /// <summary>The preset these options are, or null for Custom. Auto tree detail goes with any preset.</summary>
        public QualityPreset? Preset
        {
            get
            {
                for (var p = QualityPreset.Low; p <= QualityPreset.Ultra; p++)
                {
                    var o = For(p);
                    if (Trees == TreeDetail.Auto) o.Trees = TreeDetail.Auto;
                    if (Equals(o)) return p;
                }
                return null;
            }
        }

        /// <summary>The preset's options, keeping Auto tree detail if it was on.</summary>
        public GraphicsOptions WithPreset(QualityPreset preset)
        {
            var o = For(preset);
            if (Trees == TreeDetail.Auto) o.Trees = TreeDetail.Auto;
            return o;
        }

        public GraphicsOptions WithTrees(TreeDetail trees)
        {
            var o = this;
            o.Trees = trees;
            return o;
        }

        /// <summary>The Unity quality level (and URP asset) the options sit on: the one whose shadows they use.</summary>
        public QualityPreset Level => Shadows switch
        {
            ShadowLevel.Low => QualityPreset.Low,
            ShadowLevel.Medium => QualityPreset.Medium,
            ShadowLevel.Ultra => QualityPreset.Ultra,
            _ => QualityPreset.High,
        };

        /// <summary>The LOD bias for a fixed tree detail (B1's 1, 1.5, 2 and 3); Auto has none of its own.</summary>
        public static float LodBias(TreeDetail trees) => trees switch
        {
            TreeDetail.Low => 1f,
            TreeDetail.Medium => 1.5f,
            TreeDetail.Ultra => 3f,
            _ => 2f,
        };

        public static int MsaaSamples(Antialiasing aa) => aa == Antialiasing.Msaa4 ? 4 : aa == Antialiasing.Msaa2 ? 2 : 1;

        /// <summary>Values from an older or hand-edited store, brought back into range.</summary>
        public GraphicsOptions Clamped()
        {
            var o = this;
            o.RenderScalePercent = Snap(o.RenderScalePercent, MinRenderScale, MaxRenderScale, RenderScaleStep);
            o.ShadowDistance = Snap(o.ShadowDistance, MinShadowDistance, MaxShadowDistance, ShadowDistanceStep);
            o.Antialiasing = (Antialiasing)Math.Clamp((int)o.Antialiasing, 0, (int)Antialiasing.Msaa4);
            o.Shadows = (ShadowLevel)Math.Clamp((int)o.Shadows, 0, (int)ShadowLevel.Ultra);
            o.Terrain = (TerrainDetail)Math.Clamp((int)o.Terrain, 0, (int)TerrainDetail.Ultra);
            o.Trees = (TreeDetail)Math.Clamp((int)o.Trees, 0, (int)TreeDetail.Ultra);
            o.Textures = (TextureQuality)Math.Clamp((int)o.Textures, 0, (int)TextureQuality.Quarter);
            return o;
        }

        /// <summary>A value snapped to its steps within its range.</summary>
        static int Snap(int value, int min, int max, int step) =>
            Math.Clamp((int)Math.Round(value / (double)step) * step, min, max);

        public bool Equals(GraphicsOptions o) =>
            RenderScalePercent == o.RenderScalePercent && Antialiasing == o.Antialiasing && Shadows == o.Shadows &&
            ShadowDistance == o.ShadowDistance && Terrain == o.Terrain && TerrainShading == o.TerrainShading &&
            Trees == o.Trees && Textures == o.Textures;

        public override bool Equals(object obj) => obj is GraphicsOptions o && Equals(o);

        public override int GetHashCode() =>
            HashCode.Combine(RenderScalePercent, Antialiasing, Shadows, ShadowDistance, Terrain, TerrainShading, Trees, Textures);

        public static bool operator ==(GraphicsOptions a, GraphicsOptions b) => a.Equals(b);
        public static bool operator !=(GraphicsOptions a, GraphicsOptions b) => !a.Equals(b);
    }
}
