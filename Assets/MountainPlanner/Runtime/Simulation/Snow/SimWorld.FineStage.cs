using MountainPlanner.Domain.Snow;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.08 PR A: the fine trail snow to the screen. Every listed tile goes due when the fine field is made and when an
    /// hour lands (the hour's job, the wear and the night's grooming all write it), and when a command paints it; due tiles
    /// are copied into the host's <see cref="FineStagingRing"/> a few a tick, never while an hour's job is writing the field.
    /// Not the simulation's state: the ring is the screen's, so nothing here is hashed or saved.
    /// </summary>
    public sealed partial class SimWorld
    {
        FineTiles _fineTiles;
        bool[] _fineDue = System.Array.Empty<bool>();
        int _fineDueCount, _fineCursor;

        /// <summary>The fine field's tiles (null without one): the screen's atlas is laid out by the same list.</summary>
        internal FineTiles FineTiles => _fineTiles;

        /// <summary>The fine field changed everywhere (made, or an hour landed): every tile is due.</summary>
        internal void FineAllDue()
        {
            if (_fine == null) { _fineTiles = null; _fineDueCount = 0; return; }
            if (_fineTiles == null || !ReferenceEquals(_fineTiles.Layout, _fine.Layout))
            {
                _fineTiles = new FineTiles(_fine.Layout);
                _fineDue = new bool[_fineTiles.Count];
                _fineCursor = 0;
            }
            for (int t = 0; t < _fineDue.Length; t++) _fineDue[t] = true;
            _fineDueCount = _fineDue.Length;
        }

        /// <summary>The tiles under 8 m cells x0..x1, y0..y1 (inclusive) are due (a paint).</summary>
        void FineDue(int x0, int y0, int x1, int y1)
        {
            if (_fineTiles == null) return;
            int per = FineTiles.CellsPerTile;
            for (int ty = System.Math.Max(0, y0 / per); ty <= System.Math.Min(_fineTiles.TilesY - 1, y1 / per); ty++)
                for (int tx = System.Math.Max(0, x0 / per); tx <= System.Math.Min(_fineTiles.TilesX - 1, x1 / per); tx++)
                {
                    int s = _fineTiles.Slot[ty * _fineTiles.TilesX + tx];
                    if (s >= 0 && !_fineDue[s]) { _fineDue[s] = true; _fineDueCount++; }
                }
        }

        /// <summary>
        /// Copies up to <see cref="Pacing.StageTilesPerTick"/> due fine tiles into <paramref name="ring"/>, round-robin; none
        /// while an hour's job is in flight (it writes the fine cells). Stops when the ring is full. Returns the tiles staged.
        /// </summary>
        public int StageFine(FineStagingRing ring)
        {
            if (ring == null || _fineDueCount == 0 || _fine == null || _snowRunning) return 0;
            int count = _fineDue.Length, staged = 0;
            for (int n = 0; n < count && staged < Pacing.StageTilesPerTick && _fineDueCount > 0; n++)
            {
                int t = _fineCursor;
                _fineCursor = (_fineCursor + 1) % count;
                if (!_fineDue[t]) continue;
                if (!ring.TryWrite(_fineTiles, _fine, t)) break;
                _fineDue[t] = false;
                _fineDueCount--;
                staged++;
            }
            return staged;
        }

        /// <summary>Fine tiles waiting to be staged (the host keeps coming back while any are, paused or not).</summary>
        internal int FineTilesDue => _fine == null ? 0 : _fineDueCount;

        /// <summary>The layout the fine tiles are staged for (0: none), for the snapshot.</summary>
        internal ulong FineLayoutHash => _fine?.Layout.Hash() ?? 0;
    }
}
