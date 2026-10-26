using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.UI.Flow
{
    /// <summary>One row of Send feedback's "Included in the report": a part the player can untick, with its size.</summary>
    public sealed class FeedbackPart
    {
        public string Part = "", Name = "", Sub = "";
        public long Bytes;
        public bool On;
        /// <summary>Ticked again when the kind switches to Problem, unticked for Suggestion (the logs and the save).</summary>
        public bool ProblemOnly;
    }

    /// <summary>What the player asked for when they pressed Save and open GitHub, or Save only.</summary>
    public sealed class FeedbackRequest
    {
        public string Kind = "problem", Title = "", Note = "";
        public HashSet<string> Parts = new HashSet<string>();
        public bool OpenGitHub;
    }

    /// <summary>
    /// Task P3-14 (the accepted mockup's #demo=fb, fbsug, fbcrash, fbsent): Send feedback. Problem or Suggestion, a title
    /// (Send waits for one), the player's note, and every part of the report with its size, each of which can be left out;
    /// the total against GitHub's limit and a line on privacy. Report saved then names the zip and what to do with it.
    /// Also the error notice's Send logs and the title's once-per-crash offer. Nothing here writes or sends anything: the
    /// app flow does, through the events.
    /// </summary>
    public sealed partial class FlowScreens
    {
        /// <summary>The largest report GitHub takes as an issue attachment.</summary>
        public const long FeedbackLimit = 25L * 1024 * 1024;

        public event Action<FeedbackRequest> FeedbackSendChosen;
        public event Action FeedbackFolderChosen, FeedbackCopyChosen;
        /// <summary>The error notice's Send logs, or Settings › Data › Logs › Send logs….</summary>
        public event Action ErrorLogsSendChosen;
        /// <summary>The crash offer's Send report, and its Not now.</summary>
        public event Action CrashReportChosen, CrashOfferDismissed;

        VisualElement _feedback, _feedbackForm, _feedbackDone, _feedbackParts, _feedbackTotal, _crashOffer;
        Label _feedbackHead, _feedbackSummaryCap, _feedbackNoteCap, _feedbackSize, _feedbackFile, _feedbackFileSize, _feedbackStep1, _feedbackStep2;
        TextField _feedbackSummary, _feedbackNote;
        Button _feedbackProblem, _feedbackSuggestion, _feedbackSend, _feedbackSave, _feedbackDoneOk;
        readonly List<FeedbackPart> _parts = new List<FeedbackPart>();
        string _feedbackKind = "problem";

        public bool FeedbackOpen => IsShown(_feedback);
        /// <summary>True while Report saved shows (tests and captures).</summary>
        public bool FeedbackSaved => FeedbackOpen && IsShown(_feedbackDone);
        public bool CrashOfferShown => IsShown(_crashOffer);
        public string FeedbackKind => _feedbackKind;
        public IReadOnlyList<FeedbackPart> FeedbackParts => _parts;

        void WireReport()
        {
            _feedback = _root.Q("feedback");
            _feedbackForm = _root.Q("feedback-form");
            _feedbackDone = _root.Q("feedback-done");
            _feedbackParts = _root.Q("feedback-parts");
            _feedbackTotal = _root.Q("feedback-total");
            _feedbackHead = _root.Q<Label>("feedback-head");
            _feedbackSummaryCap = _root.Q<Label>("feedback-summary-cap");
            _feedbackNoteCap = _root.Q<Label>("feedback-note-cap");
            _feedbackSize = _root.Q<Label>("feedback-size");
            _feedbackFile = _root.Q<Label>("feedback-file");
            _feedbackFileSize = _root.Q<Label>("feedback-file-size");
            _feedbackStep1 = _root.Q<Label>("feedback-step1");
            _feedbackStep2 = _root.Q<Label>("feedback-step2");
            _feedbackSummary = _root.Q<TextField>("feedback-summary");
            _feedbackNote = _root.Q<TextField>("feedback-note");
            _feedbackProblem = _root.Q<Button>("feedback-kind-problem");
            _feedbackSuggestion = _root.Q<Button>("feedback-kind-suggestion");
            _feedbackSend = _root.Q<Button>("feedback-send");
            _feedbackSave = _root.Q<Button>("feedback-save");
            _feedbackDoneOk = _root.Q<Button>("feedback-done-ok");
            _crashOffer = _root.Q("crash-offer");
            if (_feedback == null) return;

            _feedbackProblem.clicked += () => SetFeedbackKind("problem");
            _feedbackSuggestion.clicked += () => SetFeedbackKind("suggestion");
            _feedbackSend.clicked += () => SubmitFeedback(true);
            _feedbackSave.clicked += () => SubmitFeedback(false);
            _root.Q<Button>("feedback-cancel").clicked += CloseFeedback;
            _root.Q<Button>("feedback-x").clicked += CloseFeedback;
            _feedbackDoneOk.clicked += CloseFeedback;
            _root.Q<Button>("feedback-folder").clicked += () => FeedbackFolderChosen?.Invoke();
            _root.Q<Button>("feedback-copy").clicked += () => FeedbackCopyChosen?.Invoke();
            _feedbackSummary.RegisterValueChangedCallback(_ => RefreshFeedback());
            // Enter in the title sends, as the mockup's go button says; the note keeps Enter for new lines.
            _feedbackSummary.RegisterCallback<KeyDownEvent>(e =>
            {
                if (e.keyCode != KeyCode.Return && e.keyCode != KeyCode.KeypadEnter) return;
                e.StopPropagation();
                SubmitFeedback(true);
            }, TrickleDown.TrickleDown);

            _root.Q<Button>("error-notice-send").clicked += () =>
            {
                HideErrorNotice();
                ErrorLogsSendChosen?.Invoke();
            };
            _root.Q<Button>("crash-offer-send").clicked += () =>
            {
                Show(_crashOffer, false);
                CrashReportChosen?.Invoke();
            };
            _root.Q<Button>("crash-offer-later").clicked += () =>
            {
                Show(_crashOffer, false);
                CrashOfferDismissed?.Invoke();
            };
            Settings.LogsSendChosen += () => ErrorLogsSendChosen?.Invoke();
        }

        /// <summary>Opens Send feedback on <paramref name="kind"/> with the report's parts (ticked as they come).</summary>
        public void ShowFeedback(string kind, IEnumerable<FeedbackPart> parts)
        {
            if (_feedback == null) return;
            _parts.Clear();
            _parts.AddRange(parts);
            _feedbackSummary.SetValueWithoutNotify("");
            _feedbackNote.SetValueWithoutNotify("");
            Show(_feedbackForm, true);
            Show(_feedbackDone, false);
            SetText(_feedbackHead, "Send feedback");
            SetFeedbackKind(kind ?? "problem", keepTicks: true);
            _feedback.BringToFront();
            Show(_feedback, true);
            UiFocus.OpenModal(_feedback, _feedbackSummary);
        }

        /// <summary>Report saved: the zip's name and size, and what to do with it.</summary>
        public void ShowFeedbackSaved(string fileName, long bytes, bool openedGitHub)
        {
            if (_feedback == null) return;
            SetText(_feedbackHead, "Report saved");
            SetText(_feedbackFile, fileName);
            SetText(_feedbackFileSize, LibraryViewModel.Disk(bytes));
            SetText(_feedbackStep1, openedGitHub
                ? "1. It's open in a folder window, and your browser has a new GitHub issue with the title and version filled in."
                : "1. It's open in a folder window.");
            SetText(_feedbackStep2, openedGitHub
                ? "2. <b>Drag the file into the issue</b>, then click <b>Submit new issue</b>."
                : "2. Attach it to a GitHub issue or a message to the developer.");
            Show(_feedbackForm, false);
            Show(_feedbackDone, true);
            UiFocus.OpenModal(_feedback, _feedbackDoneOk);
        }

        public void CloseFeedback()
        {
            if (!FeedbackOpen) return;
            Show(_feedback, false);
            UiFocus.CloseModal(_feedback);
        }

        /// <summary>The title's offer after a crash (once per crash; the app flow decides).</summary>
        public void ShowCrashOffer(bool show)
        {
            if (_crashOffer == null) return;
            if (show) _crashOffer.BringToFront();
            Show(_crashOffer, show);
        }

        /// <summary>The player's title and note (tests type through these).</summary>
        public void SetFeedbackText(string title, string note)
        {
            _feedbackSummary.value = title ?? "";
            _feedbackNote.value = note ?? "";
        }

        /// <summary>Ticks or unticks a part, as a click on its row does (tests).</summary>
        public void ToggleFeedbackPart(string part)
        {
            var p = _parts.FirstOrDefault(x => x.Part == part);
            if (p == null) return;
            p.On = !p.On;
            BuildFeedbackParts();
        }

        void SetFeedbackKind(string kind, bool keepTicks = false)
        {
            bool problem = kind != "suggestion";
            _feedbackKind = problem ? "problem" : "suggestion";
            if (!keepTicks) foreach (var p in _parts) if (p.ProblemOnly) p.On = problem;
            Mark(_feedbackProblem, problem);
            Mark(_feedbackSuggestion, !problem);
            SetText(_feedbackSummaryCap, problem ? "In a few words" : "Your idea in a few words");
            SetText(_feedbackNoteCap, problem ? "What happened, and what were you doing?" : "Tell us more (optional)");
            BuildFeedbackParts();
        }

        void BuildFeedbackParts()
        {
            _feedbackParts.Clear();
            for (int i = 0; i < _parts.Count; i++)
            {
                var p = _parts[i];
                var row = new VisualElement { name = "feedback-part-" + p.Part, focusable = true, tooltip = p.On ? "Leave this out" : "Include this" };
                row.AddToClassList("feedback-part");
                if (i == 0) row.AddToClassList("feedback-part--first");
                row.EnableInClassList("feedback-part--on", p.On);
                var chk = new VisualElement { pickingMode = PickingMode.Ignore };
                chk.AddToClassList("feedback-part__chk");
                var tick = new VisualElement { pickingMode = PickingMode.Ignore };
                tick.AddToClassList("feedback-part__tick");
                chk.Add(tick);
                row.Add(chk);
                var text = new VisualElement { pickingMode = PickingMode.Ignore };
                text.AddToClassList("feedback-part__text");
                text.Add(Text(p.Name, "feedback-part__name"));
                if (p.Sub.Length > 0) text.Add(Text(p.Sub, "feedback-part__sub"));
                row.Add(text);
                row.Add(Text(LibraryViewModel.Disk(p.Bytes), "feedback-part__size", "mono"));
                string part = p.Part;
                void Toggle()
                {
                    ToggleFeedbackPart(part);
                    _feedbackParts.Q("feedback-part-" + part)?.Focus();
                }
                row.RegisterCallback<ClickEvent>(_ => Toggle());
                row.RegisterCallback<NavigationSubmitEvent>(e => { if (e.target == row) Toggle(); });
                row.RegisterCallback<KeyDownEvent>(e => { if (e.target == row && e.keyCode == KeyCode.Space) { Toggle(); e.StopPropagation(); } });
                _feedbackParts.Add(row);
            }
            RefreshFeedback();
        }

        /// <summary>The bytes the ticked parts add (report.json's few KB included).</summary>
        long FeedbackBytes => 4096 + _parts.Where(p => p.On).Sum(p => p.Bytes);

        void RefreshFeedback()
        {
            long bytes = FeedbackBytes;
            bool over = bytes > FeedbackLimit, titled = _feedbackSummary.value.Trim().Length > 0;
            SetText(_feedbackSize, $"{LibraryViewModel.Disk(bytes)} of {LibraryViewModel.Disk(FeedbackLimit)}");
            _feedbackTotal.EnableInClassList("feedback-total--over", over);
            SetText(_feedbackTotal.Q<Label>("feedback-total-text"), over ? "Too big for GitHub: leave out the save or the game log" : "Saved as one zip file on this PC");
            _feedbackSend.SetEnabled(titled && !over);
            _feedbackSave.SetEnabled(titled);
        }

        void SubmitFeedback(bool github)
        {
            string title = _feedbackSummary.value.Trim();
            if (title.Length == 0 || (github && FeedbackBytes > FeedbackLimit)) return;
            var request = new FeedbackRequest { Kind = _feedbackKind, Title = title, Note = _feedbackNote.value.Trim(), OpenGitHub = github };
            foreach (var p in _parts) if (p.On) request.Parts.Add(p.Part);
            FeedbackSendChosen?.Invoke(request);
        }
    }
}
