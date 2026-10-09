using UnityEditor;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// The loading screen's picture (task P2-03.1, TitleStill.jpg from -titlestill) imports at full size: Unity's defaults
    /// would quietly shrink it to 2048 wide and stretch it to a power of two. Kept sharp on 4K and 32:9 screens, in sRGB,
    /// clamped, with mipmaps for smaller screens, BC7-compressed and without alpha.
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
            t.mipmapEnabled = true;
            t.maxTextureSize = 8192;
            t.SetPlatformTextureSettings(new TextureImporterPlatformSettings
            {
                name = "Standalone", overridden = true, maxTextureSize = 8192, format = TextureImporterFormat.BC7,
            });
        }
    }
}
