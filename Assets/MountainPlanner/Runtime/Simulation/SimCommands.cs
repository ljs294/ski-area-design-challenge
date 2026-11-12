using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-03 v2: what a command asks. The host's own (speed, pause, the skips) change how fast time runs, never a result;
    /// the world's (from <see cref="SetMountainOpen"/> on) are stamped with their tick and sequence and logged for replay.
    /// Values are logged: never renumber.
    /// </summary>
    public enum SimCommandKind : byte
    {
        None = 0,
        // ---- the host's (never change a result, TM-03) ----
        /// <summary>A = the speed, 1–4 (1×, 2×, 4×, 8×); also starts the clock, as the bar's buttons do.</summary>
        SetSpeed = 1,
        Pause = 2,
        Resume = 3,
        TogglePause = 4,
        /// <summary>TM-06 v2: finish the night, landing an hour before opening.</summary>
        SkipToMorning = 5,
        /// <summary>TM-14: before the season, to the next third of the month.</summary>
        SkipToNextThird = 6,
        /// <summary>TM-08: in summer, to the next month.</summary>
        SkipToNextMonth = 7,
        CancelSkip = 8,
        /// <summary>SI-12 v2: the state for a save, at the tick boundary. Payload = a <see cref="SimStateRequest"/>.</summary>
        SaveState = 9,
        /// <summary>
        /// Task 4.05 PR B2: the host runs no tick and no skip while held, whatever the pause, the speed or a skip asked for
        /// (the area's weather inputs are still being built: an hour stepped without them would differ from the game that
        /// saved). Pause, speed and a requested skip are kept as they are and take effect at <see cref="Release"/>.
        /// </summary>
        Hold = 10,
        Release = 11,
        // ---- the world's (stamped and logged) ----
        /// <summary>TM-11 v2: A = 1 Open, 0 Closed.</summary>
        SetMountainOpen = 20,
        /// <summary>SI-15: A = the seed (as a long's bits).</summary>
        PinSeasonSeed = 21,
        UnpinSeasonSeed = 22,
        /// <summary>SI-20: Payload = a <see cref="SimDesign"/>.</summary>
        SetDesign = 23,
        /// <summary>SI-17: Payload = a <see cref="SimSnowpack"/>, the ground's natural snow over a rectangle of the field.</summary>
        SetSnowpack = 24,
        /// <summary>LO-11's gust seam (until 4.05; tests and the demo's <c>-gust</c>): A = mph at every top station, −1 = the weather's.</summary>
        SetGust = 25,
        /// <summary>LO-13's scripted feeder (tests and the demo's <c>-feeder</c>): Payload = a <see cref="LiftFeed"/>.</summary>
        FeedLine = 26,
        /// <summary>GU-02: watch one skier by serial (0 for none). A view command: it changes no result.</summary>
        InspectSkier = 27,
        /// <summary>GU-15: put N skiers of an archetype on the hill (the style tile, the tests, the demo).</summary>
        SpawnSkiers = 28,
        /// <summary>Task 4.05: Payload = a <see cref="SimWeather"/>, the area's weather (new rows wait for October 1, WX-32 v2).</summary>
        SetWeather = 29,
        /// <summary>Task 4.05 PR B2: Payload = a <see cref="Domain.Weather.WeatherTerrain"/>, the area's static inputs (WX-35), applied at once.</summary>
        SetWeatherTerrain = 30,
    }

    /// <summary>SI-03 v2: one command. <see cref="Tick"/> and <see cref="Seq"/> are the worker's stamp, set when it applies it.</summary>
    public struct SimCommand
    {
        public SimCommandKind Kind;
        public long A;
        /// <summary>The snapshot tick it was issued against, or −1 (no staleness check: the bar's controls, the F1 panel).</summary>
        public long IssuedAgainstTick;
        public object Payload;
        public long Tick;
        public long Seq;

        public bool IsHosts => Kind >= SimCommandKind.SetSpeed && Kind <= SimCommandKind.Release;

        public static SimCommand Of(SimCommandKind kind, long a = 0, object payload = null, long issuedAgainstTick = -1) =>
            new SimCommand { Kind = kind, A = a, Payload = payload, IssuedAgainstTick = issuedAgainstTick };
    }

    /// <summary>SI-03 v2: the one ordered queue into the worker, allocated once; safe from any thread.</summary>
    public sealed class CommandQueue
    {
        readonly SimCommand[] _items;
        int _head, _count;
        readonly object _lock = new object();

        public CommandQueue(int capacity) { _items = new SimCommand[capacity]; }

        /// <summary>False when it's full (the command is refused and nothing waits).</summary>
        public bool TryAdd(in SimCommand c)
        {
            lock (_lock)
            {
                if (_count == _items.Length) return false;
                _items[(_head + _count) % _items.Length] = c;
                _count++;
                return true;
            }
        }

        public bool TryTake(out SimCommand c)
        {
            lock (_lock)
            {
                if (_count == 0) { c = default; return false; }
                c = _items[_head];
                _items[_head] = default;
                _head = (_head + 1) % _items.Length;
                _count--;
                return true;
            }
        }

        public int Count { get { lock (_lock) return _count; } }
    }

    /// <summary>
    /// A wait the worker can be woken from (a command, a stop), with no allocation per wait. Wrapping the framework's event
    /// keeps the host's loop readable and its waits in one place.
    /// </summary>
    internal sealed class Wake
    {
        readonly ManualResetEventSlim _event = new ManualResetEventSlim(false);
        public void Set() => _event.Set();
        /// <summary>Waits up to <paramref name="milliseconds"/> (−1: until woken); clears the signal either way.</summary>
        public void Wait(int milliseconds)
        {
            _event.Wait(milliseconds);
            _event.Reset();
        }
        public void Dispose() => _event.Dispose();
    }
}
