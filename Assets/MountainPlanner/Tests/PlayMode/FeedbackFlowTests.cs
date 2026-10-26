using System;
using System.Collections;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Diagnostics;
using MountainPlanner.App.Flow;
using MountainPlanner.Persistence;
using MountainPlanner.Persistence.Reports;
using MountainPlanner.UI;
using MountainPlanner.UI.Flow;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P3-14 in the flow: Send feedback opens from the error notice and Settings › Data › Logs, lists what the report
    /// holds, switches between Problem and Suggestion, waits for a title, writes the zip into the library's Reports
    /// folder and shows Report saved; from Settings it opens over Settings and closing it leaves Settings. 50% and 150%.
    /// </summary>
    public sealed class FeedbackFlowTests
    {
        string _root, _logs;
        int _scale;

        sealed class NoDownloads : ISiteDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> p, CancellationToken ct) =>
                throw new AssertionException("Nothing here downloads.");
        }

        [SetUp]
        public void Scratch()
        {
            _root = Path.Combine(Path.GetTempPath(), "mp-feedback-flow-" + Guid.NewGuid().ToString("N"));
            _logs = Path.Combine(_root, "Logs");
            Directory.CreateDirectory(_logs);
            File.WriteAllText(Path.Combine(_logs, "errors-20261024-090000.log"), "Game v0.3.0\r\nNullReferenceException\r\n");
            Http.NetworkDisabled = true;
            AppFlow.OpenReportsExternally = false;
            _scale = UiPreferences.ScalePercent;
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
        }

        [TearDown]
        public void Clean()
        {
            ErrorReporter.Uninstall();
            LogAssert.ignoreFailingMessages = false;
            var pump = Object.FindAnyObjectByType<ErrorReporter>();
            if (pump != null) Object.DestroyImmediate(pump.gameObject);
            if (AppFlow.Instance != null) Object.DestroyImmediate(AppFlow.Instance.gameObject);
            Http.NetworkDisabled = false;
            AppFlow.OpenReportsExternally = true;
            UiPreferences.SetScale(_scale, remember: false);
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        IEnumerator Open(AppFlow flow, string kind)
        {
            flow.OpenFeedback(kind, false);
            for (int i = 0; i < 10 && !flow.Screens.FeedbackOpen; i++) yield return null;
            Assert.That(flow.Screens.FeedbackOpen, Is.True, "Send feedback opened");
            yield return Frames(3);
        }

        [UnityTest]
        public IEnumerator SendLogsSavesAReportWithWhatIsTicked()
        {
            ErrorReporter.Install(_logs);
            ErrorReporter.CreatePump();
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return null;
            var ui = flow.Screens.Document.rootVisualElement;
            Assert.That(ui.Q<Button>("error-notice-send"), Is.Not.Null, "the error notice has Send logs");

            yield return Open(flow, ReportKind.Problem);
            var screens = flow.Screens;
            Assert.That(screens.FeedbackKind, Is.EqualTo(ReportKind.Problem));
            var parts = screens.FeedbackParts.Select(p => p.Part).ToList();
            CollectionAssert.Contains(parts, ReportPart.ErrorLogs);
            CollectionAssert.Contains(parts, ReportPart.System);
            Assert.That(screens.FeedbackParts.All(p => p.On), Is.True, "a problem starts with everything ticked");
            Assert.That(ui.Q<Button>("feedback-send").enabledSelf, Is.False, "Send waits for a title");

            ClickButton(ui.Q<Button>("feedback-kind-suggestion"));
            yield return Frames(2);
            Assert.That(screens.FeedbackKind, Is.EqualTo(ReportKind.Suggestion));
            Assert.That(screens.FeedbackParts.Single(p => p.Part == ReportPart.ErrorLogs).On, Is.False, "a suggestion leaves the logs out");
            ClickButton(ui.Q<Button>("feedback-kind-problem"));
            yield return Frames(2);
            screens.ToggleFeedbackPart(ReportPart.System);   // leave the system out
            screens.SetFeedbackText("Trees vanish", "At the second zoom step.");
            yield return null;
            Assert.That(ui.Q<Button>("feedback-send").enabledSelf, Is.True);

            ClickButton(ui.Q<Button>("feedback-save"));
            for (float until = Time.realtimeSinceStartup + 30; Time.realtimeSinceStartup < until && !screens.FeedbackSaved;) yield return null;
            Assert.That(screens.FeedbackSaved, Is.True, "Report saved shows");
            string zip = flow.LastReportPath;
            Assert.That(Path.GetDirectoryName(zip), Is.EqualTo(ReportBundle.FolderIn(_root)), "in the library's Reports folder");
            var report = ReportBundle.Read(zip);
            Assert.That(report.Title, Is.EqualTo("Trees vanish"));
            Assert.That(report.DisplayVersion, Is.EqualTo(BuildInfo.Current.DisplayVersion));
            Assert.That(report.System, Is.Empty, "System and settings was left out");
            Assert.That(report.Files.Any(f => f.Part == ReportPart.ErrorLogs), Is.True);
            StringAssert.Contains(Path.GetFileName(zip), ui.Q<Label>("feedback-file").text);

            ClickButton(ui.Q<Button>("feedback-done-ok"));
            yield return null;
            Assert.That(screens.FeedbackOpen, Is.False);
        }

        [UnityTest]
        public IEnumerator SendLogsInSettingsOpensFeedbackOverIt()
        {
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return null;
            flow.Screens.HideCover();   // no scene here: the loading cover would keep the keys
            flow.Screens.ShowSettings();
            flow.Screens.Settings.ShowPage(SettingsWindow.DataPage);
            yield return Frames(3);
            var ui = flow.Screens.Document.rootVisualElement;
            Assert.That(ui.Q<Button>("logs-send"), Is.Not.Null, "Settings › Data › Logs › Send logs…");
            flow.Screens.ShowCrashOffer(true);   // the title's offer is its own notice, not the Settings switch of the same idea
            Assert.That(ui.Q("crash-notice").ClassListContains("hidden"), Is.False, "the crash offer shows");
            Assert.That(ui.Q("crash-notice").Q<Button>("crash-notice-send"), Is.Not.Null);
            flow.Screens.ShowCrashOffer(false);
            bool chosen = false;
            flow.Screens.Settings.LogsSendChosen += () => chosen = true;
            // Enter on the focused button, as Settings is used by keyboard (its scroll view keeps a synthetic pointer's clicks).
            var send = ui.Q<Button>("logs-send");
            send.Focus();
            yield return Frames(2);
            using (var e = NavigationSubmitEvent.GetPooled()) { e.target = send; send.SendEvent(e); }
            for (float until = Time.realtimeSinceStartup + 5; Time.realtimeSinceStartup < until && !flow.Screens.FeedbackOpen;) yield return null;
            Assert.That(chosen, Is.True, $"Enter reached Send logs… (cover {flow.Screens.CoverUp})");
            Assert.That(flow.Screens.FeedbackOpen, Is.True, "Send logs opens Send feedback");
            Assert.That(flow.Screens.FeedbackKind, Is.EqualTo(ReportKind.Problem));
            Assert.That(flow.Screens.OverlayOpen, Is.True, "over Settings");
            flow.Screens.CloseFeedback();
            Assert.That(flow.Screens.OverlayOpen, Is.True, "Settings stays");
        }

        [UnityTest]
        public IEnumerator TheKeyboardReachesEveryControlAt50And150Percent([Values(50, 150)] int scale)
        {
            UiPreferences.SetScale(scale, remember: false);
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return null;
            yield return Open(flow, ReportKind.Problem);
            yield return Frames(5);
            var ui = flow.Screens.Document.rootVisualElement;
            var panel = ui.Q("feedback-panel");
            var focused = ui.focusController.focusedElement as VisualElement;
            Assert.That(focused != null && (focused == ui.Q<TextField>("feedback-summary") || ui.Q<TextField>("feedback-summary").Contains(focused)), Is.True,
                $"the title field has the keyboard (not {focused?.name})");
            var root = ui.panel.visualTree.worldBound;
            Assert.That(root.Contains(panel.worldBound.min) && root.Contains(panel.worldBound.max - Vector2.one), Is.True,
                $"the window fits the screen at {scale}% ({panel.worldBound} in {root})");
            foreach (var row in panel.Query(className: "feedback-part").ToList()) Assert.That(row.focusable, Is.True, $"{row.name} takes the keyboard");
        }

        /// <summary>A left click on the button, as the pointer makes it (ReleaseFlowTests).</summary>
        static void ClickButton(Button b)
        {
            Assert.That(b, Is.Not.Null);
            Vector2 at = b.worldBound.center;
            using (var down = PointerDownEvent.GetPooled(new Event { type = EventType.MouseDown, mousePosition = at, button = 0, clickCount = 1 })) b.SendEvent(down);
            using (var up = PointerUpEvent.GetPooled(new Event { type = EventType.MouseUp, mousePosition = at, button = 0, clickCount = 1 })) b.SendEvent(up);
        }

        /// <summary>At least <paramref name="n"/> frames and a tenth of a second, so layout and focus settle (UiKeyboardTests).</summary>
        static IEnumerator Frames(int n)
        {
            float until = Time.realtimeSinceStartup + 0.1f;
            for (int i = 0; i < n || Time.realtimeSinceStartup < until; i++) yield return null;
        }
    }
}
