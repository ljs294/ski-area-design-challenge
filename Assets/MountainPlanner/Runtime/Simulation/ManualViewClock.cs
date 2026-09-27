using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// The iteration-1 clock: set by the time-of-day and date scrubber, and never advances on its own.
    /// It drives the sun only; snow stays flat (0.3 §5, §10).
    /// </summary>
    public sealed class ManualViewClock : IGameClock
    {
        public ManualViewClock(ViewTime start)
        {
            if (!start.IsValid) throw new ArgumentException("Start time must be a constructed ViewTime.", nameof(start));
            Now = start;
        }

        public ViewTime Now { get; private set; }

        public event Action<ViewTime> Changed;

        /// <summary>Moves the view to <paramref name="time"/>; raises <see cref="Changed"/> only if it differs.</summary>
        public void Set(ViewTime time)
        {
            if (!time.IsValid) throw new ArgumentException("Time must be a constructed ViewTime.", nameof(time));
            if (time == Now) return;
            Now = time;
            Changed?.Invoke(time);
        }
    }
}
