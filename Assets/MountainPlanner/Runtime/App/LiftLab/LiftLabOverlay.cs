using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Draws the Lift Lab's text overlay. It is its own component so the benchmark can switch it off: an
    /// enabled OnGUI allocates every frame, which would spoil the per-frame GC measurement.
    /// </summary>
    public sealed class LiftLabOverlay : MonoBehaviour
    {
        public LiftLab Lab;
        GUIStyle _style;

        void OnGUI()
        {
            if (Lab == null) return;
            _style ??= new GUIStyle(GUI.skin.box) { alignment = TextAnchor.UpperLeft, fontSize = 15, wordWrap = true, normal = { textColor = Color.white } };
            GUI.backgroundColor = new Color(0f, 0f, 0f, 2f);
            GUI.Box(new Rect(20, 20, 900, Lab.Help ? 150 : 96), Lab.OverlayText, _style);
            GUI.backgroundColor = Color.white;
        }
    }
}
