using System;
using System.Threading;
using MountainPlanner.Acquisition;
using MountainPlanner.UI.Flow;

namespace MountainPlanner.App.Flow
{
    /// <summary>
    /// Area jobs on the download card (task 4.05, PR A2, owner finding F1): Get weather and Get water and roads add data to an
    /// area already here, and show it as a download does, on the S4 card and its pill: the stage, the server, the MB, the
    /// speed, time left, and why it stopped. One job at a time with downloads (the guards in each job's own file).
    /// - Started by the player: the card opens; started by the area opening (the weather's automatic try): the pill alone.
    /// - Cancel stops it at once with no question: neither job writes anything until it has everything, so the area is as it was.
    /// - Finished: the card goes and a toast says so. Failed: the card says why, with Retry when the player started it; the
    ///   automatic try just goes away, to try again at the next open.
    /// </summary>
    public sealed partial class AppFlow
    {
        sealed class AreaJob
        {
            public string Name = "";
            public string Action = "";
            public bool Manual;
            public bool Running = true;
            public Action Retry;
            public readonly CancellationTokenSource Cancel = new CancellationTokenSource();
            public readonly DownloadViewModel View = new DownloadViewModel();
        }

        /// <summary>The job on the card: running, or failed and waiting for Retry or Close; null when there is none.</summary>
        AreaJob _areaJob;

        bool AreaJobRunning => _areaJob != null && _areaJob.Running;

        /// <summary>The card shows the area job (no download holds it).</summary>
        bool AreaJobShown => Downloads.Current == null && _areaJob != null;

        /// <param name="action">The job's button, "Get weather"; <paramref name="verb"/> what it's doing, "Getting weather".</param>
        AreaJob BeginAreaJob(string name, string action, string verb, bool manual, Action retry)
        {
            DismissAreaJob();   // a failed card left from the last one
            if (Downloads.Current != null && !Downloads.Active) Downloads.Dismiss();   // a failed download's card gives way (its record stays)
            var job = new AreaJob { Name = name, Action = action, Manual = manual, Retry = retry };
            job.View.StartAreaJob(name, action, verb, canRetry: manual && retry != null);
            _areaJob = job;
            Controller.AreaJobStarted(openCard: manual);
            return job;
        }

        /// <summary>The backfill's progress onto the card (all of it), and its share onto the area's row in Manage Areas.</summary>
        IProgress<AcquisitionProgress> AreaJobProgress(AreaJob job, Action<int> percent) => new Progress<AcquisitionProgress>(p =>
        {
            if (this == null) return;
            job.View.Apply(PipelineDownloader.ToStatus(p, job.Name, 0));
            percent((int)Math.Floor(Math.Max(0, Math.Min(1, p.Overall)) * 100));
        });

        /// <summary>
        /// The job ended: <paramref name="error"/> null when it finished. A failed job the player started keeps the card, saying
        /// <paramref name="why"/>; anything else takes the card and pill away. Returns whether it was cancelled.
        /// </summary>
        bool EndAreaJob(AreaJob job, Exception error, string why)
        {
            job.Running = false;
            bool cancelled = error is OperationCanceledException && job.Cancel.IsCancellationRequested;
            if (_areaJob != job || this == null) return cancelled;
            if (error == null || cancelled || !job.Manual)
            {
                _areaJob = null;
                job.Cancel.Dispose();
                Controller.DownloadStopped();
                return cancelled;
            }
            var kind = DownloadErrors.Classify(error);
            job.View.Failed(new DownloadProblem { Kind = kind, Title = job.Action + " stopped", Text = why }, error.Message);
            Controller.RestoreDownload();
            return false;
        }

        /// <summary>The card's Cancel: the job stops at its next step (the weather's calibration checks between plays).</summary>
        void CancelAreaJob()
        {
            var job = _areaJob;
            if (job == null) return;
            if (!job.Running) { DismissAreaJob(); return; }
            try { job.Cancel.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        /// <summary>The failed card's Retry: the same job again, through its own guards.</summary>
        void RetryAreaJob()
        {
            var job = _areaJob;
            if (job == null || job.Running) return;
            DismissAreaJob();
            job.Retry?.Invoke();
        }

        /// <summary>The failed card's Close, or a new job or download taking the card.</summary>
        void DismissAreaJob()
        {
            var job = _areaJob;
            if (job == null || job.Running) return;
            _areaJob = null;
            job.Cancel.Dispose();
            Controller.DownloadStopped();
        }
    }
}
