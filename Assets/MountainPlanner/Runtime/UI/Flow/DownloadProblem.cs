namespace MountainPlanner.UI.Flow
{
    /// <summary>What kind of failure stopped a download (S11: errors say what happened and what to do, never a raw exception).</summary>
    public enum ProblemKind { NoConnection, OfflineMode, DiskFull, AccessDenied, ServerError, Unknown }

    /// <summary>A download failure in the player's words. The raw exception goes to the log only.</summary>
    public sealed class DownloadProblem
    {
        public ProblemKind Kind;
        /// <summary>What happened, as a heading: "No connection".</summary>
        public string Title = "";
        /// <summary>What it means and what to do, one or two sentences.</summary>
        public string Text = "";

        /// <summary>The download waits and continues by itself (a lost connection, or offline mode until it's turned off).</summary>
        public bool Waits => Kind == ProblemKind.NoConnection || Kind == ProblemKind.OfflineMode;

        public static DownloadProblem Of(ProblemKind kind, long bytesNeeded = 0)
        {
            switch (kind)
            {
                case ProblemKind.NoConnection:
                    return new DownloadProblem { Kind = kind, Title = "No connection", Text = "The download continues by itself when the connection is back. What arrived so far is kept." };
                case ProblemKind.OfflineMode:
                    return new DownloadProblem { Kind = kind, Title = "Offline mode is on", Text = "The download continues when you turn offline mode off. What arrived so far is kept." };
                case ProblemKind.DiskFull:
                    return new DownloadProblem
                    {
                        Kind = kind, Title = "Not enough disk space",
                        Text = (bytesNeeded > 0 ? $"The download needs about {LibraryViewModel.Disk(bytesNeeded)} more. " : "") +
                               "Free some space, or move the library in Settings › Data, then try again. What arrived so far is kept.",
                    };
                case ProblemKind.AccessDenied:
                    return new DownloadProblem { Kind = kind, Title = "The library folder can't be written", Text = "Choose another folder in Settings › Data, or check the folder's permissions, then try again." };
                case ProblemKind.ServerError:
                    return new DownloadProblem { Kind = kind, Title = "A map service didn't answer properly", Text = "It may be busy or down for a while. Try again later; what arrived so far is kept." };
                default:
                    return new DownloadProblem { Kind = ProblemKind.Unknown, Title = "The download stopped", Text = "Try again; what arrived so far is kept. If it stops again, the game's log (Player.log) has the details." };
            }
        }
    }
}
