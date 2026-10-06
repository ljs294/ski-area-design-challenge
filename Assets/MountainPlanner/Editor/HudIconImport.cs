using Unity.VectorGraphics;
using Unity.VectorGraphics.Editor;
using UnityEditor;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// The HUD's symbols (task P2-02, from the mockup by tools/ui-parity/extract-icons.mjs) import as UI Toolkit vector
    /// images that keep their whole 24 px viewport, so a symbol's layers stack exactly (HudIcon).
    /// </summary>
    sealed class HudIconImport : AssetPostprocessor
    {
        const string Folder = "Assets/MountainPlanner/Art/UI/Icons/";

        void OnPreprocessAsset()
        {
            if (!assetPath.StartsWith(Folder) || !(assetImporter is SVGImporter svg)) return;
            svg.SvgType = SVGType.VectorImage;
            svg.ViewportOptions = ViewportOptions.PreserveViewport;
        }
    }
}
