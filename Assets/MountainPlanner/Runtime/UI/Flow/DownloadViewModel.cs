using System;
using System.Collections.Generic;
using System.Globalization;

namespace MountainPlanner.UI.Flow
{
    /// <summary>
    /// One download snapshot as the UI sees it. App copies it from the pipeline's progress (UI doesn't
    /// reference Acquisition), field for field; the stage names come from the pipeline, never from UI code.
    /// </summary>
    public sealed class DownloadStatus
    {
        public string Name = "";
        public double SizeKm;
        /// <summary>Every stage the pipeline declared, in order; may be empty, then the list grows as stages arrive.</summary>
        public IReadOnlyList<string> Stages = Array.Empty<string>();
        /// <summary>1-based index of the current stage; StageCount when finished.</summary>
        public int StageIndex;
        public int StageCount;
        public string Stage = "";
        public double Overall;
        public long Bytes;
        /// <summary>Of <see cref="Bytes"/>, how many came over the network (the rest came from the download cache).</summary>
        public long Downloaded;
        public double BytesPerSecond;
        public double? SecondsRemaining;
        public string Detail = "";
        public bool Finished;
    }

    public enum StageState { Waiting, Current, Done }

    /// <summary>Waiting: stopped by a lost connection or offline mode, and continuing by itself (task P2-06).</summary>
    public enum DownloadPhase { Running, Waiting, Paused, Failed, Finished }

    /// <summary>
    /// The S4 download card's state (0.4 S4): the stage list with ticks, the overall bar, time left, the
    /// detail line and the transfer line. It changes only when a snapshot arrives (4 a second), and the
    /// stage rows are rebuilt only when the list itself changes, so a tick is a few text sets.
    /// </summary>
    public sealed class DownloadViewModel
    {
        readonly List<string> _names = new List<string>();
        readonly List<StageState> _states = new List<StageState>();

        public string Name { get; private set; } = "";
        public DownloadPhase Phase { get; private set; } = DownloadPhase.Running;
        public IReadOnlyList<string> StageNames => _names;
        public IReadOnlyList<StageState> StageStates => _states;
        /// <summary>Bumps when stages are added or renamed: the view rebuilds its rows only then.</summary>
        public int StagesVersion { get; private set; }
        /// <summary>Bumps when any row's tick changes.</summary>
        public int StatesVersion { get; private set; }
        public float Fraction { get; private set; }
        public string Title { get; private set; } = "";
        public string Percent { get; private set; } = "0%";
        public string TimeLeft { get; private set; } = "";
        public string Detail { get; private set; } = "";
        public string Transfer { get; private set; } = "";
        /// <summary>The minimised status pill: "Downloading Crystal Mountain · 41%".</summary>
        public string Pill { get; private set; } = "";
        public string Error { get; private set; } = "";
        /// <summary>The HUD bar's two lines (task P2-06, owner D3): what it's doing, then the area and share.</summary>
        public string BarLabel =>
            Phase == DownloadPhase.Failed ? "Download stopped"
            : Phase == DownloadPhase.Paused ? "Download paused"
            : Phase == DownloadPhase.Waiting ? (Problem?.Kind == ProblemKind.OfflineMode ? "Paused · offline mode" : Problem?.Kind == ProblemKind.ServiceBusy ? "Waiting for the map service" : "Waiting for connection")
            : "Downloading";
        public string BarText => $"{Name} · {Percent}";
        /// <summary>Why it stopped or waits, in the player's words (S11); null while it runs.</summary>
        public DownloadProblem Problem { get; private set; }
        /// <summary>Bumps on every change, so the screen redraws only then (at most 4 times a second while it runs).</summary>
        public int Version { get; private set; }
        /// <summary>The stage and share a resumed download had reached: the bar holds there until the work passes it.</summary>
        double _floor;
        int _waitSeconds = -1;

        public void Start(string name, double sizeKm)
        {
            _names.Clear();
            _states.Clear();
            StagesVersion++;
            StatesVersion++;
            Name = name;
            Phase = DownloadPhase.Running;
            Error = "";
            Fraction = 0;
            Percent = "0%";
            TimeLeft = "";
            Detail = "Starting";
            Transfer = "";
            Title = $"Downloading {name} · {FlowUnits.SiteSize(sizeKm)}";
            Pill = $"Downloading {name} · 0%";
            Problem = null;
            _floor = 0;
            _waitSeconds = -1;
            Version++;
        }

        /// <summary>
        /// A paused download starting again: it shows where it stopped (the record's stage and share) while the pipeline
        /// reads back what already arrived, and the bar never goes below that.
        /// </summary>
        public void Resume(string name, double sizeKm, string lastStage, double lastOverall)
        {
            Start(name, sizeKm);
            _floor = Math.Max(0, Math.Min(0.99, lastOverall));
            Fraction = (float)_floor;
            Percent = Pct(_floor);
            Detail = lastStage.Length > 0 ? $"Resuming at {lastStage}: reading back what already arrived" : "Resuming: reading back what already arrived";
            Pill = $"Downloading {Name} · {Percent}";
            Version++;
        }

        public void Apply(DownloadStatus s)
        {
            if (s == null) return;
            if (Name.Length == 0 && s.Name.Length > 0) Start(s.Name, s.SizeKm);
            UpdateNames(s);
            int current = CurrentIndex(s);
            bool changed = false;
            for (int i = 0; i < _names.Count; i++)
            {
                var state = s.Finished || i < current ? StageState.Done : i == current ? StageState.Current : StageState.Waiting;
                if (_states[i] != state) { _states[i] = state; changed = true; }
            }
            if (changed) StatesVersion++;

            double overall = s.Finished ? 1 : Math.Max(0, Math.Min(1, s.Overall));
            bool replaying = !s.Finished && overall < _floor;
            overall = Math.Max(overall, Math.Max(_floor, Phase == DownloadPhase.Running ? Fraction : 0));   // never backwards within a run
            Fraction = (float)overall;
            Percent = Pct(overall);
            TimeLeft = s.Finished ? "" : replaying ? "" : Remaining(s.SecondsRemaining);
            Detail = replaying ? $"Resuming: {s.Detail}" : s.Detail;
            if (!s.Finished && Phase == DownloadPhase.Waiting) { Phase = DownloadPhase.Running; Problem = null; _waitSeconds = -1; }
            Transfer = Megabytes(s.Bytes) + (s.BytesPerSecond > 0 && !s.Finished ? " · " + Megabytes((long)s.BytesPerSecond) + "/s" : "");
            if (Phase == DownloadPhase.Running) Pill = $"Downloading {Name} · {Percent}";
            if (s.Finished) Phase = DownloadPhase.Finished;   // a late snapshot after a pause or failure keeps that phase
            if (s.Stages.Count == 0 && s.StageCount > 0 && !s.Finished)
                Title = $"Downloading {Name} · stage {Math.Max(1, s.StageIndex)} of {s.StageCount}";
            Version++;
        }

        public void Paused()
        {
            Phase = DownloadPhase.Paused;
            Detail = "Paused. Resume continues where it stopped.";
            Pill = $"{Name} · paused";
            TimeLeft = "";
            Version++;
        }

        /// <summary>Stopped for good until Retry: the problem in words; <paramref name="message"/> is the raw one, for the log.</summary>
        public void Failed(DownloadProblem problem, string message = "")
        {
            Phase = DownloadPhase.Failed;
            Problem = problem;
            Error = message;
            Detail = problem.Title + ". " + problem.Text;
            Pill = $"{Name} · stopped";
            TimeLeft = "";
            Version++;
        }

        /// <summary>
        /// Waiting for the connection (trying again in <paramref name="secondsLeft"/>; below 0 = not counting, e.g.
        /// offline mode). Call it every frame while waiting: it changes only when the whole second does.
        /// </summary>
        public void Waiting(DownloadProblem problem, double secondsLeft)
        {
            int whole = secondsLeft < 0 ? -1 : (int)Math.Ceiling(secondsLeft);
            if (Phase == DownloadPhase.Waiting && Problem?.Kind == problem.Kind && whole == _waitSeconds) return;
            Phase = DownloadPhase.Waiting;
            Problem = problem;
            _waitSeconds = whole;
            bool offline = problem.Kind == ProblemKind.OfflineMode, busy = problem.Kind == ProblemKind.ServiceBusy;
            string what = busy ? "A map service is busy" : "No connection";
            Detail = offline ? "Offline mode is on. The download continues when you turn it off."
                   : whole > 0 ? $"{what}. Trying again in {whole} s."
                   : $"{what}. Trying again now.";
            TimeLeft = "";
            Transfer = "";
            Pill = offline ? $"{Name} · paused · offline mode" : busy ? $"{Name} · waiting for the map service" : $"{Name} · waiting for connection";
            Version++;
        }

        static string Pct(double f) => ((int)Math.Floor(f * 100)).ToString(CultureInfo.InvariantCulture) + "%";

        void UpdateNames(DownloadStatus s)
        {
            bool changed = false;
            if (s.Stages.Count > 0)
            {
                if (_names.Count != s.Stages.Count) changed = true;
                else for (int i = 0; i < _names.Count && !changed; i++) changed = _names[i] != s.Stages[i];
                if (changed)
                {
                    _names.Clear();
                    _names.AddRange(s.Stages);
                }
            }
            else if (!s.Finished && s.Stage.Length > 0 && !_names.Contains(s.Stage))
            {
                _names.Add(s.Stage);   // older pipelines: learn the stages as they arrive
                changed = true;
            }
            if (!changed) return;
            while (_states.Count < _names.Count) _states.Add(StageState.Waiting);
            while (_states.Count > _names.Count) _states.RemoveAt(_states.Count - 1);
            StagesVersion++;
            StatesVersion++;
        }

        int CurrentIndex(DownloadStatus s)
        {
            if (s.Finished) return _names.Count;
            if (s.Stages.Count > 0) return s.StageIndex - 1;
            return _names.IndexOf(s.Stage);
        }

        public static string Megabytes(long bytes) => (bytes / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + " MB";

        /// <summary>"about 1 min 20 s left"; a rough time is rounded so it doesn't flicker.</summary>
        public static string Remaining(double? seconds)
        {
            if (seconds == null || double.IsNaN(seconds.Value) || seconds.Value < 0) return "estimating time left";
            double t = seconds.Value;
            if (t < 60) return $"about {Math.Max(5, (int)Math.Ceiling(t / 5) * 5)} s left";
            if (t < 3600)
            {
                int total = (int)Math.Ceiling(t / 10) * 10;
                int m = total / 60, sec = total % 60;
                return sec == 0 ? $"about {m} min left" : $"about {m} min {sec} s left";
            }
            int minutes = (int)Math.Ceiling(t / 60);
            return $"about {minutes / 60} h {minutes % 60} min left";
        }
    }
}
