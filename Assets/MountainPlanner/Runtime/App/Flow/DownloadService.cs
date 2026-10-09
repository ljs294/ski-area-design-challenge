using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
using MountainPlanner.Acquisition.IO;
using MountainPlanner.Domain.Geo;
using MountainPlanner.Persistence;
using MountainPlanner.UI.Flow;

namespace MountainPlanner.App.Flow
{
    /// <summary>Runs one site download into the library. The real one is the acquisition pipeline; tests use fakes.</summary>
    public interface ISiteDownloader
    {
        /// <summary>Downloads into <paramref name="buildFolder"/>, moves the package into the library, and returns its folder.</summary>
        Task<string> DownloadAsync(PendingDownload download, string buildFolder, IProgress<DownloadStatus> progress, CancellationToken ct);
    }

    /// <summary>
    /// The acquisition pipeline (tasks 04 and 05) behind <see cref="ISiteDownloader"/>. It shares the
    /// download cache with tools/acquire (&lt;data&gt;/download-cache), so a download stopped in either one resumes in the other.
    /// </summary>
    public sealed class PipelineDownloader : ISiteDownloader
    {
        readonly string _dataRoot;

        public PipelineDownloader(string dataRoot) => _dataRoot = dataRoot;

        public static string CacheFolder(string dataRoot) => Path.Combine(dataRoot, "download-cache");

        public async Task<string> DownloadAsync(PendingDownload d, string buildFolder, IProgress<DownloadStatus> progress, CancellationToken ct)
        {
            var request = new SiteRequest { Name = d.Name, Centre = new GeoPoint(d.Latitude, d.Longitude), SizeKm = d.SizeKm };
            var sink = new Relay(progress, d);
            await new AcquisitionPipeline(CacheFolder(_dataRoot)).RunAsync(request, buildFolder, sink, ct).ConfigureAwait(false);
            return ResortLibrary.Add(_dataRoot, buildFolder);
        }

        /// <summary>The pipeline's progress, copied into the UI's own type (UI doesn't reference Acquisition).</summary>
        sealed class Relay : IProgress<AcquisitionProgress>
        {
            readonly IProgress<DownloadStatus> _out;
            readonly PendingDownload _d;

            public Relay(IProgress<DownloadStatus> output, PendingDownload d)
            {
                _out = output;
                _d = d;
            }

            public void Report(AcquisitionProgress p) => _out?.Report(ToStatus(p, _d.Name, _d.SizeKm));
        }

        public static DownloadStatus ToStatus(AcquisitionProgress p, string name, double sizeKm) => new DownloadStatus
        {
            Name = name, SizeKm = sizeKm, Stages = p.Stages, StageIndex = p.StageIndex, StageCount = p.StageCount, Stage = p.Stage,
            Overall = p.Overall, Bytes = p.Bytes, BytesPerSecond = p.BytesPerSecond, SecondsRemaining = p.SecondsRemaining,
            Detail = p.Detail, Finished = p.Finished, Downloaded = p.DownloadedBytes,
        };
    }

    /// <summary>
    /// One background download at a time, for the whole session (it outlives scene reloads; G5). The work runs on
    /// a worker thread; <see cref="Pump"/>, called from the main thread, hands the newest snapshot to the view model
    /// and reports the outcome, so nothing touches the UI off the main thread.
    ///
    /// Task P2-06: a lost connection doesn't stop it. It waits (<see cref="DownloadPhase.Waiting"/>) and tries again
    /// every <see cref="RetrySeconds"/>, or, with offline mode on, until <see cref="TryNow"/> is called when that's
    /// turned off; the download cache keeps everything that arrived. Any other failure stops it with the problem in words.
    /// </summary>
    public sealed class DownloadService
    {
        /// <summary>How often the paused-at line in the record is refreshed, in Pump calls' seconds.</summary>
        public const double RecordEverySeconds = 2;
        /// <summary>How long it waits for the connection before trying again.</summary>
        public const double RetrySeconds = 15;
        /// <summary>How long it gives a busy map service (502, 503...) before trying again.</summary>
        public const double BusyRetrySeconds = 60;

        string _dataRoot;
        ISiteDownloader _downloader;
        readonly Func<string> _utcNow;
        DownloadStatus _latest;
        readonly object _gate = new object();
        Task<string> _task;
        CancellationTokenSource _cts;
        bool _discard;
        double _sinceRecord;
        /// <summary>Seconds until the next try while waiting; below 0 = waiting for <see cref="TryNow"/> (offline mode).</summary>
        double _retryIn = -1;
        long _downloaded;
        string _loggedStage = "";

        public readonly DownloadViewModel View = new DownloadViewModel();
        public PendingDownload Current { get; private set; }
        /// <summary>The worker is running (not waiting, paused or stopped).</summary>
        public bool Running => _task != null;
        /// <summary>Waiting for the connection, or for offline mode to be turned off.</summary>
        public bool Waiting => _task == null && Current != null && View.Phase == DownloadPhase.Waiting;
        /// <summary>Running or waiting to continue: the one download slot is taken.</summary>
        public bool Active => Running || Waiting;
        public string DataRoot => _dataRoot;

        /// <summary>The package reached the library: its folder.</summary>
        public event Action<string> Finished;
        /// <summary>The download stopped with a problem it can't wait out; its record stays, so Retry resumes.</summary>
        public event Action<DownloadProblem> Failed;
        /// <summary>Cancelled: kept for resuming (true) or discarded (false).</summary>
        public event Action<bool> Stopped;
        /// <summary>It began waiting for the connection, or for offline mode to be turned off.</summary>
        public event Action<DownloadProblem> WaitStarted;

        public DownloadService(string dataRoot, ISiteDownloader downloader, Func<string> utcNow)
        {
            _dataRoot = dataRoot;
            _downloader = downloader;
            _utcNow = utcNow;
        }

        /// <summary>Another library folder (Settings › Data; task P2-05). Only between downloads.</summary>
        public void Retarget(string dataRoot, ISiteDownloader downloader)
        {
            if (Active) throw new InvalidOperationException("A download is running.");
            _dataRoot = dataRoot;
            _downloader = downloader;
        }

        /// <summary>A new download from the picker's choice, or the same request again when it was paused.</summary>
        public static PendingDownload Request(PickedSite site, string startedUtc) => new PendingDownload
        {
            Id = PendingDownloads.IdFor(site.Name, site.Centre.Latitude, site.Centre.Longitude, site.SizeKm),
            Name = site.Name, Latitude = site.Centre.Latitude, Longitude = site.Centre.Longitude, SizeKm = site.SizeKm,
            StartedUtc = startedUtc,
        };

        /// <summary>
        /// Starts (or resumes) <paramref name="d"/>; false when another download has the slot. A record that got
        /// somewhere (LastOverall) shows as resuming from there.
        /// </summary>
        public bool Start(PendingDownload d)
        {
            if (Running || (Waiting && Current.Id != d.Id)) return false;
            Current = d;
            PendingDownloads.Save(_dataRoot, d);
            if (d.LastOverall > 0) View.Resume(d.Name, d.SizeKm, d.LastStage ?? "", d.LastOverall);
            else View.Start(d.Name, d.SizeKm);
            Run(d);
            return true;
        }

        void Run(PendingDownload d)
        {
            _discard = false;
            _sinceRecord = 0;
            _retryIn = -1;
            _downloaded = 0;
            _loggedStage = "";
            lock (_gate) _latest = null;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var progress = new Latest(this);
            string build = PendingDownloads.BuildFolder(_dataRoot, d);
            _task = Task.Run(() => _downloader.DownloadAsync(d, build, progress, ct), ct);
        }

        /// <summary>While waiting: try again now (Try now, or offline mode was turned off).</summary>
        public void TryNow()
        {
            if (Waiting) Run(Current);
        }

        /// <summary>Stops the download. Kept: its record and partial files stay for Resume. Discarded: they're deleted.</summary>
        public void Cancel(bool keep)
        {
            if (Waiting)
            {
                var d = Current;
                if (keep) TrySave(d);
                else PendingDownloads.Remove(_dataRoot, d);
                View.Paused();
                Current = null;
                Stopped?.Invoke(keep);
                return;
            }
            if (!Running) return;
            _discard = !keep;
            _cts.Cancel();
        }

        /// <summary>After a failure: forget the card (the record stays, so the library offers Resume).</summary>
        public void Dismiss()
        {
            if (Active) return;
            Current = null;
        }

        /// <summary>
        /// Main thread, every frame: newest snapshot to the view model; completion to the events. Without a new
        /// snapshot (3 frames in 4 at 60 fps) it allocates nothing.
        /// </summary>
        public void Pump(double deltaSeconds)
        {
            if (Waiting)
            {
                if (_retryIn >= 0)
                {
                    _retryIn -= deltaSeconds;
                    if (_retryIn <= 0) Run(Current);
                    else View.Waiting(View.Problem, _retryIn);
                }
                return;
            }
            DownloadStatus s;
            lock (_gate)
            {
                s = _latest;
                _latest = null;
            }
            if (s != null)
            {
                View.Apply(s);
                if (s.Stage != _loggedStage && s.Stage.Length > 0 && Current != null)
                {
                    _loggedStage = s.Stage;   // one line per stage: demo.bat 54 and the benchmark read where it got to
                    UnityEngine.Debug.Log($"[Downloads] {Current.Name}: {s.Stage} at {s.Overall:P0}, {s.Downloaded / 1e6:F1} MB over the network so far");
                }
                if (s.Downloaded > _downloaded)
                {
                    _downloaded = s.Downloaded;
                    OfflineState.ReportConnection(true);   // bytes are arriving: whatever said "no connection" is out of date
                }
            }
            if (_task == null) return;

            _sinceRecord += deltaSeconds;
            if (s != null && _sinceRecord >= RecordEverySeconds && Current != null)
            {
                _sinceRecord = 0;
                // A resumed download reading back what arrived keeps the furthest point reached, so a second kill loses nothing.
                if (s.Overall >= Current.LastOverall)
                {
                    Current.LastOverall = Math.Round(s.Overall, 3);
                    Current.LastStage = s.Stage;
                }
                TrySave(Current);
            }
            if (!_task.IsCompleted) return;

            var task = _task;
            var d = Current;
            _task = null;
            _cts.Dispose();
            _cts = null;
            if (task.Status == TaskStatus.RanToCompletion)
            {
                PendingDownloads.Remove(_dataRoot, d);
                View.Apply(new DownloadStatus { Name = d.Name, SizeKm = d.SizeKm, Finished = true, Stages = Array.Empty<string>(), Detail = "Finished" });
                Current = null;
                Finished?.Invoke(task.Result);
            }
            else if (task.IsCanceled || task.Exception?.GetBaseException() is OperationCanceledException)
            {
                if (_discard) PendingDownloads.Remove(_dataRoot, d);
                else TrySave(d);
                View.Paused();
                Current = null;
                Stopped?.Invoke(!_discard);
            }
            else
            {
                Exception error = task.Exception?.InnerExceptions.Count == 1 ? task.Exception.InnerExceptions[0] : task.Exception;
                var problem = DownloadProblem.Of(DownloadErrors.Classify(error));
                TrySave(d);
                if (problem.Waits)
                {
                    UnityEngine.Debug.Log($"[Downloads] {d.Name}: waiting ({problem.Kind}): {error?.GetBaseException().Message}");
                    if (problem.Kind == ProblemKind.NoConnection) OfflineState.ReportConnection(false);
                    _retryIn = problem.Kind == ProblemKind.NoConnection ? RetrySeconds : problem.Kind == ProblemKind.ServiceBusy ? BusyRetrySeconds : -1;
                    View.Waiting(problem, _retryIn);
                    WaitStarted?.Invoke(problem);
                }
                else
                {
                    UnityEngine.Debug.LogWarning($"[Downloads] {d.Name} stopped ({problem.Kind}): {error}");
                    View.Failed(problem, error?.GetBaseException().Message ?? "");
                    Failed?.Invoke(problem);
                }
            }
        }

        void TrySave(PendingDownload d)
        {
            try { PendingDownloads.Save(_dataRoot, d); }
            catch (IOException) { /* the record is a convenience; the download itself carries on */ }
            catch (UnauthorizedAccessException) { }
        }

        /// <summary>Keeps only the newest snapshot; the main thread picks it up in Pump.</summary>
        sealed class Latest : IProgress<DownloadStatus>
        {
            readonly DownloadService _owner;
            public Latest(DownloadService owner) => _owner = owner;
            public void Report(DownloadStatus value)
            {
                lock (_owner._gate) _owner._latest = value;
            }
        }

        public static string UtcStamp(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    }

    /// <summary>Sorts a download's failure into what the player is told (S11; task P2-06).</summary>
    public static class DownloadErrors
    {
        const int DiskFull = 0x70, HandleDiskFull = 0x27;

        public static ProblemKind Classify(Exception e)
        {
            if (e == null) return ProblemKind.Unknown;
            if (NetworkFailure.IsOfflineMode(e)) return ProblemKind.OfflineMode;
            if (NetworkFailure.IsNoConnection(e)) return ProblemKind.NoConnection;
            if (NetworkFailure.IsServiceBusy(e)) return ProblemKind.ServiceBusy;
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is UnauthorizedAccessException) return ProblemKind.AccessDenied;
                int code = x.HResult & 0xFFFF;
                if (x is IOException && (code == DiskFull || code == HandleDiskFull)) return ProblemKind.DiskFull;
            }
            for (var x = e; x != null; x = x.InnerException)
                if (x is System.Net.Http.HttpRequestException || x is InvalidDataException) return ProblemKind.ServerError;
            return ProblemKind.Unknown;
        }
    }
}
