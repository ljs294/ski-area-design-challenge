#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
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
        readonly Stopwatch _clock = Stopwatch.StartNew();
        readonly Timer? _timer;
        readonly object _gate = new object();
        readonly Queue<(double Seconds, long Bytes)> _window = new Queue<(double, long)>();

        int _stage = -1, _step, _stepCount;
        string _verb = "";
        string _server = "the server";
        Func<double> _stepFraction = () => 0;
        double _completedWeight;
        double _stepStarted;
        double _spanStart, _spanEnd = 1;
        long _stepBytes;

        public ProgressTracker(IProgress<AcquisitionProgress>? sink, TransferMeter meter)
        {
            _sink = sink;
            _meter = meter;
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
                if (_stage >= 0) _completedWeight += _stages[_stage].Weight;
                _stage = _stages.FindIndex(s => s.Name == name);
                if (_stage < 0) throw new InvalidOperationException($"Stage '{name}' was not defined.");
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
        /// <paramref name="fraction"/> reports the step's own progress when polled.
        /// </summary>
        public void BeginStep(string verb, int step, int count, Func<double> fraction, string server = "the server")
        {
            lock (_gate)
            {
                _server = server;
                _verb = verb;
                _step = step;
                _stepCount = count;
                _stepFraction = fraction;
                _stepStarted = _clock.Elapsed.TotalSeconds;
                _stepBytes = _meter.Bytes;
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
                double phaseFraction = _stepCount > 0 ? ((_step - 1) + stepFraction) / _stepCount : stepFraction;
                double stageFraction = _spanStart + (_spanEnd - _spanStart) * Clamp01(phaseFraction);
                double current = _stage >= 0 && _stage < _stages.Count ? _stages[_stage].Weight * Clamp01(stageFraction) : 0;
                double overall = finished ? 1 : Clamp01((_completedWeight + current) / Math.Max(total, 1e-9));

                double now = _clock.Elapsed.TotalSeconds;
                long bytes = _meter.Bytes;
                _window.Enqueue((now, _meter.DownloadedBytes));
                while (_window.Count > 2 && now - _window.Peek().Seconds > 5) _window.Dequeue();
                var first = _window.Peek();
                double rate = now - first.Seconds > 0.2 ? (_meter.DownloadedBytes - first.Bytes) / (now - first.Seconds) : 0;
                double? remaining = overall > 0.02 && !finished ? now / overall * (1 - overall) : (double?)null;

                string stage = _stage >= 0 && _stage < _stages.Count ? _stages[_stage].Name : finished ? "Done" : "";
                // A server preparing a response sends nothing for a while; say so rather than sit at 0%.
                double quiet = now - _stepStarted;
                string waiting = !finished && _stepCount > 0 && _meter.Bytes == _stepBytes && quiet > 2
                    ? $" · waiting for {_server} ({quiet:F0} s)" : "";
                string detail = finished ? "Finished"
                    : _stepCount > 0 ? $"{stage}: {_verb} {_step} of {_stepCount} · {Pct(stepFraction)}{waiting}"
                    : _verb.Length > 0 ? $"{stage}: {_verb} · {Pct(stepFraction)}"
                    : stage;
                return new AcquisitionProgress
                {
                    StageIndex = Math.Min(_stage + 1, _stages.Count), StageCount = _stages.Count, Stage = stage,
                    Step = _step, StepCount = _stepCount, StepFraction = stepFraction, Overall = overall,
                    Bytes = bytes, DownloadedBytes = _meter.DownloadedBytes, BytesPerSecond = rate,
                    SecondsRemaining = remaining, Detail = detail, Finished = finished,
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
