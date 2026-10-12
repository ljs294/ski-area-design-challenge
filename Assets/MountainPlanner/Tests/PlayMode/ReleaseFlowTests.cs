using System;
using System.Collections;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.App;
using MountainPlanner.App.Diagnostics;
using MountainPlanner.App.Flow;
using MountainPlanner.Persistence;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
using UnityEngine.UIElements;
using Object = UnityEngine.Object;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task P2-10 in the flow: the title shows the version bottom left, Credits shows it, and the session's first error
    /// shows the "a log was saved" notice once, with Open folder; Settings › Data has the Logs row.
    /// </summary>
    public sealed class ReleaseFlowTests
    {
        string _root, _logs;

        sealed class NoDownloads : ISiteDownloader
        {
            public Task<string> DownloadAsync(PendingDownload d, string build, IProgress<DownloadStatus> p, CancellationToken ct) =>
                throw new AssertionException("Nothing here downloads.");
        }

        [SetUp]
        public void Scratch()
        {
            _root = Path.Combine(Path.GetTempPath(), "mp-release-flow-" + Guid.NewGuid().ToString("N"));
            _logs = Path.Combine(_root, "Logs");
            Http.NetworkDisabled = true;
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
            MountainViewer.TitleMode = false;
            MountainViewer.RequestedPackage = null;
            if (Directory.Exists(_root)) Directory.Delete(_root, true);
        }

        [UnityTest]
        public IEnumerator TheTitleAndCreditsShowTheVersion()
        {
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return null;
            var ui = flow.Screens.Document.rootVisualElement;
            var version = ui.Q<Label>("title-version");
            Assert.That(version.text, Is.EqualTo(BuildInfo.Current.TitleVersion));
            Assert.That(version.parent.name, Is.EqualTo("title-stage"), "under the title, in its 16:9 column");
            flow.Screens.ShowCredits(new UI.Flow.CreditsPage { Version = "Version 0.2.0" });
            yield return null;
            Assert.That(ui.Q<Label>(className: "credits-version")?.text, Is.EqualTo("Version 0.2.0"));
            Assert.That(ui.Q<Button>("logs-open"), Is.Not.Null, "Settings › Data › Logs › Open");
            Assert.That(ui.Q<Button>("logs-copy"), Is.Not.Null, "Settings › Data › Logs › Copy report");
        }

        [UnityTest]
        public IEnumerator TheFirstErrorShowsTheNoticeOnce()
        {
            ErrorReporter.Install(_logs);
            ErrorReporter.CreatePump();
            var flow = AppFlow.Create(_root, new NoDownloads());
            yield return null;
            Assert.That(flow.Screens.ErrorNoticeShown, Is.False);

            LogAssert.ignoreFailingMessages = true;
            Debug.LogException(new InvalidOperationException(ErrorReporter.ForcedMessage));
            yield return null;
            yield return null;
            Assert.That(flow.Screens.ErrorNoticeShown, Is.True, "the first error shows the notice");
            Assert.That(File.Exists(ErrorReporter.LogPath), Is.True);
            StringAssert.Contains(ErrorReporter.ForcedMessage, ErrorLog.ReadShared(ErrorReporter.LogPath));

            var ui = flow.Screens.Document.rootVisualElement;
            var close = ui.Q<Button>("error-notice-x");
            using (var click = NavigationSubmitEvent.GetPooled())
            {
                click.target = close;
                close.SendEvent(click);
            }
            yield return null;
            Assert.That(flow.Screens.ErrorNoticeShown, Is.False, "closed");
            Debug.LogError("a second error");
            yield return null;
            yield return null;
            Assert.That(flow.Screens.ErrorNoticeShown, Is.False, "once per session");
            Assert.That(ErrorReporter.Count, Is.EqualTo(2));
        }
    }
}
