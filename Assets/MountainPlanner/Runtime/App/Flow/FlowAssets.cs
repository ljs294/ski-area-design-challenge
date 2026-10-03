using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// The assets the flow builds at run time but can't load by path (task 14): the site picker's UXML and panel
    /// (task 13's, in Art/UI). Loaded from Resources/MountainPlannerFlow/FlowAssets, so builds keep them.
    /// </summary>
    public sealed class FlowAssets : ScriptableObject
    {
        public VisualTreeAsset PickerTree;
        public PanelSettings PickerPanel;
    }
}
