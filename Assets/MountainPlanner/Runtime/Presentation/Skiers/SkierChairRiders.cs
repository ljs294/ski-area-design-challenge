using System;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.Simulation;
using Unity.Collections;
using Unity.Mathematics;
using UnityEngine;

namespace MountainPlanner.Presentation.Skiers
{
    /// <summary>
    /// Task 4.22: seated figures on the chairs. Rides are compressed (the 4.02 time model: a guest is off at the top long
    /// before the chair it boarded gets there), so a seated figure does not follow its guest: when a guest starts a ride,
    /// a figure takes the next free seat on the chair that was leaving the bottom at that moment, and stays on that chair
    /// until it reaches the top. The chairs go up loaded and come down empty. The seated figure wears its own keyed outfit,
    /// not its guest's, so the guest (already skiing again) and its figure on the chair are not twins. The guest's own
    /// dot, card and Follow are untouched. Main thread, no allocation per frame.
    /// </summary>
    public sealed class SkierChairRiders : IDisposable
    {
        sealed class Lift
        {
            public LiftView View;
            public double Seconds;
            public bool On;
            public int Seats;
            public double[] Board = Array.Empty<double>();   // rope seconds the seat's rider left the bottom; NaN = empty
            public uint[] Key = Array.Empty<uint>();
        }

        Lift[] _lifts = Array.Empty<Lift>();
        readonly uint[] _seen;
        readonly int _cap;
        NativeArray<float4> _seated;
        NativeArray<uint> _seatedKey;

        /// <summary>This frame's seated figures: the seat (xyz) and the chair's heading (w, radians, 0 = +z).</summary>
        public NativeArray<float4> Seated => _seated;
        /// <summary>Per seated figure: its outfit, with the top bit set for a snowboarder.</summary>
        public NativeArray<uint> SeatedKey => _seatedKey;
        public int Count { get; private set; }

        public SkierChairRiders(int cap)
        {
            _cap = Math.Max(1, cap);
            _seen = new uint[_cap];
            _seated = new NativeArray<float4>(_cap, Allocator.Persistent);
            _seatedKey = new NativeArray<uint>(_cap, Allocator.Persistent);
        }

        /// <summary>
        /// Design lift <paramref name="index"/> (the guests' <c>SkierRender.Index</c>) is drawn by <paramref name="view"/>
        /// with its chairs at <paramref name="ropeSeconds"/>; <paramref name="on"/> when its riders are seated figures
        /// (a compressed lift whose chairs have seats, shown). A different view (the lift rebuilt) empties its seats.
        /// </summary>
        public void SetLift(int index, LiftView view, double ropeSeconds, bool on)
        {
            if (index < 0) return;
            if (index >= _lifts.Length)
            {
                int n = Math.Max(index + 1, _lifts.Length * 2);
                var grown = new Lift[n];
                Array.Copy(_lifts, grown, _lifts.Length);
                for (int i = _lifts.Length; i < n; i++) grown[i] = new Lift();
                _lifts = grown;
            }
            var l = _lifts[index];
            on &= view != null && view.SeatLocal.Length > 0 && view.CarrierCount > 0 && view.RideTime > 0;
            if (!ReferenceEquals(l.View, view) || (on && l.Board.Length != view.CarrierCount * view.SeatLocal.Length))
            {
                l.View = view;
                l.Seats = view != null ? view.SeatLocal.Length : 0;
                int seats = view != null ? view.CarrierCount * l.Seats : 0;
                l.Board = new double[seats];
                l.Key = new uint[seats];
                for (int i = 0; i < seats; i++) l.Board[i] = double.NaN;
            }
            l.Seconds = ropeSeconds;
            l.On = on;
        }

        /// <summary>Seats the guests who started a ride since last frame and lists every rider still going up.</summary>
        public void Update(NativeArray<SkierRender> records, int count, int outfits)
        {
            count = Math.Min(count, records.Length);
            for (int i = 0; i < count; i++)
            {
                var r = records[i];
                if (r.Slot >= _seen.Length) continue;
                if (r.State != (byte)GuestState.Riding) { _seen[r.Slot] = 0; continue; }
                if (_seen[r.Slot] == r.Serial) continue;
                _seen[r.Slot] = r.Serial;
                if (r.Index >= _lifts.Length || !_lifts[r.Index].On) continue;
                Seat(_lifts[r.Index], r, outfits);
            }

            Count = 0;
            foreach (var l in _lifts)
            {
                if (!l.On) continue;
                var v = l.View;
                float loop = v.Carriers.LoopTime;
                float keep = v.RideTime + v.CarrierInterval;
                for (int k = 0; k < v.CarrierCount && Count < _cap; k++)
                {
                    int first = k * l.Seats;
                    bool any = false;
                    for (int s = 0; s < l.Seats && !any; s++)
                    {
                        double b = l.Board[first + s];
                        if (double.IsNaN(b)) continue;
                        if (l.Seconds - b > keep || l.Seconds < b - 10.0) l.Board[first + s] = double.NaN;   // at the top (or the clock went back)
                        else any = true;
                    }
                    if (!any || k == v.RiddenCarrier) continue;
                    // Only on the loaded rope: from leaving the loading point to the top.
                    float since = ((v.CarrierTime(k, l.Seconds) - v.BoardTime) % loop + loop) % loop;
                    if (since > v.RideTime) continue;
                    if (!v.CarrierPose(k, l.Seconds, out _, out var hang)) continue;
                    Vector3 fwd = hang.GetColumn(2);
                    float yaw = Mathf.Atan2(fwd.x, fwd.z);
                    for (int s = 0; s < l.Seats && Count < _cap; s++)
                    {
                        if (double.IsNaN(l.Board[first + s])) continue;
                        var p = hang.MultiplyPoint3x4(v.SeatLocal[s]);
                        _seated[Count] = new float4(p.x, p.y, p.z, yaw);
                        _seatedKey[Count] = l.Key[first + s];
                        Count++;
                    }
                }
            }
        }

        void Seat(Lift l, in SkierRender r, int outfits)
        {
            var v = l.View;
            float loop = v.Carriers.LoopTime;
            // The guest's ride began (now - entry) ago on this lift's own rope clock (rope ms cut to 32 bits, as
            // SkierLayer.SetLift keeps it). Signed: the chairs are drawn a little behind the newest snapshot, so a guest who
            // has just boarded can be a fraction of a second ahead of them.
            uint nowMs = unchecked((uint)(long)Math.Round(l.Seconds * 1000.0));
            double board = l.Seconds - unchecked((int)(nowMs - r.EntryTick)) / 1000.0;
            if (l.Seconds - board > v.RideTime) { _late++; return; }   // seen late: that chair is already at the top
            // The carrier at the loading point then: interval × k + board ≡ BoardTime (mod loop).
            double x = ((v.BoardTime - board) % loop + loop) % loop;
            int k = (int)Math.Round(x / v.CarrierInterval) % v.CarrierCount;
            uint outfit = outfits > 0 ? ((r.Serial ^ 0x5bd1e995u) * 0x85EBCA6Bu >> 7) % (uint)outfits : 0;
            uint key = outfit | (SkierPlaceJob.IsBoarder(r.Serial) ? 0x8000_0000u : 0);
            int first = k * l.Seats;
            for (int s = 0; s < l.Seats; s++)
            {
                double b = l.Board[first + s];
                if (!double.IsNaN(b) && l.Seconds - b <= v.RideTime + v.CarrierInterval) continue;
                l.Board[first + s] = board;
                l.Key[first + s] = key;
                _boarded++;
                return;
            }
            _full++;
        }

        int _boarded, _late, _full;

        /// <summary>For a review capture: the seated rider nearest <paramref name="near"/> this frame, if any.</summary>
        public bool NearestSeat(Vector3 near, out Vector3 at)
        {
            at = default;
            float best = float.MaxValue;
            for (int i = 0; i < Count; i++)
            {
                var q = new Vector3(_seated[i].x, _seated[i].y, _seated[i].z);
                float d = (q - near).sqrMagnitude;
                if (d < best) { best = d; at = q; }
            }
            return Count > 0;
        }

        /// <summary>For a review log: each lift's seats and ride, and how many riders were seated, seen late or found the chair full.</summary>
        public string Describe()
        {
            var b = new System.Text.StringBuilder();
            b.Append($"{_boarded} seated, {_late} seen late, {_full} found the chair full; ");
            for (int i = 0; i < _lifts.Length; i++)
            {
                var l = _lifts[i];
                if (l.View == null) continue;
                b.Append($"[{i} on {l.On}, {l.View.SeatLocal.Length} seats x {l.View.CarrierCount}, board {l.View.BoardTime:F0} s, ride {l.View.RideTime:F0} of {l.View.Carriers?.LoopTime ?? 0:F0} s] ");
            }
            return b.ToString();
        }

        public void Dispose()
        {
            if (_seated.IsCreated) _seated.Dispose();
            if (_seatedKey.IsCreated) _seatedKey.Dispose();
        }
    }
}
