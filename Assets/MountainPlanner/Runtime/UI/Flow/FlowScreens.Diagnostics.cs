using System;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// Task P2-10: the game's version, bottom left on the title (owner, 2026-10-12), and the notice the session's first
    /// error shows: a log was saved, with Send logs (task P3-14) and a button that opens its folder. The notice shows once per session (the app
    /// flow decides), stays <see cref="ErrorNoticeSeconds"/> and never takes the keyboard, so play goes on.
    /// </summary>
    public sealed partial class FlowScreens
    {
        public const float ErrorNoticeSeconds = 12;

        Label _titleVersion;
        VisualElement _errorNotice;
        float _errorNoticeUntil;

        /// <summary>The error notice's Open folder, or Settings › Data › Logs › Open.</summary>
        public event Action ErrorLogsOpenChosen;

        /// <summary>True while the first error's notice shows (tests).</summary>
        public bool ErrorNoticeShown => IsShown(_errorNotice);

        void WireDiagnostics()
        {
            _titleVersion = _root.Q<Label>("title-version");
            _errorNotice = _root.Q("error-notice");
            _root.Q<Button>("error-notice-open").clicked += () =>
            {
                HideErrorNotice();
                ErrorLogsOpenChosen?.Invoke();
            };
            _root.Q<Button>("error-notice-x").clicked += HideErrorNotice;
            Settings.LogsOpenChosen += () => ErrorLogsOpenChosen?.Invoke();
            WireReport();   // Send feedback, the notice's Send logs and the crash offer (FlowScreens.Report.cs, task P3-14)
        }

        /// <summary>The version under the title's signs: "v0.2.0".</summary>
        public void SetVersion(string text) => SetText(_titleVersion, text ?? "");

        public string VersionText => _titleVersion?.text ?? "";

        /// <summary>Shows the first error's notice over everything, the cover included.</summary>
        public void ShowErrorNotice()
        {
            if (_errorNotice == null) return;
            _errorNotice.BringToFront();
            Show(_errorNotice, true);
            _errorNoticeUntil = Time.unscaledTime + ErrorNoticeSeconds;
        }

        void HideErrorNotice()
        {
            _errorNoticeUntil = 0;
            Show(_errorNotice, false);
        }

        void UpdateDiagnostics()
        {
            if (_errorNoticeUntil > 0 && Time.unscaledTime > _errorNoticeUntil) HideErrorNotice();
        }
    }
}
