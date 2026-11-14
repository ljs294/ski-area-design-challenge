using System;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Task 4.23: the notice that a newer release is out, with Update (or Get the installer for a game unzipped by hand),
    /// What's new and Not now. Like the crash offer it never takes the keyboard; the app flow decides when it shows.
    /// Settings › Data › Version's Check now comes through here too.
    /// </summary>
    public sealed partial class FlowScreens
    {
        VisualElement _updateNotice;
        Label _updateNoticeText;
        Button _updateNoticeGo;

        /// <summary>The notice's Update (or Get the installer).</summary>
        public event Action UpdateChosen;
        /// <summary>The notice's What's new: the release page.</summary>
        public event Action UpdateNewsChosen;
        /// <summary>The notice's Not now.</summary>
        public event Action UpdateDismissed;
        /// <summary>Settings › Data › Version › Check now.</summary>
        public event Action SettingsUpdateCheckNowChosen;

        /// <summary>True while the update notice shows (tests).</summary>
        public bool UpdateNoticeShown => IsShown(_updateNotice);

        void WireUpdates()
        {
            _updateNotice = _root.Q("update-notice");
            _updateNoticeText = _root.Q<Label>("update-notice-text");
            _updateNoticeGo = _root.Q<Button>("update-notice-go");
            Settings.UpdateCheckNowChosen += () => SettingsUpdateCheckNowChosen?.Invoke();
            if (_updateNotice == null) return;
            _updateNoticeGo.clicked += () => UpdateChosen?.Invoke();
            _root.Q<Button>("update-notice-news").clicked += () => UpdateNewsChosen?.Invoke();
            _root.Q<Button>("update-notice-later").clicked += () =>
            {
                ShowUpdateNotice(null, null);
                UpdateDismissed?.Invoke();
            };
        }

        /// <summary>Shows the notice with <paramref name="text"/> and the go button's label; null hides it.</summary>
        public void ShowUpdateNotice(string text, string go)
        {
            if (_updateNotice == null) return;
            bool show = text != null;
            if (show)
            {
                _updateNoticeText.text = text;
                _updateNoticeGo.text = go ?? "Update";
                _updateNotice.BringToFront();
            }
            Show(_updateNotice, show);
        }
    }
}
