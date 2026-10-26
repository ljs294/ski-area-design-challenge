using System;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// The menu's Send feedback (task P3-14; the accepted mockup's menu, under Credits). Hidden until the app flow calls
    /// <see cref="ShowFeedback"/>, so a capture or benchmark run without the flow has no dialog to open.
    /// </summary>
    public sealed partial class MountainHud
    {
        /// <summary>The menu's Send feedback was clicked.</summary>
        public event Action FeedbackChosen;

        Button _feedbackRow;

        public void ShowFeedback(bool available)
        {
            if (_root == null) return;
            if (_feedbackRow == null)
            {
                _feedbackRow = _root.Q<Button>("menu-feedback");
                if (_feedbackRow == null) return;
                _feedbackRow.clicked += () => { SetDrop(Drop.None); FeedbackChosen?.Invoke(); };
            }
            _feedbackRow.EnableInClassList("hidden", !available);
        }
    }
}
