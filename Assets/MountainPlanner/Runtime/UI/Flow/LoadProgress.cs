using UnityEngine;

namespace MountainPlanner.UI.Flow
{
    /// <summary>The stages of opening a mountain, in order (task P2-03.1).</summary>
    public enum LoadStage { Package, Preparing, Terrain, GroundCover, Snowpack, Cliffs, Forest }

    /// <summary>Where one stage stands. Skipped: it doesn't apply to this open (a current cache, no cliffs, no forest).</summary>
    public enum LoadState { Waiting, Current, Done, Skipped }

    /// <summary>
    /// Opening a mountain, stage by stage (task P2-03.1): what the loading box's bar and its Details list show. The
    /// opener (ResortOpener) writes it as it goes, always on the main thread; the flow reads it each frame and redraws
    /// only when <see cref="Version"/> moves. One per viewer, so an opener still running for a scene that has gone can
    /// only write into its own.
    /// </summary>
    public sealed class LoadProgress
    {
        public const int StageCount = 7;

        /// <summary>
        /// Each stage's share of an open, from a cached open of Jackson Hole 5 km (Player.log): terrain about 0.88 after
        /// reading the package, ground cover 0.06, the snowpack 0.03, cliffs and forest the rest. Preparing terrain (a first
        /// open builds the cache, several times longer than the open itself) counts only when it happens.
        /// </summary>
        static readonly float[] Share = { 0.02f, 3f, 0.88f, 0.06f, 0.03f, 0.005f, 0.005f };

        readonly LoadState[] _state = new LoadState[StageCount];
        readonly long[] _done = new long[StageCount], _total = new long[StageCount];
        readonly double[] _start = new double[StageCount], _end = new double[StageCount];
        readonly double _opened;
        float _shown;

        public LoadProgress() : this(Time.realtimeSinceStartupAsDouble) { }

        /// <summary>A progress record whose clock starts at <paramref name="now"/> (tests pass their own times).</summary>
        public LoadProgress(double now) => _opened = now;

        /// <summary>Moves whenever anything here changes.</summary>
        public int Version { get; private set; }
        /// <summary>Set when opening failed: the stages stop where they were.</summary>
        public bool Failed { get; private set; }

        public LoadState State(LoadStage s) => _state[(int)s];
        public long Done(LoadStage s) => _done[(int)s];
        public long Total(LoadStage s) => _total[(int)s];

        /// <summary>How long a stage took, or has taken so far; 0 before it starts.</summary>
        public double Seconds(LoadStage s, double now)
        {
            int i = (int)s;
            return _state[i] == LoadState.Done ? _end[i] - _start[i] : _state[i] == LoadState.Current ? now - _start[i] : 0;
        }

        /// <summary>From the start of this record to the last stage that finished, or to <paramref name="now"/> while still opening.</summary>
        public double TotalSeconds(double now) => (Complete ? LastEnd() : now) - _opened;

        /// <summary>Every stage is done or skipped.</summary>
        public bool Complete
        {
            get
            {
                for (int i = 0; i < StageCount; i++)
                    if (_state[i] != LoadState.Done && _state[i] != LoadState.Skipped) return false;
                return true;
            }
        }

        public void Begin(LoadStage s, long total = 0) => Begin(s, total, Time.realtimeSinceStartupAsDouble);

        public void Begin(LoadStage s, long total, double now)
        {
            int i = (int)s;
            if (_state[i] == LoadState.Done || _state[i] == LoadState.Skipped) return;
            _state[i] = LoadState.Current;
            _start[i] = now;
            if (total > 0) _total[i] = total;
            Version++;
        }

        /// <summary>
        /// How far a stage has got. Reports can arrive out of order (the cache build counts tiles from many threads), so
        /// only the furthest is kept; nothing moves once the stage is done.
        /// </summary>
        public void Advance(LoadStage s, long done, long total = 0)
        {
            int i = (int)s;
            if (_state[i] == LoadState.Done || _state[i] == LoadState.Skipped) return;
            if (total > 0 && total != _total[i]) { _total[i] = total; Version++; }
            if (done <= _done[i]) return;
            _done[i] = done;
            Version++;
        }

        public void End(LoadStage s) => End(s, Time.realtimeSinceStartupAsDouble);

        public void End(LoadStage s, double now)
        {
            int i = (int)s;
            if (_state[i] == LoadState.Done || _state[i] == LoadState.Skipped) return;
            if (_state[i] == LoadState.Waiting) _start[i] = now;
            _state[i] = LoadState.Done;
            _end[i] = now;
            if (_total[i] > 0) _done[i] = _total[i];
            Version++;
        }

        public void Skip(LoadStage s)
        {
            int i = (int)s;
            if (_state[i] != LoadState.Waiting) return;
            _state[i] = LoadState.Skipped;
            Version++;
        }

        public void Fail()
        {
            if (Failed) return;
            Failed = true;
            Version++;
        }

        /// <summary>
        /// The whole open, 0–1, weighted by each stage's usual share. Skipped stages drop out; Preparing counts only once it
        /// has begun. It never goes backwards, even when Preparing joins after the package was read.
        /// </summary>
        public float Fraction
        {
            get
            {
                if (Complete) return _shown = 1;
                float sum = 0, got = 0;
                for (int i = 0; i < StageCount; i++)
                {
                    if (_state[i] == LoadState.Skipped) continue;
                    if (i == (int)LoadStage.Preparing && _state[i] == LoadState.Waiting) continue;
                    sum += Share[i];
                    if (_state[i] == LoadState.Done) got += Share[i];
                    else if (_state[i] == LoadState.Current && _total[i] > 0) got += Share[i] * Mathf.Clamp01((float)_done[i] / _total[i]);
                }
                float f = sum > 0 ? got / sum : 0;
                if (f > _shown) _shown = f;
                return _shown;
            }
        }

        double LastEnd()
        {
            double last = _opened;
            for (int i = 0; i < StageCount; i++)
                if (_state[i] == LoadState.Done && _end[i] > last) last = _end[i];
            return last;
        }
    }
}
