using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using MountainPlanner.App.Diagnostics;
using MountainPlanner.Persistence.Reports;
using MountainPlanner.UI;
using MountainPlanner.UI.Flow;
using UnityEngine;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Task P3-14: Send feedback. The menu, the error notice, Settings › Data › Logs and the title's offer after a crash
    /// open it; the game takes the picture first, gathers the report's parts and shows them; Send writes the zip off the
    /// main thread into &lt;data&gt;/Reports, shows it in Explorer and opens the pre-filled GitHub issue (decision PR2).
    /// The game itself sends nothing. A session marks itself running, so the next launch knows if it crashed (PR7).
    /// </summary>
    public sealed partial class AppFlow
    {
        /// <summary>False in tests and captures: the zip is written, but Explorer and the browser don't open.</summary>
        public static bool OpenReportsExternally = true;

        /// <summary>The newest report this session wrote (tests and the demo).</summary>
        public string LastReportPath { get; private set; }

        CrashMarker _crashMarker;
        string _crashPending;
        bool _crashOfferDecided;
        ProblemReport _report;
        IReadOnlyList<ReportItem> _reportItems;
        bool _reportBusy, _reportTest;

        /// <summary>Runs that aren't the player's own session: a scratch library, an opened report, captures and smoke tests.</summary>
        static readonly string[] NotAPlayerSession = { "-data", "-reportsettings", "-uicapture", "-flowcapture", "-smoketest", "-titlemovie", "-displaycheck", "-reporttest" };

        /// <summary>&lt;data&gt;/Reports: the library's own, so a scratch -data run keeps its reports with it.</summary>
        string ReportsFolder => ReportBundle.FolderIn(DataRoot);

        void WireReport()
        {
            Screens.ErrorLogsSendChosen += () => OpenFeedback(ReportKind.Problem, false);
            Screens.CrashReportChosen += () => OpenFeedback(ReportKind.Problem, true);
            Screens.CrashOfferDismissed += () => _crashMarker?.Answered();
            Screens.FeedbackSendChosen += SendFeedback;
            Screens.FeedbackFolderChosen += () => Reveal(LastReportPath);
            Screens.FeedbackCopyChosen += CopyReportSummary;

            // Tool runs never get here (no flow); a scratch -data folder or an opened report isn't the player's session.
            string[] args = Environment.GetCommandLineArgs();
            _reportTest = Array.IndexOf(args, "-reporttest") >= 0;
            if (_reportTest) OpenReportsExternally = false;
            if (Application.isEditor || NotAPlayerSession.Any(a => Array.IndexOf(args, a) >= 0)) return;
            _crashMarker = new CrashMarker(MountainViewer.DataRoot);
            _crashMarker.Begin($"{BuildInfo.Current.DisplayVersion} ({BuildInfo.Current.ShortCommit}), started {DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss'Z'", CultureInfo.InvariantCulture)}");
            _crashPending = _crashMarker.PendingCrash;
            Application.quitting += _crashMarker.End;
        }

        /// <summary>The in-game menu's Send feedback (opens on Suggestion).</summary>
        void WireReportHud(MountainHud hud)
        {
            hud.ShowFeedback(true);
            hud.FeedbackChosen += () => OpenFeedback(ReportKind.Suggestion, false);
        }

        /// <summary>Once the title is up: the offer, if the last session crashed and the player wants offers.</summary>
        void UpdateCrashOffer()
        {
            if (_crashOfferDecided || TitleReadySeconds < 0 || Screens.CoverUp) return;
            _crashOfferDecided = true;
            if (_reportTest) StartCoroutine(ReportTest());
            if (_crashPending != null && DataPreferences.CrashOffer && Controller.Screen == FlowScreen.Title) Screens.ShowCrashOffer(true);
        }

        /// <summary>
        /// -reporttest: once the title is in, a Problem report with every part is written into the library's Reports
        /// folder (no Explorer, no browser), its path logged, and the game quits 0 (3 if it failed). Checks a shipped exe.
        /// </summary>
        IEnumerator ReportTest()
        {
            OpenFeedback(ReportKind.Problem, false);
            for (float until = Time.realtimeSinceStartup + 20; !Screens.FeedbackOpen && Time.realtimeSinceStartup < until;) yield return null;
            Screens.SetFeedbackText("Report test", "Written by -reporttest.");
            var request = new FeedbackRequest { Kind = ReportKind.Problem, Title = "Report test", Note = "Written by -reporttest." };
            foreach (var p in Screens.FeedbackParts) request.Parts.Add(p.Part);
            SendFeedback(request);
            for (float until = Time.realtimeSinceStartup + 60; !Screens.FeedbackSaved && Time.realtimeSinceStartup < until;) yield return null;
            Debug.Log(Screens.FeedbackSaved ? $"[Report] -reporttest wrote {LastReportPath}" : "[Report] -reporttest failed: no report was saved");
            Application.Quit(Screens.FeedbackSaved ? 0 : 3);
        }

        /// <summary>Opens Send feedback on <paramref name="kind"/>; the view is captured first, without the dialog.</summary>
        public void OpenFeedback(string kind, bool afterCrash)
        {
            if (_reportBusy || Screens.FeedbackOpen) return;
            StartCoroutine(CaptureThenOpen(kind, afterCrash));
        }

        IEnumerator CaptureThenOpen(string kind, bool afterCrash)
        {
            _reportBusy = true;
            yield return null;   // the menu or notice that was clicked closes first
            byte[] picture = null;
            if (!Application.isBatchMode)   // batch runs (tests) have no frame to end, so no picture
            {
                yield return new WaitForEndOfFrame();
                try
                {
                    var shot = ScreenCapture.CaptureScreenshotAsTexture();
                    picture = shot.EncodeToJPG(85);
                    Destroy(shot);
                }
                catch (Exception e) { Debug.LogWarning($"[Report] No screenshot: {e.Message}"); }
            }
            try
            {
                _report = ReportCollector.Report(kind, _viewer != null ? _viewer.OpenPackage : null, afterCrash);
                _reportItems = ReportBundle.Gather(ReportCollector.Sources(DataRoot, picture), DateTime.UtcNow);
                Screens.ShowFeedback(kind, Parts(_report, _reportItems));
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                Screens.Alert("The report couldn't be gathered", e.Message, null, null);
            }
            finally { _reportBusy = false; }
        }

        /// <summary>The dialog's rows: one per part that has something, in the mockup's order, with what it holds.</summary>
        static List<FeedbackPart> Parts(ProblemReport report, IReadOnlyList<ReportItem> items)
        {
            var rows = new List<FeedbackPart>();
            foreach (string part in ReportPart.All)
            {
                var of = items.Where(i => i.Part == part).ToList();
                if (of.Count == 0 && part != ReportPart.System) continue;
                var row = new FeedbackPart
                {
                    Part = part, Bytes = part == ReportPart.System ? 4096 : of.Sum(i => i.Size),
                    On = ReportPart.OnByDefault(part, report.Kind), ProblemOnly = part != ReportPart.Screenshot && part != ReportPart.System,
                };
                switch (part)
                {
                    case ReportPart.Screenshot: row.Name = "Screenshot"; row.Sub = "The view just before this window opened"; break;
                    case ReportPart.ErrorLogs: row.Name = "Error logs"; row.Sub = of.Count == 1 ? "1 log" : $"{of.Count} logs, the newest {ErrorLogDay(of[0].ZipPath)}"; break;
                    case ReportPart.GameLog: row.Name = "Game log"; row.Sub = string.Join(" and ", of.Select(i => Path.GetFileName(i.ZipPath))) + (of.Any(i => i.Cut) ? ", the end of a long one" : ""); break;
                    case ReportPart.CrashDump: row.Name = "Crash dump"; row.Sub = "Unity's record of the last crash: the game's memory at that moment"; break;
                    case ReportPart.Save:
                        row.Name = "Your save";
                        string save = report.Area?.SaveName ?? "", area = report.Area?.Name ?? "";
                        row.Sub = (save.Length > 0 ? $"“{save}” of {area}" : area) + ", read only";
                        break;
                    default:
                        row.Name = "System and settings";
                        report.System.TryGetValue(SystemKeys.Gpu, out string gpu);
                        report.System.TryGetValue(SystemKeys.Scale, out string scale);
                        report.System.TryGetValue(SystemKeys.Quality, out string quality);
                        row.Sub = $"{report.DisplayVersion} · {gpu} · {ShortWindows(report)} · {Screen.width}×{Screen.height} at {scale} · {quality}";
                        break;
                }
                rows.Add(row);
            }
            return rows;
        }

        /// <summary>"24 Oct", from errors-20261024-090000.log.</summary>
        static string ErrorLogDay(string zipPath)
        {
            string name = Path.GetFileNameWithoutExtension(zipPath);
            return name.Length >= 15 && DateTime.TryParseExact(name.Substring(7, 8), "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var day)
                ? day.ToString("d MMM", CultureInfo.InvariantCulture) : "";
        }

        static string ShortWindows(ProblemReport report) =>
            report.System.TryGetValue(SystemKeys.Windows, out string w) ? w.Split('(')[0].Trim() : "";

        async void SendFeedback(FeedbackRequest request)
        {
            if (_report == null || _reportBusy) return;
            _reportBusy = true;
            var report = _report;
            report.Kind = request.Kind;
            report.Title = request.Title;
            report.Note = request.Note;
            var items = _reportItems;
            var parts = request.Parts.ToList();
            var privacy = ReportCollector.Privacy;
            string path = Path.Combine(ReportsFolder, ReportBundle.FileNameFor(DateTime.UtcNow));
            try
            {
                long bytes = await Task.Run(() =>
                {
                    long size = ReportBundle.Write(path, report, items, parts, privacy);
                    ReportBundle.Prune(ReportsFolder);
                    return size;
                });
                LastReportPath = path;
                Debug.Log($"[Report] Saved {path} ({bytes} bytes; {report.Files.Count} files)");
                if (report.AfterCrash) _crashMarker?.Answered();
                Reveal(path);
                if (request.OpenGitHub && OpenReportsExternally) Application.OpenURL(IssueLink.For(report, Path.GetFileName(path)));
                Screens.ShowFeedbackSaved(Path.GetFileName(path), bytes, request.OpenGitHub);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException || e is InvalidDataException)
            {
                Screens.Alert("The report couldn't be saved", e.Message, null, null);
            }
            finally { _reportBusy = false; }
        }

        /// <summary>Opens Explorer with the report selected.</summary>
        static void Reveal(string path)
        {
            if (!OpenReportsExternally || string.IsNullOrEmpty(path) || !File.Exists(path)) return;
            try { System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{path}\""); }
            catch (Exception e) { Debug.LogWarning($"[Report] Explorer didn't open: {e.Message}"); }
        }

        void CopyReportSummary()
        {
            if (_report == null) return;
            GUIUtility.systemCopyBuffer = IssueLink.Summary(_report, Path.GetFileName(LastReportPath ?? ""));
            Screens.Toast("Summary copied: paste it into your message.", 4);
        }
    }
}
