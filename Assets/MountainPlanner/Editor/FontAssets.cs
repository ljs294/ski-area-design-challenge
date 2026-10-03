using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.TextCore.LowLevel;
using UnityEngine.TextCore.Text;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// Builds the UI font assets (game-ui-direction.md UI-6 and §9): Overpass for words and Overpass Mono for
    /// figures, Regular with Bold as its bold weight, from the static TTFs in Art/UI/Fonts (SIL Open Font
    /// License, Google Fonts). Every asset is static, pre-filled with the characters the UI writes, so playing
    /// never adds glyphs and the assets don't churn in git.
    /// The middle dot (U+00B7) is left out on purpose: Overpass draws it off-centre, so it falls through to the
    /// panel's default font, as the accepted mockup sends that one character to a system font. Anything else
    /// missing falls through the same way.
    ///   Unity -batchmode -executeMethod MountainPlanner.Editor.FontAssets.Build -quit
    /// Use in USS: -unity-font-definition: url("Fonts/Overpass-SDF.asset") (Mono: Fonts/OverpassMono-SDF.asset).
    /// </summary>
    public static class FontAssets
    {
        public const string Folder = "Assets/MountainPlanner/Art/UI/Fonts/";
        const char MiddleDot = '·';

        [MenuItem("Mountain Planner/Build UI Font Assets")]
        public static void Build()
        {
            var words = Characters(latin1: true);
            var regular = Bake("Overpass-SDF", "Overpass-Regular.ttf", 40, 1024, 512, words);
            var bold = Bake("Overpass-Bold-SDF", "Overpass-Bold.ttf", 40, 1024, 512, words);
            var figures = Characters(latin1: false);
            var mono = Bake("OverpassMono-SDF", "OverpassMono-Regular.ttf", 36, 512, 512, figures);
            var monoBold = Bake("OverpassMono-Bold-SDF", "OverpassMono-Bold.ttf", 36, 512, 512, figures);

            // -unity-font-style: bold picks the real bold face (weight 700) instead of a synthetic one.
            regular.fontWeightTable[7].regularTypeface = bold;
            mono.fontWeightTable[7].regularTypeface = monoBold;
            foreach (var asset in new[] { regular, bold, mono, monoBold }) EditorUtility.SetDirty(asset);
            AssetDatabase.SaveAssets();
            Debug.Log("[FontAssets] Built Overpass and Overpass Mono, Regular and Bold.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// The characters the UI writes: Basic Latin, (for words) Latin-1 letters and signs for place names,
        /// and the punctuation and signs the screens use, without the middle dot.
        /// </summary>
        public static string Characters(bool latin1)
        {
            var chars = new StringBuilder();
            for (char c = ' '; c <= '~'; c++) chars.Append(c);
            if (latin1)
                for (char c = ' '; c <= 'ÿ'; c++)
                    if (c != MiddleDot) chars.Append(c);
            chars.Append("–—‘’“”•…‰′″€−×÷≤≥≈±°←↑→↓");
            return new string(chars.ToString().Where(c => c != MiddleDot).Distinct().ToArray());
        }

        static FontAsset Bake(string name, string ttf, int pointSize, int width, int height, string characters)
        {
            var font = AssetDatabase.LoadAssetAtPath<Font>(Folder + ttf)
                ?? throw new System.InvalidOperationException($"{Folder}{ttf} is missing (git lfs checkout?).");
            string path = Folder + name + ".asset";
            AssetDatabase.DeleteAsset(path);

            var asset = FontAsset.CreateFontAsset(font, pointSize, pointSize / 8, GlyphRenderMode.SDFAA, width, height, AtlasPopulationMode.Dynamic, false);
            asset.name = name;
            AssetDatabase.CreateAsset(asset, path);
            asset.atlasTextures[0].name = name + " Atlas";
            AssetDatabase.AddObjectToAsset(asset.atlasTextures[0], asset);
            asset.material.name = name + " Material";
            AssetDatabase.AddObjectToAsset(asset.material, asset);

            asset.TryAddCharacters(characters, out string missing);
            asset.atlasPopulationMode = AtlasPopulationMode.Static;
            asset.fallbackFontAssetTable = new List<FontAsset>();
            EditorUtility.SetDirty(asset.atlasTextures[0]);
            EditorUtility.SetDirty(asset);
            Debug.Log($"[FontAssets] {name}: {asset.characterTable.Count} characters in a {width}×{height} atlas" +
                      (string.IsNullOrEmpty(missing) ? "." : $"; not in the font (they fall back): {missing}"));
            return asset;
        }
    }
}
