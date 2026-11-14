#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;

namespace MountainPlanner.Acquisition.Updates
{
    public enum UpdateState
    {
        /// <summary>The installed version is the latest (or newer).</summary>
        UpToDate,
        /// <summary>A newer release is out: <see cref="UpdateResult.Latest"/>.</summary>
        Available,
        /// <summary>This is a preview build (0.3.2+dev…, or the editor): previews are never offered a release.</summary>
        Preview,
        /// <summary>The feed couldn't be reached or read; the game carries on as it is.</summary>
        Unreachable,
    }

    public sealed class UpdateResult
    {
        public UpdateState State;
        public ReleaseInfo? Latest;
        /// <summary>One line for the log.</summary>
        public string Message = "";
    }

    /// <summary>
    /// The version check (task 4.23, UP4): the installed version against the feed's latest release. Never throws for a
    /// network or format problem (it answers <see cref="UpdateState.Unreachable"/>), so a game or launcher that is
    /// offline starts as normal. Only the caller's own cancellation escapes.
    /// </summary>
    public static class UpdateCheck
    {
        public static UpdateState Decide(string installed, ReleaseInfo latest)
        {
            if (!GameVersion.TryParse(installed, out var have) || have.IsPreview) return UpdateState.Preview;
            return latest.Version > have ? UpdateState.Available : UpdateState.UpToDate;
        }

        public static async Task<UpdateResult> CheckAsync(IReleaseFeed feed, string installed, CancellationToken ct)
        {
            if (!GameVersion.TryParse(installed, out var have) || have.IsPreview)
                return new UpdateResult { State = UpdateState.Preview, Message = $"Update check skipped: {Show(installed)} is a preview build." };
            try
            {
                var latest = await feed.LatestAsync(ct).ConfigureAwait(false);
                var state = Decide(installed, latest);
                string message = state == UpdateState.Available
                    ? $"Update check: {latest.Version} is out (this is {have})."
                    : $"Update check: {have} is up to date (latest {latest.Version}).";
                return new UpdateResult { State = state, Latest = latest, Message = message };
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e)
            {
                return new UpdateResult { State = UpdateState.Unreachable, Message = $"Update check: {feed.Name} unreachable ({e.GetType().Name}: {e.Message})." };
            }
        }

        static string Show(string v) => string.IsNullOrEmpty(v) ? "this" : v;
    }
}
