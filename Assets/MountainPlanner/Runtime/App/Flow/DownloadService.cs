using System;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MountainPlanner.Acquisition;
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
            Detail = p.Detail, Finished = p.Finished,
        };
    }

    /// <summary>
    /// One background download at a time, for the whole session (it outlives scene reloads). The work runs
    /// on a worker thread; <see cref="Pump"/>, called from the main thread, hands the newest snapshot to the
    /// view model and reports the outcome, so nothing touches the UI off the main thread.
    /// </summary>
    public sealed class DownloadService
    {
        /// <summary>How often the paused-at line in the record is refreshed, in Pump calls' seconds.</summary>
        public const double RecordEverySeconds = 2;

        readonly string _dataRoot;
        readonly ISiteDownloader _downloader;
        readonly Func<string> _utcNow;
        DownloadStatus _latest;
        readonly object _gate = new object();
        Task<string> _task;
        CancellationTokenSource _cts;
        bool _discard;
        double _sinceRecord;

        public readonly DownloadViewModel View = new DownloadViewModel();
        public PendingDownload Current { get; private set; }
        public bool Running => _task != null;

        /// <summary>The package reached the library: its folder.</summary>
        public event Action<string> Finished;
        /// <summary>The download stopped with an error; its record stays, so Retry resumes.</summary>
        public event Action<string> Failed;
        /// <summary>Cancelled: kept for resuming (true) or discarded (false).</summary>
        public event Action<bool> Stopped;

        public DownloadService(string dataRoot, ISiteDownloader downloader, Func<string> utcNow)
        {
            _dataRoot = dataRoot;
            _downloader = downloader;
            _utcNow = utcNow;
        }

        /// <summary>A new download from the picker's choice, or the same request again when it was paused.</summary>
        public static PendingDownload Request(PickedSite site, string startedUtc) => new PendingDownload
        {
            Id = PendingDownloads.IdFor(site.Name, site.Centre.Latitude, site.Centre.Longitude, site.SizeKm),
            Name = site.Name, Latitude = site.Centre.Latitude, Longitude = site.Centre.Longitude, SizeKm = site.SizeKm,
            StartedUtc = startedUtc,
        };

        public bool Start(PendingDownload d)
        {
            if (Running) return false;
            Current = d;
            PendingDownloads.Save(_dataRoot, d);
            View.Start(d.Name, d.SizeKm);
            _discard = false;
            _sinceRecord = 0;
            lock (_gate) _latest = null;
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            var progress = new Latest(this);
            string build = PendingDownloads.BuildFolder(_dataRoot, d);
            _task = Task.Run(() => _downloader.DownloadAsync(d, build, progress, ct), ct);
            return true;
        }

        /// <summary>Stops the download. Kept: its record and partial files stay for Resume. Discarded: they're deleted.</summary>
        public void Cancel(bool keep)
        {
            if (!Running) return;
            _discard = !keep;
            _cts.Cancel();
        }

        /// <summary>After a failure: forget the card (the record stays, so the library offers Resume).</summary>
        public void Dismiss()
        {
            if (Running) return;
            Current = null;
        }

        /// <summary>Main thread, every frame: newest snapshot to the view model; completion to the events.</summary>
        public void Pump(double deltaSeconds)
        {
            DownloadStatus s;
            lock (_gate)
            {
                s = _latest;
                _latest = null;
            }
            if (s != null) View.Apply(s);
            if (_task == null) return;

            _sinceRecord += deltaSeconds;
            if (s != null && _sinceRecord >= RecordEverySeconds && Current != null)
            {
                _sinceRecord = 0;
                Current.LastOverall = Math.Round(s.Overall, 3);
                Current.LastStage = s.Stage;
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
                string message = task.Exception?.GetBaseException().Message ?? "Unknown error";
                TrySave(d);
                View.Failed(message);
                Failed?.Invoke(message);
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
}
