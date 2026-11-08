using System;
using System.Threading;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// The real time the host paces itself by, in microseconds from any fixed start. App passes a stopwatch; tests pass a
    /// fake they move by hand. Wall clocks stay out of the simulation (AGENTS.md): this is the one door, and nothing it
    /// says reaches the world's state (TM-03).
    /// </summary>
    public interface IRealTime
    {
        long Microseconds { get; }
    }

    /// <summary>
    /// SI-02: the worker that owns a <see cref="SimWorld"/>. It turns real time into ticks at the chosen speed (TM-02 v2), takes
    /// commands from one queue at tick boundaries (SI-03 v2), runs skips at full fidelity in batches (TM-06 v2, TM-14) and
    /// publishes snapshots through a triple buffer (SI-16). Speed and skips never change a result (TM-03): they change only how
    /// many steps run per real second. When the machine can't keep up the clock runs slower and says so (SI-11 honest
    /// slowdown); nothing queues up to catch up later beyond <see cref="Pacing.MaxBacklogRealTicks"/>.
    ///
    /// Threads: <see cref="Send"/> and <see cref="Latest"/> are for any one reader thread; everything else runs on the worker.
    /// Constructed without a thread (<c>threaded: false</c>), the caller drives <see cref="Service"/> itself (tests, the
    /// headless runner).
    /// </summary>
    public sealed class SimulationHost : IDisposable
    {
        public readonly Pacing Pacing;
        readonly SimWorld _world;
        readonly IRealTime _time;
        readonly CommandQueue _queue;
        readonly SnapshotBuffers _buffers;
        readonly Wake _wake = new Wake();
        /// <summary>SI-17: where the worker's snow tiles wait for the screen (the reader's, allocated once).</summary>
        public readonly TileStagingRing Staging = new TileStagingRing();
        readonly Thread _thread;
        volatile bool _stop;

        // ---- the worker's own ----
        long _lastMicros;
        long _accMicros;          // motion microseconds owed
        int _speed = 1;
        bool _paused = true;
        bool _dirty = true;
        long _ticksRun;
        SkipKind _skip;
        long _skipFrom, _skipTo;
        // SI-11: what the clock was asked for and what it ran, over about a second
        long _windowStart, _windowAsked, _windowRan;
        int _sustained = 1000;
        // BG-01: per-step timings, µs
        readonly int[] _timings = new int[256];
        readonly int[] _sorted = new int[256];
        int _timingCount, _timingNext;
        long _published;

        /// <summary>The longest a skip batch runs before it publishes its progress, real µs.</summary>
        public const int SkipBatchMicros = 50_000;
        /// <summary>A skip batch's tick cap, for a real-time source that doesn't move (tests).</summary>
        public const int SkipBatchMaxSteps = 20_000;

        public SimulationHost(SimWorld world, IRealTime time, bool threaded = true)
        {
            _world = world ?? throw new ArgumentNullException(nameof(world));
            _time = time ?? throw new ArgumentNullException(nameof(time));
            Pacing = world.Pacing;
            _queue = new CommandQueue(512);
            _buffers = new SnapshotBuffers(Pacing);
            _lastMicros = _windowStart = _time.Microseconds;
            PublishNow();
            if (threaded)
            {
                _thread = new Thread(Run) { IsBackground = true, Name = "Simulation" };
                _thread.Start();
            }
        }

        /// <summary>The world, for the caller of <see cref="Service"/> only (no thread): never touch it while a worker runs.</summary>
        public SimWorld WorldUnsafe => _world;

        /// <summary>Queues a command for the worker (refused, with an event, when the queue is full).</summary>
        public bool Send(in SimCommand c)
        {
            bool ok = _queue.TryAdd(c);
            _wake.Set();
            return ok;
        }

        public bool Send(SimCommandKind kind, long a = 0, object payload = null) => Send(SimCommand.Of(kind, a, payload));

        /// <summary>The newest snapshot (the reader's to hold until it asks again).</summary>
        public SimSnapshot Latest => _buffers.Latest;

        void Run()
        {
            while (!_stop)
            {
                int wait = Service();
                if (_stop) break;
                _wake.Wait(wait);
            }
        }

        /// <summary>
        /// One pass of the worker: commands, then the ticks the real time owes (or a skip's batch), then a snapshot if
        /// anything changed. Returns how long the worker may wait before the next pass, ms (−1: until a command).
        /// </summary>
        public int Service()
        {
            long now = _time.Microseconds;
            long dt = Math.Max(0, now - _lastMicros);
            _lastMicros = now;
            TakeCommands();
            int wait;
            if (_skip != SkipKind.None)
            {
                RunSkipBatch();
                wait = _skip != SkipKind.None ? 0 : NextWait();
            }
            else
            {
                if (!_paused) RunOwed(dt);
                wait = NextWait();
            }
            UpdateSustained(now);
            // SI-17: the snow's due tiles to the screen, after the steps (and while paused, so an edit's snow still shows).
            if (_world.StageSnow(Staging, _paused || _skip != SkipKind.None) > 0) _dirty = true;
            if (_dirty) PublishNow();
            // Tiles still due (the ring is full, or a tile staged a moment ago): come back soon rather than sleep.
            if (_world.Snow.TilesDue > 0 && (wait < 0 || wait > 10)) wait = 10;
            return wait;
        }

        int NextWait()
        {
            if (_paused) return -1;
            long tickMicros = Pacing.TickMs * 1000L;
            long need = tickMicros - _accMicros;
            int perSecond = Pacing.Speeds[_speed - 1];
            long realMicros = need <= 0 ? 0 : need / perSecond;
            return (int)Math.Max(1, Math.Min(20, realMicros / 1000));
        }

        void TakeCommands()
        {
            while (_queue.TryTake(out var c))
            {
                _dirty = true;
                if (c.IsHosts) { HostCommand(c); continue; }
                // SI-03 v2: while paused (and between steps) a world command applies at once; while running it waits for the
                // next tick's start. Both stamp it with the tick it took effect at.
                if (_paused || _skip != SkipKind.None) _world.ApplyNow(c);
                else _world.Stage(c);
            }
        }

        void HostCommand(in SimCommand c)
        {
            switch (c.Kind)
            {
                case SimCommandKind.SetSpeed:
                    _speed = (int)Math.Max(1, Math.Min(Pacing.Speeds.Length, c.A));
                    _paused = false;
                    break;
                case SimCommandKind.Pause:
                    _paused = true;
                    break;
                case SimCommandKind.Resume:
                    _paused = false;
                    break;
                case SimCommandKind.TogglePause:
                    _paused = !_paused;
                    break;
                case SimCommandKind.SkipToMorning:
                case SimCommandKind.SkipToNextThird:
                case SimCommandKind.SkipToNextMonth:
                {
                    var kind = c.Kind == SimCommandKind.SkipToMorning ? SkipKind.Morning
                        : c.Kind == SimCommandKind.SkipToNextThird ? SkipKind.NextThird : SkipKind.NextMonth;
                    if (_skip != SkipKind.None || !_world.SkipTarget(kind, out long target)) { _world.Reject(c, SimRefusal.SkipNotAllowed); break; }
                    _skip = kind;
                    _skipFrom = _world.FaceMs;
                    _skipTo = target;
                    _world.Events.Add(_world.Tick, SimEventType.SkipStarted, -1, target, (ushort)kind);
                    break;
                }
                case SimCommandKind.SaveState:
                    if (c.Payload is SimStateRequest request) request.Answer(_world);
                    break;
                case SimCommandKind.CancelSkip:
                    if (_skip == SkipKind.None) { _world.Reject(c, SimRefusal.NoChange); break; }
                    _world.Events.Add(_world.Tick, SimEventType.SkipCancelled, -1, _world.FaceMs, (ushort)_skip);
                    EndSkip();
                    break;
            }
        }

        /// <summary>Ticks the real time owes at the speed, each step timed (BG-01); the backlog is capped (honest slowdown).</summary>
        void RunOwed(long realMicros)
        {
            long perSecond = Pacing.Speeds[_speed - 1];
            long tickMicros = Pacing.TickMs * 1000L;
            long asked = realMicros * perSecond;
            _windowAsked += asked;
            _accMicros += asked;
            long cap = perSecond * Pacing.MaxBacklogRealTicks * tickMicros;
            if (_accMicros > cap) _accMicros = cap;
            while (_accMicros >= tickMicros && !_stop)
            {
                long owed = _accMicros / tickMicros;
                int k = RunStep((int)Math.Min(owed, Pacing.IdleTickFactor));
                _accMicros -= k * tickMicros;
                _windowRan += k * tickMicros;
                // A command arriving mid-backlog takes effect at the next tick, not after the whole backlog.
                if (_queue.Count > 0) TakeCommands();
                if (_skip != SkipKind.None) break;
            }
        }

        void RunSkipBatch()
        {
            long start = _time.Microseconds;
            int steps = 0;
            while (_world.FaceMs < _skipTo && !_stop)
            {
                RunStep(Pacing.IdleTickFactor);
                if (++steps >= SkipBatchMaxSteps || _time.Microseconds - start >= SkipBatchMicros) break;
                if (_queue.Count > 0) { TakeCommands(); if (_skip == SkipKind.None) return; }
            }
            if (_world.FaceMs >= _skipTo)
            {
                _world.Events.Add(_world.Tick, SimEventType.SkipFinished, -1, _world.FaceMs, (ushort)_skip);
                EndSkip();
            }
        }

        void EndSkip()
        {
            _skip = SkipKind.None;
            _accMicros = 0;   // the skip landed: nothing owed from before it (the player's speed and pause stay as they were)
            _dirty = true;
        }

        int RunStep(int maxTicks)
        {
            long t0 = _time.Microseconds;
            int k = _world.Step(maxTicks);
            long t1 = _time.Microseconds;
            int per = (int)Math.Min(int.MaxValue, (t1 - t0) / Math.Max(1, k));
            _timings[_timingNext] = per;
            _timingNext = (_timingNext + 1) & 255;
            if (_timingCount < 256) _timingCount++;
            _ticksRun += k;
            _dirty = true;
            return k;
        }

        void UpdateSustained(long now)
        {
            if (now - _windowStart < 1_000_000) return;
            if (!_paused && _skip == SkipKind.None && _windowAsked > 0)
            {
                int s = (int)Math.Min(1000, _windowRan * 1000 / _windowAsked);
                if (s != _sustained) { _sustained = s; _dirty = true; }
            }
            _windowStart = now;
            _windowAsked = _windowRan = 0;
        }

        void PublishNow()
        {
            var s = _buffers.Back;
            _world.Write(s);
            s.Seq = ++_published;
            s.Speed = _speed;
            s.TicksPerRealTick = Pacing.Speeds[_speed - 1];
            s.Paused = _paused;
            s.SustainedPermille = _sustained;
            s.Skip = _skip;
            long span = _skipTo - _skipFrom;
            s.SkipPermille = _skip == SkipKind.None || span <= 0 ? 0 : (int)Math.Min(1000, (_world.FaceMs - _skipFrom) * 1000 / span);
            s.TicksRun = _ticksRun;
            s.StagedTiles = Staging.Written;
            Timings(out s.TickMeanMicros, out s.TickP99Micros);
            _buffers.Publish();
            _dirty = false;
        }

        void Timings(out int mean, out int p99)
        {
            mean = p99 = 0;
            if (_timingCount == 0) return;
            long sum = 0;
            for (int i = 0; i < _timingCount; i++) { _sorted[i] = _timings[i]; sum += _timings[i]; }
            Array.Sort(_sorted, 0, _timingCount);
            mean = (int)(sum / _timingCount);
            p99 = _sorted[Math.Min(_timingCount - 1, _timingCount * 99 / 100)];
        }

        public void Dispose()
        {
            _stop = true;
            _wake.Set();
            _thread?.Join(2000);
            _wake.Dispose();
        }
    }

    /// <summary>
    /// SI-12 v2: a save's request for the simulation's state. The worker answers at the next tick boundary (between steps,
    /// or at once while paused); the caller waits a bounded time and saves without a simulation if it never comes. It's left
    /// to the collector, never disposed: a late answer must find its signal still there.
    /// </summary>
    public sealed class SimStateRequest
    {
        readonly ManualResetEventSlim _done = new ManualResetEventSlim(false);
        public byte[] Bytes { get; private set; }
        public string Error { get; private set; } = "";

        internal void Answer(SimWorld w)
        {
            try
            {
                Bytes = w.SaveState();
                w.Events.Add(w.Tick, SimEventType.StateSaved, -1, Bytes.Length);
            }
            catch (Exception e) { Error = e.Message; }
            _done.Set();
        }

        /// <summary>Waits up to <paramref name="milliseconds"/> for the state; true when it came.</summary>
        public bool Wait(int milliseconds) => _done.Wait(milliseconds) && Bytes != null;
    }

    /// <summary>
    /// The sun's clock from the simulation (0.3 §10's promise): <see cref="IGameClock"/> fed on the main thread from each
    /// snapshot, the label date as the sun's (WX-04).
    /// </summary>
    public sealed class SimGameClock : IGameClock
    {
        public ViewTime Now { get; private set; }
        public event Action<ViewTime> Changed;

        /// <summary>Takes a snapshot's time; true (and <see cref="Changed"/>) when it moved.</summary>
        public bool Feed(SimSnapshot s)
        {
            if (s == null) return false;
            var t = new ViewTime(s.ViewYear, s.ViewDayOfYear, s.SecondOfDay);
            if (t == Now) return false;
            Now = t;
            Changed?.Invoke(t);
            return true;
        }
    }

    /// <summary>
    /// RP-07 v2's render clock: chairs drawn between the two newest snapshots, never ahead of the newest (no guessing). The
    /// drawn tick moves on at the speed's rate in real time and stops at the newest; each lift's rope travel is interpolated
    /// between the two snapshots by ID. Engine-free and allocation-free after construction.
    /// </summary>
    public sealed class RopeClock
    {
        readonly ulong[] _newIds, _oldIds;
        readonly long[] _newMs, _oldMs;
        int _newCount, _oldCount;
        long _newTick = -1, _oldTick = -1, _seq = -1;
        double _drawTick;
        int _ticksPerSecond;

        public RopeClock(Pacing p)
        {
            _newIds = new ulong[p.MaxLifts]; _oldIds = new ulong[p.MaxLifts];
            _newMs = new long[p.MaxLifts]; _oldMs = new long[p.MaxLifts];
        }

        /// <summary>The tick the chairs are drawn at.</summary>
        public double DrawTick => _drawTick;

        /// <summary>Once a frame: the newest snapshot and the real seconds since the last frame.</summary>
        public void Advance(SimSnapshot s, double realSeconds, int tickMs)
        {
            if (s != null && s.Seq != _seq)
            {
                _seq = s.Seq;
                if (s.Tick != _newTick)
                {
                    Array.Copy(_newIds, _oldIds, _newCount); Array.Copy(_newMs, _oldMs, _newCount);
                    _oldCount = _newCount; _oldTick = _newTick;
                    _newCount = s.LiftCount;
                    Array.Copy(s.LiftIds, _newIds, _newCount); Array.Copy(s.RunMs, _newMs, _newCount);
                    _newTick = s.Tick;
                    if (_oldTick < 0) { _oldTick = _newTick; Array.Copy(_newIds, _oldIds, _newCount); Array.Copy(_newMs, _oldMs, _newCount); _oldCount = _newCount; _drawTick = _newTick; }
                }
                else
                {
                    // Same tick, a new design (lifts added or gone): take the newest ropes as they are.
                    _newCount = s.LiftCount;
                    Array.Copy(s.LiftIds, _newIds, _newCount); Array.Copy(s.RunMs, _newMs, _newCount);
                }
                _ticksPerSecond = s.Paused ? 0 : s.TicksPerRealTick * (1000 / Math.Max(1, tickMs));
            }
            if (_newTick < 0) return;
            _drawTick += realSeconds * _ticksPerSecond;
            if (_drawTick > _newTick) _drawTick = _newTick;
            if (_drawTick < _oldTick) _drawTick = _oldTick;   // fell behind (a skip, a hitch): catch up to the older snapshot
        }

        /// <summary>A lift's rope travel at the drawn tick, seconds; 0 for a lift no snapshot has yet.</summary>
        public double Seconds(ulong liftId)
        {
            int n = Find(_newIds, _newCount, liftId);
            if (n < 0) return 0;
            long newMs = _newMs[n];
            int o = Find(_oldIds, _oldCount, liftId);
            if (o < 0 || _newTick <= _oldTick) return newMs / 1000.0;
            long oldMs = _oldMs[o];
            double f = (_drawTick - _oldTick) / (_newTick - _oldTick);
            return (oldMs + (newMs - oldMs) * f) / 1000.0;
        }

        static int Find(ulong[] ids, int count, ulong id)
        {
            int lo = 0, hi = count - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >> 1;
                if (ids[mid] == id) return mid;
                if (ids[mid] < id) lo = mid + 1; else hi = mid - 1;
            }
            return -1;
        }
    }
}
