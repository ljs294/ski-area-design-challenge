using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// The viewer's IMGUI overlay (loading, errors, toasts, photo mode and the F1 developer panel) on a component of
    /// its own, so <see cref="MountainViewer"/> can switch it off when there's nothing to draw: Unity allocates a few
    /// hundred bytes every frame for each enabled OnGUI, even one that draws nothing, and the game must allocate
    /// nothing per frame (0.3 §8; task 15).
    /// </summary>
    public sealed class ViewerOverlay : MonoBehaviour
    {
        public MountainViewer Viewer;

        void OnGUI()
        {
            if (Viewer != null) Viewer.DrawOverlay();
        }
    }
}
