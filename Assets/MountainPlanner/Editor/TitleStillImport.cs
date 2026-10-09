using UnityEditor;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// The loading screen's picture (task P2-03.1, TitleStill.jpg from -titlestill) imports at full size: Unity's defaults
    /// would quietly shrink it to 2048 wide and stretch it to a power of two. Kept sharp on 4K and 32:9 screens, in sRGB,
    /// clamped, BC7-compressed (16.6 MB) and without alpha. No mipmaps: it's drawn at about screen size, and with them
    /// Unity stored it uncompressed (88 MB), since the lower levels aren't multiples of 4.
    /// </summary>
    sealed class TitleStillImport : AssetPostprocessor
    {
        public const string Path = "Assets/MountainPlanner/Art/UI/Flow/Resources/MountainPlannerFlow/TitleStill.jpg";

        void OnPreprocessTexture()
        {
            if (assetPath != Path) return;
            var t = (TextureImporter)assetImporter;
            t.textureType = TextureImporterType.Default;
            t.npotScale = TextureImporterNPOTScale.None;
            t.sRGBTexture = true;
            t.alphaSource = TextureImporterAlphaSource.None;
            t.wrapMode = UnityEngine.TextureWrapMode.Clamp;
            t.mipmapEnabled = false;   // drawn at about screen size; odd-sized lower mip levels kept it from compressing
            t.maxTextureSize = 8192;
            t.textureCompression = TextureImporterCompression.CompressedHQ;   // BC7 on desktop
            t.ClearPlatformTextureSettings("Standalone");
        }

        /// <summary>Bumped when these settings change, so the picture is imported again.</summary>
        public override uint GetVersion() => 3;
    }
}
