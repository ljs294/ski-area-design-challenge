using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
            foreach (var asset in new[] { regular, bold, mono, monoBold })
            {
                Kern(asset);
                EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();
            Debug.Log("[FontAssets] Built Overpass and Overpass Mono, Regular and Bold.");
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Adds the fonts' kerning to the assets already built, without rebaking them (task P2-02: words ran 2–5% wider
        /// than the mockup's, which the browser kerns).
        ///   Unity -batchmode -executeMethod MountainPlanner.Editor.FontAssets.AddKerning -quit
        /// </summary>
        [MenuItem("Mountain Planner/Add Kerning to UI Font Assets")]
        public static void AddKerning()
        {
            foreach (string name in new[] { "Overpass-SDF", "Overpass-Bold-SDF", "OverpassMono-SDF", "OverpassMono-Bold-SDF" })
            {
                var asset = AssetDatabase.LoadAssetAtPath<FontAsset>(Folder + name + ".asset");
                if (asset == null) { Debug.LogError($"[FontAssets] {name} is missing."); continue; }
                Kern(asset);
                EditorUtility.SetDirty(asset);
            }
            AssetDatabase.SaveAssets();
            if (Application.isBatchMode) EditorApplication.Exit(0);
        }

        /// <summary>
        /// Fills a static asset's pair adjustments (kerning) from its font's GPOS table, or its old kern table, for the
        /// glyphs it holds. A static asset never imports them itself, and TextCore reads them only from this table.
        /// The font engine's readers are internal in Unity 6.3, so they're reached by name.
        /// </summary>
        static void Kern(FontAsset asset)
        {
            const BindingFlags Static = BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
            // A static asset keeps no link to its font: its name says which TTF it came from.
            string ttf = asset.name.Replace("-SDF", "") + (asset.name.Contains("Bold") ? "" : "-Regular") + ".ttf";
            string file = System.IO.Path.GetFullPath(Folder + ttf);
            var handleType = typeof(FontEngine).Assembly.GetType("UnityEngine.TextCore.LowLevel.FontFaceHandle");
            var load = typeof(FontEngine).GetMethod("LoadFontFace", Static, null, new[] { typeof(string), typeof(float), typeof(int), handleType.MakeByRefType() }, null);
            var read = typeof(FontEngine).GetMethod("GetPairAdjustmentRecords", Static, null, new[] { handleType, typeof(uint[]) }, null);
            var readKern = typeof(FontEngine).GetMethod("GetGlyphPairAdjustmentTable", Static, null, new[] { handleType, typeof(uint[]) }, null);
            var unload = typeof(FontEngine).GetMethod("UnloadFontFace", Static, null, new[] { handleType }, null);
            var table = typeof(FontAsset).GetProperty("fontFeatureTable", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)?.GetValue(asset) as FontFeatureTable;
            var list = typeof(FontFeatureTable).GetField("m_GlyphPairAdjustmentRecords", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(table) as List<GlyphPairAdjustmentRecord>;
            if (load == null || read == null || unload == null || list == null)
            {
                Debug.LogError("[FontAssets] Unity's font engine changed shape (6.3 API expected); kerning not added.");
                return;
            }
            var args = new object[] { file, asset.faceInfo.pointSize, 0, null };
            if (!System.IO.File.Exists(file) || (FontEngineError)load.Invoke(null, args) != FontEngineError.Success)
            {
                Debug.LogError($"[FontAssets] {asset.name}: can't load {ttf} for kerning.");
                return;
            }
            object face = args[3];
            uint[] glyphs = asset.glyphTable.Select(g => g.index).ToArray();
            var records = read.Invoke(null, new[] { face, glyphs }) as GlyphPairAdjustmentRecord[];
            if ((records == null || records.Length == 0) && readKern != null) records = readKern.Invoke(null, new[] { face, glyphs }) as GlyphPairAdjustmentRecord[];
            unload.Invoke(null, new[] { face });
            // Keep the pairs between this asset's glyphs that move something, once each.
            // Font units to the asset's points (FaceInfo keeps units-per-em but doesn't show it in 6.3).
            float scale = asset.faceInfo.pointSize / (float)new SerializedObject(asset).FindProperty("m_FaceInfo.m_UnitsPerEM").intValue;
            var held = new HashSet<uint>(glyphs);
            var seen = new HashSet<(uint, uint)>();
            var kept = (records ?? new GlyphPairAdjustmentRecord[0])
                .Where(r => held.Contains(r.firstAdjustmentRecord.glyphIndex) && held.Contains(r.secondAdjustmentRecord.glyphIndex))
                .Where(r => r.firstAdjustmentRecord.glyphValueRecord.xAdvance != 0 || r.firstAdjustmentRecord.glyphValueRecord.xPlacement != 0
                            || r.secondAdjustmentRecord.glyphValueRecord.xPlacement != 0)
                .Where(r => seen.Add((r.firstAdjustmentRecord.glyphIndex, r.secondAdjustmentRecord.glyphIndex)))
                .Select(r => Scaled(r, scale)).ToList();
            list.Clear();
            list.AddRange(kept);
            table.SortGlyphPairAdjustmentRecords();
            Debug.Log($"[FontAssets] {asset.name}: {kept.Count} kerning pairs among {glyphs.Length} glyphs.");
        }

        /// <summary>The font engine reads GPOS values in font units; the asset lays text out in its own point size.</summary>
        static GlyphPairAdjustmentRecord Scaled(GlyphPairAdjustmentRecord r, float scale)
        {
            GlyphAdjustmentRecord One(GlyphAdjustmentRecord a)
            {
                var v = a.glyphValueRecord;
                return new GlyphAdjustmentRecord(a.glyphIndex, new GlyphValueRecord(v.xPlacement * scale, v.yPlacement * scale, v.xAdvance * scale, v.yAdvance * scale));
            }
            return new GlyphPairAdjustmentRecord(One(r.firstAdjustmentRecord), One(r.secondAdjustmentRecord));
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
