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

    /// <summary>
    /// The ground a terrain brush moves as it paints (task 15, owner BR5): heights under the brush every frame, then the
    /// whole pending ground (trees, cover, snow) when a stroke ends, and back to the committed ground on cancel.
    /// </summary>
    public interface ILiveGround
    {
        /// <summary>
        /// This frame's heights over <paramref name="dirty"/> (lattice points) from <paramref name="ground"/>: the stroke in
        /// hand (<see cref="BrushRun.Live"/>), or a set of edits to put back.
        /// </summary>
        void Live(IPointDeltas ground, PointRect dirty);
        /// <summary>Lays <paramref name="edits"/> over the mountain in full, trees and all (the committed ground again on a forced close).</summary>
        void Show(TerrainEdits edits);
        /// <summary>
        /// BR21: the cover, the trees' footing and the snow catch up with <paramref name="ground"/>, whose heights the live upload
        /// put on the tiles already; <paramref name="clearing"/>'s trees are the ones out. Done when the task is.
        /// </summary>
        System.Threading.Tasks.Task CatchUp(TerrainEdits ground, TerrainEdits clearing);
        /// <summary>BR21: <paramref name="ground"/> on screen, heights too (Ctrl+Z, Revert), with <paramref name="clearing"/>'s trees out.</summary>
        System.Threading.Tasks.Task ShowGround(TerrainEdits ground, TerrainEdits clearing);
        /// <summary>BR13: the Contours layer on while a brush is armed; off puts back what the player had.</summary>
        void ContoursWhileArmed(bool armed);
        /// <summary>How long the last live upload took, ms (the brush bench).</summary>
        double LastLiveMs { get; }
    }
}
