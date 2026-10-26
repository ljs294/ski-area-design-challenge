using System.Collections;
using System.Collections.Generic;
using MountainPlanner.Persistence.Reports;
using MountainPlanner.UI.Flow;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// -uicapture's task P3-14 states (the accepted mockup's fb, fbsug, fbcrash, fbsent, errnote and crashoffer): Send
    /// feedback on Problem with a title and a note, on Suggestion, after a crash, Report saved, the error notice with
    /// Send logs, and the title's crash offer. The parts are a fixed set, so every capture shows the same rows.
    /// </summary>
    public sealed partial class AppFlow
    {
        static List<FeedbackPart> CaptureParts(bool crash)
        {
            var parts = new List<FeedbackPart>
            {
                new FeedbackPart { Part = ReportPart.Screenshot, Name = "Screenshot", Sub = "The view just before this window opened", Bytes = 1_270_000, On = true },
                new FeedbackPart { Part = ReportPart.ErrorLogs, Name = "Error logs", Sub = "3 logs, the newest 24 Oct", Bytes = 86_000, On = true, ProblemOnly = true },
                new FeedbackPart { Part = ReportPart.GameLog, Name = "Game log", Sub = "Player.log and Player-prev.log", Bytes = 2_210_000, On = true, ProblemOnly = true },
            };
            if (crash) parts.Add(new FeedbackPart { Part = ReportPart.CrashDump, Name = "Crash dump", Sub = "Unity's record of the last crash: the game's memory at that moment", Bytes = 625_000, On = true, ProblemOnly = true });
            parts.Add(new FeedbackPart { Part = ReportPart.Save, Name = "Your save", Sub = "“Upper bowl” of Jackson Hole, read only", Bytes = 348_000, On = true, ProblemOnly = true });
            parts.Add(new FeedbackPart { Part = ReportPart.System, Name = "System and settings", Sub = "v0.3.0 · NVIDIA GeForce RTX 3060 Ti · Windows 11 · 2560×1080 at 85% · High", Bytes = 4096, On = true });
            return parts;
        }

        IEnumerator CaptureReportStates(string folder)
        {
            if (Wanted("s12-feedback") || Wanted("s12-feedback-saved"))
            {
                Screens.ShowFeedback(ReportKind.Problem, CaptureParts(false));
                Screens.SetFeedbackText("Trees vanish when I zoom out", "Near the top of the tram, trees blink out at the second zoom step and come back after a second.");
                yield return Wait(0.3f);
                if (Wanted("s12-feedback")) yield return EachLook(folder, "s12-feedback");
                Screens.ShowFeedbackSaved("report-20261025-143210.zip", 3_880_000, true);
                yield return Wait(0.3f);
                if (Wanted("s12-feedback-saved")) yield return EachLook(folder, "s12-feedback-saved");
                Screens.CloseFeedback();
            }
            if (Wanted("s12-feedback-suggestion"))
            {
                Screens.ShowFeedback(ReportKind.Suggestion, CaptureParts(false));
                yield return Wait(0.3f);
                yield return EachLook(folder, "s12-feedback-suggestion");
                Screens.CloseFeedback();
            }
            if (Wanted("s12-feedback-crash"))
            {
                Screens.ShowFeedback(ReportKind.Problem, CaptureParts(true));
                yield return Wait(0.3f);
                yield return EachLook(folder, "s12-feedback-crash");
                Screens.CloseFeedback();
            }
            if (Wanted("s12-error-notice"))
            {
                Screens.ShowErrorNotice();
                yield return Wait(0.3f);
                yield return EachLook(folder, "s12-error-notice");
                Screens.HideErrorNoticeForCapture();
            }
            if (Wanted("s12-crash-offer"))
            {
                Screens.ShowCrashOffer(true);
                yield return Wait(0.3f);
                yield return EachLook(folder, "s12-crash-offer");
                Screens.ShowCrashOffer(false);
            }
        }
    }
}
