using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.06: the sun's clock, drawn between the two newest snapshots the way <see cref="RopeClock"/> draws the chairs, so
    /// the sun moves every frame rather than in a snapshot's whole seconds (a fast clock jumped it, and every shadow with it).
    /// Never ahead of the newest snapshot (no guessing); a hitch or a skip catches up to the older one. Engine-free and
    /// allocation-free.
    /// </summary>
    public sealed class FaceClock
    {
        long _newTick = -1, _oldTick = -1, _seq = -1, _newFaceMs, _oldFaceMs;
        double _drawTick;
        int _ticksPerSecond;

        /// <summary>The tick the sun is drawn at.</summary>
        public double DrawTick => _drawTick;

        /// <summary>Once a frame: the newest snapshot and the real seconds since the last frame.</summary>
        public void Advance(SimSnapshot s, double realSeconds, int tickMs)
        {
            if (s != null && s.Seq != _seq)
            {
                _seq = s.Seq;
                if (s.Tick != _newTick)
                {
                    _oldTick = _newTick; _oldFaceMs = _newFaceMs;
                    _newTick = s.Tick; _newFaceMs = s.FaceMs;
                    // The first snapshot, or a clock that went back (a load): start at it.
                    if (_oldTick < 0 || _newTick < _oldTick || _newFaceMs < _oldFaceMs)
                    {
                        _oldTick = _newTick; _oldFaceMs = _newFaceMs; _drawTick = _newTick;
                    }
                }
                _ticksPerSecond = s.Paused ? 0 : s.TicksPerRealTick * (1000 / Math.Max(1, tickMs));
            }
            if (_newTick < 0) return;
            _drawTick += realSeconds * _ticksPerSecond;
            if (_drawTick > _newTick) _drawTick = _newTick;
            if (_drawTick < _oldTick) _drawTick = _oldTick;
        }

        /// <summary>Face milliseconds the drawn time is behind the newest snapshot (0 when it has caught up).</summary>
        public double BehindMs
        {
            get
            {
                if (_newTick < 0 || _newTick <= _oldTick) return 0;
                double f = (_drawTick - _oldTick) / (_newTick - _oldTick);
                return (_newFaceMs - _oldFaceMs) * (1 - f);
            }
        }

        /// <summary>
        /// Seconds past the newest snapshot's whole second (its <see cref="SimSnapshot.SecondOfDay"/>) that the sun is drawn
        /// at: the snapshot's own milliseconds less how far behind the drawn time is. Negative while it trails.
        /// </summary>
        public double SecondsPastSnapshot => _newTick < 0 ? 0 : ((_newFaceMs % 1000) - BehindMs) / 1000.0;
    }
}
