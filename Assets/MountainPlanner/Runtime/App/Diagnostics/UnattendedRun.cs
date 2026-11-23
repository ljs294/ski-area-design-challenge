using System;
using UnityEngine;

namespace MountainPlanner.App.Diagnostics
{
    /// <summary>
    /// Task 4.26 BB23: a run nobody watches quits with an error code when its setup fails, instead of waiting on an error
    /// screen until something kills it. A capture run given a bad <c>-opensave</c> threw in the viewer's start, which
    /// skipped every capture switch after it, so the player never quit and held the render lane for 2 hours (4.08,
    /// 2026-11-23). The rule doesn't depend on which capture switch was asked for: a run is unattended in batch mode, or
    /// as a job in a GPU lane (<c>tools/gpu/gpu.ps1</c> sets <c>MP_GPU_LANE</c> for every command it runs). The game
    /// played by hand keeps its error screen, and so does the editor (tests check their own errors; it can't quit).
    /// </summary>
    public static class UnattendedRun
    {
        /// <summary>The exit code of an unattended run whose setup failed.</summary>
        public const int SetupFailedExitCode = 1;

        /// <summary>The variable gpu.ps1 sets to the lane (measure or render) of every command it runs.</summary>
        public const string LaneVariable = "MP_GPU_LANE";

        /// <summary>Whether this run is unattended: batch mode, or a GPU lane's job.</summary>
        public static bool Active => IsUnattended(Application.isEditor, Application.isBatchMode, Environment.GetEnvironmentVariable(LaneVariable));

        /// <summary>The rule itself: a player (not the editor) in batch mode, or with any lane named.</summary>
        public static bool IsUnattended(bool editor, bool batchMode, string lane) => !editor && (batchMode || !string.IsNullOrWhiteSpace(lane));

        /// <summary>
        /// A setup step failed (<paramref name="what"/>, and the exception if there is one, which is logged here): an
        /// unattended run logs why and quits with <see cref="SetupFailedExitCode"/>. Returns whether it is quitting
        /// (false when someone is at the screen, which then shows the error as before).
        /// </summary>
        public static bool FailSetup(string what, Exception e = null)
        {
            if (e != null) Debug.LogException(e);
            if (!Active) return false;
            Debug.LogError($"[Unattended] setup failed ({what}): quitting with exit code {SetupFailedExitCode}");
            Application.Quit(SetupFailedExitCode);
            return true;
        }
    }
}
