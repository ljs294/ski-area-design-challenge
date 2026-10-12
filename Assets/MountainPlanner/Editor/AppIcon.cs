using System.Linq;
using UnityEditor;
using UnityEditor.Build;
using UnityEngine;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// The game's app icon (task P2-10, decision BR5): the Twin Peaks game mark on a sign-white tile, rendered at every
    /// size by tools/brand/icon.mjs from the ink masters, each size with its own cut (the plain pair at 16 and 24, the
    /// small cut to 64, the full mark above). The builds give Windows the exact size for each slot, so nothing is
    /// rescaled; the PNGs import uncompressed, without mipmaps, at their own size.
    /// </summary>
    sealed class AppIcon : AssetPostprocessor
    {
        public const string Folder = "Assets/MountainPlanner/Art/UI/Brand/Icon/";

        void OnPreprocessTexture()
        {
            if (!assetPath.StartsWith(Folder)) return;
            var t = (TextureImporter)assetImporter;
            t.textureType = TextureImporterType.Default;
            t.npotScale = TextureImporterNPOTScale.None;
            t.mipmapEnabled = false;
            t.alphaIsTransparency = true;
            t.sRGBTexture = true;
            t.isReadable = true;
            t.textureCompression = TextureImporterCompression.Uncompressed;
            t.maxTextureSize = 2048;
        }

        static Texture2D Load(int size) => AssetDatabase.LoadAssetAtPath<Texture2D>($"{Folder}icon-{size}.png");

        /// <summary>The icon at <paramref name="size"/>, or the nearest larger one (the nearest smaller past the largest).</summary>
        static Texture2D Nearest(int size, int[] sizes)
        {
            int pick = sizes.Where(s => s >= size).DefaultIfEmpty(sizes.Max()).Min();
            return Load(pick);
        }

        /// <summary>Sets the Windows player's icons, slot by slot, and the default icon; the build calls it.</summary>
        public static void Apply()
        {
            int[] have = { 16, 24, 32, 48, 64, 128, 256, 512, 1024 };
            if (Load(256) == null)
            {
                Debug.LogWarning("[AppIcon] No icons in " + Folder + ": run node tools/brand/icon.mjs. The build keeps Unity's icon.");
                return;
            }
            var target = NamedBuildTarget.Standalone;
            foreach (IconKind kind in new[] { IconKind.Application })
            {
                int[] slots = PlayerSettings.GetIconSizes(target, kind);
                PlayerSettings.SetIcons(target, slots.Select(s => Nearest(s, have)).ToArray(), kind);
            }
            PlayerSettings.SetIcons(NamedBuildTarget.Unknown, new[] { Load(1024) }, IconKind.Any);
        }
    }
}
