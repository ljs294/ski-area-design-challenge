using System;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// TD-07 (owner R6, 2026-11-08): the game's true rating (TD-02 v2) is hidden from players, who post a sign and "guess
    /// and hope that guests like it". The dev switch <c>-truerating</c> shows what the game calculates: on the trail card
    /// and the review, the profile strip's steepest 150 ft box and label, and TD-06's estimate while painting.
    /// </summary>
    public static class TrueRating
    {
        public const string Flag = "-truerating";

        /// <summary>True when the game was started with <see cref="Flag"/>; tests may set it.</summary>
        public static bool Shown { get; set; } = Array.IndexOf(Environment.GetCommandLineArgs(), Flag) >= 0;
    }
}
