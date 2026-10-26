using System;
using System.IO;
using MountainPlanner.App.Diagnostics;
using MountainPlanner.Persistence;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Task P2-10: the version under the title and in Credits; the session's first error shows the notice once (owner,
    /// 2026-10-12) with Open folder; Settings › Data › Logs opens the folder, or Send logs (task P3-14).
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>Seconds from launch until the title's mountain was fully in; -1 until then (the smoke test waits on it).</summary>
        public static float TitleReadySeconds { get; private set; } = -1;
        /// <summary>True when that mountain was the demo built into the game (StreamingAssets), as a fresh install shows.</summary>
        public static bool TitleFromBundledDemo { get; private set; }

        bool _errorNoticeShown;

        void WireDiagnostics()
        {
            Screens.SetVersion(BuildInfo.Current.TitleVersion);
            Screens.ErrorLogsOpenChosen += OpenErrorLogs;
            WireReport();   // Send feedback and the crash marker (AppFlow.Report.cs, task P3-14)
            ErrorReporter.FirstError += OnFirstError;
            if (ErrorReporter.LogPath != null) OnFirstError(ErrorReporter.LogPath);   // an error before the flow existed
        }

        void ReleaseDiagnostics() => ErrorReporter.FirstError -= OnFirstError;

        void OnFirstError(string path)
        {
            if (_errorNoticeShown) return;
            _errorNoticeShown = true;
            Screens.ShowErrorNotice();
        }

        static string ErrorLogFolder => ErrorReporter.Folder ?? ErrorLog.FolderIn(MountainViewer.DataRoot);

        void OpenErrorLogs()
        {
            Directory.CreateDirectory(ErrorLogFolder);
            Application.OpenURL(new Uri(ErrorLogFolder).AbsoluteUri);
        }

        static void NoteTitleReady(float seconds, bool bundled)
        {
            TitleReadySeconds = seconds;
            TitleFromBundledDemo = bundled;
        }
    }
}
