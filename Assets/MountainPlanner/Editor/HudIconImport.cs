using Unity.VectorGraphics;
using Unity.VectorGraphics.Editor;
using UnityEditor;

namespace MountainPlanner.Editor
{
    /// <summary>
    /// The HUD's symbols (task P2-02, from the mockup by tools/ui-parity/extract-icons.mjs) import as UI Toolkit vector
    /// images that keep their whole 24 px viewport, so a symbol's layers stack exactly (HudIcon). The brand's marks and
    /// lockups (task P2-03.1, written by tools/brand/export.py) import the same way, so USS can size them by their
    /// viewBox and tint them.
    /// </summary>
    sealed class HudIconImport : AssetPostprocessor
    {
        static readonly string[] Folders = { "Assets/MountainPlanner/Art/UI/Icons/", "Assets/MountainPlanner/Art/UI/Brand/" };

        void OnPreprocessAsset()
        {
            if (!(assetImporter is SVGImporter svg) || !System.Array.Exists(Folders, f => assetPath.StartsWith(f))) return;
            svg.SvgType = SVGType.VectorImage;
            svg.ViewportOptions = ViewportOptions.PreserveViewport;
        }
    }
}
