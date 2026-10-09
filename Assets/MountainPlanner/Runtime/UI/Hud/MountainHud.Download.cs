using System;
using UnityEngine.UIElements;

namespace MountainPlanner.UI
{
    /// <summary>
    /// A background download in the status bar (task P2-06, owner D3; the mockup's demo=dl, dlwait, dlfail): what it's
    /// doing, the area and its share, and a hairline of progress. Only there while a download runs, waits or has stopped;
    /// clicking it shows the download card. The app flow drives it, at most when its view model changes.
    /// </summary>
    public sealed partial class MountainHud
    {
        /// <summary>The bar's download was clicked: show the download card.</summary>
        public event Action DownloadChosen;

        VisualElement _dlCell, _dlFill;
        Button _dlButton;
        Label _dlLabel, _dlText;

        /// <summary>Shows the download (<paramref name="label"/> null hides it). <paramref name="stateClass"/>: "" running, else pill--waiting, --paused or --failed.</summary>
        public void SetDownload(string label, string text, float fraction, string stateClass)
        {
            if (_dlCell == null)
            {
                if (_root == null) return;
                _dlCell = _root.Q("bar-download-cell");
                _dlButton = _root.Q<Button>("bar-download");
                _dlLabel = _root.Q<Label>("bar-download-label");
                _dlText = _root.Q<Label>("bar-download-text");
                _dlFill = _root.Q("bar-download-fill");
                if (_dlCell == null) return;
                _dlButton.clicked += () => DownloadChosen?.Invoke();
            }
            _dlCell.EnableInClassList("hidden", label == null);
            if (label == null) return;
            if (_dlLabel.text != label) _dlLabel.text = label;
            if (_dlText.text != text) _dlText.text = text;
            _dlFill.style.width = UnityEngine.UIElements.Length.Percent(fraction * 100f);
            _dlButton.EnableInClassList("dl--wait", stateClass == "pill--waiting" || stateClass == "pill--paused");
            _dlButton.EnableInClassList("dl--fail", stateClass == "pill--failed");
        }
    }
}
