using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Runs the view time at the HUD's speeds (task P2-02): pause, and speeds 1–4 at 1, 3, 10 and 30 game minutes a real
    /// second (the mockup's rates). The caller passes the real seconds that went by, so this stays free of wall clocks
    /// (AGENTS.md); whole game seconds come out, the fraction carries to the next step. It holds its own
    /// <see cref="Now"/>: the viewer hands it to the sun (<c>SceneLighting.SetTime</c>) every
    /// <see cref="PushSeconds"/> game seconds rather than every frame, and <see cref="Adopt"/> takes a time set elsewhere
    /// (the F1 slider, -time) when nothing is pending.
    /// </summary>
    public sealed class ViewClockRunner
    {
        /// <summary>Game seconds per real second at speeds 0 (none) to 4.</summary>
        public static readonly int[] Rates = { 0, 60, 180, 600, 1800 };
        public const int MaxSpeed = 4;
        /// <summary>How far the clock runs ahead of the sun before the sun catches up (an eighth of a degree).</summary>
        public const int PushSeconds = 30;

        double _carry;
        ViewTime _pushed;

        public ViewClockRunner(ViewTime start)
        {
            if (!start.IsValid) throw new ArgumentException("Start time must be a constructed ViewTime.", nameof(start));
            Now = _pushed = start;
        }

        public ViewTime Now { get; private set; }
        public int Speed { get; private set; } = 1;
        public bool Paused { get; private set; } = true;

        /// <summary>Raised when the speed or pause changes.</summary>
        public event Action Changed;

        public void TogglePause() => SetPaused(!Paused);

        public void SetPaused(bool paused)
        {
            if (paused == Paused) return;
            Paused = paused;
            _carry = 0;
            Changed?.Invoke();
        }

        /// <summary>A speed from 1 to 4; choosing one also starts the clock (as the mockup's speed arrows do).</summary>
        public void SetSpeed(int speed)
        {
            speed = Math.Max(1, Math.Min(MaxSpeed, speed));
            if (speed == Speed && !Paused) return;
            Speed = speed;
            Paused = false;
            Changed?.Invoke();
        }

        /// <summary>Moves the clock on by <paramref name="realSeconds"/> at the current speed; returns true if <see cref="Now"/> changed.</summary>
        public bool Advance(double realSeconds)
        {
            if (Paused || !(realSeconds > 0)) return false;
            _carry += realSeconds * Rates[Speed];
            long whole = (long)Math.Floor(_carry);
            if (whole <= 0) return false;
            _carry -= whole;
            Now = Add(Now, whole);
            return true;
        }

        /// <summary>True when the sun is <see cref="PushSeconds"/> or more behind (or the clock just stopped); mark it with <see cref="Pushed"/>.</summary>
        public bool SunBehind => Now != _pushed && (Paused || Math.Abs(Between(Now, _pushed)) >= PushSeconds);

        /// <summary>The sun now shows <see cref="Now"/>.</summary>
        public void Pushed() => _pushed = Now;

        /// <summary>
        /// Takes a time the view was moved to elsewhere (the F1 slider, -time): if the sun's clock no longer shows what
        /// this last gave it, that time wins.
        /// </summary>
        public void Adopt(ViewTime sun)
        {
            if (!sun.IsValid || sun == _pushed) return;
            Now = _pushed = sun;
            _carry = 0;
        }

        /// <summary>A time plus whole seconds, across midnight and the new year.</summary>
        public static ViewTime Add(ViewTime t, long seconds)
        {
            long s = t.SecondOfDay + seconds;
            int year = t.Year, day = t.DayOfYear;
            long days = s >= 0 ? s / ViewTime.SecondsPerDay : (s - ViewTime.SecondsPerDay + 1) / ViewTime.SecondsPerDay;
            s -= days * ViewTime.SecondsPerDay;
            for (long k = 0; k < days; k++)
            {
                if (++day > ViewTime.DaysInYear(year)) { day = 1; year++; }
            }
            for (long k = 0; k > days; k--)
            {
                if (--day < 1) { year--; day = ViewTime.DaysInYear(year); }
            }
            return new ViewTime(year, day, (int)s);
        }

        /// <summary>Seconds from <paramref name="b"/> to <paramref name="a"/>; across a new year, simply far.</summary>
        static long Between(ViewTime a, ViewTime b) => a.Year == b.Year
            ? (long)(a.DayOfYear - b.DayOfYear) * ViewTime.SecondsPerDay + a.SecondOfDay - b.SecondOfDay
            : (a.Year - b.Year) * 366L * ViewTime.SecondsPerDay;
    }
}
