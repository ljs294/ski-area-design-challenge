using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The ground a tool shapes (task 08): the open area's terrain edit session, seen from the tools. The app starts it
    /// when a tool that needs it arms (<see cref="Start"/>); until <see cref="Ready"/> the tool waits. The earthwork folds
    /// the design's built work into terrain edits (TL3); the heights are the committed edited heights (SH-02).
    /// </summary>
    public interface IEarthworkHost
    {
        /// <summary>Starts the session if it isn't running (idempotent).</summary>
        void Start();
        /// <summary>The session is up and its earthwork knows the committed design.</summary>
        bool Ready { get; }
        DesignEarthwork Earthwork { get; }
        /// <summary>The committed edited heights.</summary>
        EditedHeights Heights { get; }
        /// <summary>Draws a pending edit's changed contours in yellow (CF-08), segments as from <see cref="PendingContours"/>.</summary>
        void ShowPending(List<double> segments);
        void ClearPending();
        /// <summary>The contour interval the pending contours use, metres (the display units' interval).</summary>
        double ContourInterval { get; }
    }
}
