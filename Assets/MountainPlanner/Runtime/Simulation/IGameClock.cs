using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// The time the world is shown at. Iteration 1 only has <see cref="ManualViewClock"/>; a future
    /// simulation clock implements the same interface on its own thread (0.3 §10).
    /// </summary>
    public interface IGameClock
    {
        ViewTime Now { get; }

        /// <summary>Raised after <see cref="Now"/> changes, with the new time.</summary>
        event Action<ViewTime> Changed;
    }
}
