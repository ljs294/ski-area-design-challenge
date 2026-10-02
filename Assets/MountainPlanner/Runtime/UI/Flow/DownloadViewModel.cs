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
        public double BytesPerSecond;
        public double? SecondsRemaining;
        public string Detail = "";
        public bool Finished;
    }

    public enum StageState { Waiting, Current, Done }

    public enum DownloadPhase { Running, Paused, Failed, Finished }

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
        /// <summary>The minimised status pill: "Crystal Mountain 41%".</summary>
        public string Pill { get; private set; } = "";
        public string Error { get; private set; } = "";

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
            Title = $"Downloading {name} · {Km(sizeKm)}";
            Pill = $"{name} 0%";
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
            Fraction = (float)overall;
            Percent = ((int)Math.Floor(overall * 100)).ToString(CultureInfo.InvariantCulture) + "%";
            TimeLeft = s.Finished ? "" : Remaining(s.SecondsRemaining);
            Detail = s.Detail;
            Transfer = Megabytes(s.Bytes) + (s.BytesPerSecond > 0 && !s.Finished ? " · " + Megabytes((long)s.BytesPerSecond) + "/s" : "");
            Pill = $"{Name} {Percent}";
            if (s.Finished) Phase = DownloadPhase.Finished;   // a late snapshot after a pause or failure keeps that phase
            if (s.Stages.Count == 0 && s.StageCount > 0 && !s.Finished)
                Title = $"Downloading {Name} · stage {Math.Max(1, s.StageIndex)} of {s.StageCount}";
        }

        public void Paused()
        {
            Phase = DownloadPhase.Paused;
            Detail = "Paused. Resume continues where it stopped.";
            Pill = $"{Name} paused";
        }

        public void Failed(string message)
        {
            Phase = DownloadPhase.Failed;
            Error = message;
            Detail = "Stopped: " + message;
            Pill = $"{Name} stopped";
        }

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

        static string Km(double km) => km.ToString("0.0", CultureInfo.InvariantCulture) + " km";

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
