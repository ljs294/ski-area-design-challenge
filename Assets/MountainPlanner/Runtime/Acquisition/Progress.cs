#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition.IO;

namespace MountainPlanner.Acquisition
{
    /// <summary>
    /// One progress snapshot (U6; 0.3 §6 progress contract). The S4 download screen and the CLI both
    /// render it: an overall bar, the stage, and a detail line such as
    /// "Terrain: downloading sector 5 of 19 · 35%".
    /// </summary>
    public sealed class AcquisitionProgress
    {
        public int StageIndex { get; set; }
        public int StageCount { get; set; }
        public string Stage { get; set; } = "";
        /// <summary>Every stage the job declared, in order, so a screen can list them all from the first snapshot.</summary>
        public IReadOnlyList<string> Stages { get; set; } = Array.Empty<string>();
        public int Step { get; set; }
        public int StepCount { get; set; }
        /// <summary>The current step's own progress, 0–1.</summary>
        public double StepFraction { get; set; }
        /// <summary>The whole job, 0–1, weighted by expected work.</summary>
        public double Overall { get; set; }
        /// <summary>Bytes delivered so far, including any served from the resume cache.</summary>
        public long Bytes { get; set; }
        public long DownloadedBytes { get; set; }
        public double BytesPerSecond { get; set; }
        public double? SecondsRemaining { get; set; }
        /// <summary>A ready-made status line for the current step.</summary>
        public string Detail { get; set; } = "";
        public bool Finished { get; set; }
        /// <summary>The server the current step is waiting on (a <see cref="Servers"/> name), or null when it uses no network.</summary>
        public string? Server { get; set; }
        /// <summary>Seconds since the last byte arrived (or since the step began, if none has).</summary>
        public double QuietSeconds { get; set; }
    }

    /// <summary>
    /// Tracks the job's stages and steps and publishes <see cref="AcquisitionProgress"/> at least four
    /// times a second while work runs, plus immediately on every stage or step change.
    /// </summary>
    public sealed class ProgressTracker : IDisposable
    {
        public const int IntervalMilliseconds = 250;

        readonly IProgress<AcquisitionProgress>? _sink;
        readonly TransferMeter _meter;
        readonly List<(string Name, double Weight)> _stages = new List<(string, double)>();
        readonly Func<double> _now;
        readonly Timer? _timer;
        readonly object _gate = new object();
        readonly Queue<(double Seconds, long Bytes)> _window = new Queue<(double, long)>();

        int _stage = -1, _step, _stepCount;
        string _verb = "";
        string? _server;
        Func<double> _stepFraction = () => 0;
        /// <summary>The step's fraction covers the whole phase (<see cref="SetPhase"/>); its number is for the detail line only.</summary>
        bool _stepIsPhase;
        double _completedWeight;
        double _lastByteAt;
        double _spanStart, _spanEnd = 1;
        long _lastBytes;

        public ProgressTracker(IProgress<AcquisitionProgress>? sink, TransferMeter meter) : this(sink, meter, null) { }

        /// <summary><paramref name="now"/> gives seconds on a steady clock (tests pass their own; default a stopwatch).</summary>
        internal ProgressTracker(IProgress<AcquisitionProgress>? sink, TransferMeter meter, Func<double>? now)
        {
            _sink = sink;
            _meter = meter;
            if (now == null)
            {
                var clock = Stopwatch.StartNew();
                now = () => clock.Elapsed.TotalSeconds;
            }
            _now = now;
            if (sink != null) _timer = new Timer(_ => Publish(), null, IntervalMilliseconds, IntervalMilliseconds);
        }

        /// <summary>Declares a stage and its expected share of the work (any unit, e.g. bytes).</summary>
        public void DefineStage(string name, double weight) { lock (_gate) _stages.Add((name, Math.Max(weight, 1e-6))); }

        public void SetStageWeight(string name, double weight)
        {
            lock (_gate)
                for (int i = 0; i < _stages.Count; i++)
                    if (_stages[i].Name == name) _stages[i] = (name, Math.Max(weight, 1e-6));
        }

        public void BeginStage(string name)
        {
            lock (_gate)
            {
                _stage = _stages.FindIndex(s => s.Name == name);
                if (_stage < 0) throw new InvalidOperationException($"Stage '{name}' was not defined.");
                // Every stage before this one counts as done, so a resumed download can skip finished stages (task P2-06).
                _completedWeight = 0;
                for (int i = 0; i < _stage; i++) _completedWeight += _stages[i].Weight;
                _step = 0;
                _stepCount = 0;
                _verb = "";
                _stepFraction = () => 0;
                _spanStart = 0;
                _spanEnd = 1;
            }
            Publish();
        }

        /// <summary>
        /// Maps the following steps into part of the current stage, e.g. S1M downloads 0–0.4 and the
        /// 3DEP fallback 0.4–1, so a stage with phases never makes the overall bar go backwards.
        /// </summary>
        public void SetPhase(double start, double end)
        {
            lock (_gate)
            {
                _spanStart = Clamp01(start);
                _spanEnd = Math.Max(_spanStart, Clamp01(end));
            }
        }

        /// <summary>
        /// Starts step <paramref name="step"/> of <paramref name="count"/>, e.g. ("downloading sector", 5, 19).
        /// <paramref name="fraction"/> reports the step's own progress when polled. <paramref name="server"/> is the
        /// <see cref="Servers"/> name of the server the step downloads from; leave it null for steps that use no network.
        /// </summary>
        /// <param name="wholePhase">
        /// <paramref name="fraction"/> runs across the whole phase set by <see cref="SetPhase"/>, rather than its
        /// <paramref name="step"/>th of <paramref name="count"/> share: for steps of unequal length that each have their own phase.
        /// </param>
        public void BeginStep(string verb, int step, int count, Func<double> fraction, string? server = null, bool wholePhase = false)
        {
            lock (_gate)
            {
                _stepIsPhase = wholePhase;
                _server = server;
                _verb = verb;
                _step = step;
                _stepCount = count;
                _stepFraction = fraction;
                _lastByteAt = _now();
                _lastBytes = _meter.Bytes;
            }
            Publish();
        }

        /// <summary>Names the server the current step is now asking (e.g. the next OpenStreetMap mirror).</summary>
        public void SetServer(string? server)
        {
            lock (_gate)
            {
                _server = server;
                _lastByteAt = _now();
                _lastBytes = _meter.Bytes;
            }
            Publish();
        }

        public void Finish()
        {
            lock (_gate)
            {
                if (_stage >= 0) _completedWeight += _stages[_stage].Weight;
                _stage = _stages.Count;
            }
            _timer?.Dispose();
            _sink?.Report(Snapshot(finished: true));
        }

        public AcquisitionProgress Snapshot(bool finished = false)
        {
            lock (_gate)
            {
                double total = 0;
                foreach (var s in _stages) total += s.Weight;
                double stepFraction = Clamp01(_stepFraction());
                double phaseFraction = _stepCount > 0 && !_stepIsPhase ? ((_step - 1) + stepFraction) / _stepCount : stepFraction;
                double stageFraction = _spanStart + (_spanEnd - _spanStart) * Clamp01(phaseFraction);
                double current = _stage >= 0 && _stage < _stages.Count ? _stages[_stage].Weight * Clamp01(stageFraction) : 0;
                double overall = finished ? 1 : Clamp01((_completedWeight + current) / Math.Max(total, 1e-9));

                double now = _now();
                long bytes = _meter.Bytes;
                if (bytes != _lastBytes)
                {
                    _lastBytes = bytes;
                    _lastByteAt = now;
                }
                _window.Enqueue((now, _meter.DownloadedBytes));
                while (_window.Count > 2 && now - _window.Peek().Seconds > 5) _window.Dequeue();
                var first = _window.Peek();
                double rate = now - first.Seconds > 0.2 ? (_meter.DownloadedBytes - first.Bytes) / (now - first.Seconds) : 0;
                double? remaining = overall > 0.02 && !finished ? now / overall * (1 - overall) : (double?)null;

                string stage = _stage >= 0 && _stage < _stages.Count ? _stages[_stage].Name : finished ? "Done" : "";
                // A server preparing a response, or stalled mid-transfer, sends nothing for a while; say so rather than
                // sit still. Steps that use no network (planning, building, preparing) never wait on a server.
                double quiet = now - _lastByteAt;
                string waiting = !finished && _server != null && _stepCount > 0 && quiet > 2
                    ? $" · waiting for {_server} ({quiet:F0} s)" : "";
                string detail = finished ? "Finished"
                    : _stepCount > 0 ? $"{stage}: {_verb} {_step} of {_stepCount} · {Pct(stepFraction)}{waiting}"
                    : _verb.Length > 0 ? $"{stage}: {_verb} · {Pct(stepFraction)}"
                    : stage;
                return new AcquisitionProgress
                {
                    StageIndex = Math.Min(_stage + 1, _stages.Count), StageCount = _stages.Count, Stage = stage,
                    Stages = _stages.Select(s => s.Name).ToArray(),
                    Step = _step, StepCount = _stepCount, StepFraction = stepFraction, Overall = overall,
                    Bytes = bytes, DownloadedBytes = _meter.DownloadedBytes, BytesPerSecond = rate,
                    SecondsRemaining = remaining, Detail = detail, Finished = finished,
                    Server = finished ? null : _server, QuietSeconds = quiet,
                };
            }
        }

        void Publish()
        {
            if (_sink == null) return;
            try { _sink.Report(Snapshot()); }
            catch (Exception) { /* a failing progress sink must never break the download */ }
        }

        static double Clamp01(double v) => double.IsNaN(v) ? 0 : v < 0 ? 0 : v > 1 ? 1 : v;
        static string Pct(double f) => ((int)Math.Floor(f * 100)).ToString(CultureInfo.InvariantCulture) + "%";

        public void Dispose() => _timer?.Dispose();
    }
}
