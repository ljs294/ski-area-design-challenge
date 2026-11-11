using System;

namespace MountainPlanner.Simulation
{
    public sealed partial class SimWorld
    {
        // ================= SN-31: the Traffic info layer's counters (task 4.13) =================
        //
        // View output only, like InspectSkier: none of this is hashed or saved, and nothing the simulation decides reads it.
        // A save opens with the window empty and it fills again within the hour, which nobody can tell from a quiet morning.

        /// <summary>SN-31: the rolling hour is this many buckets of five face minutes, so it never flashes empty on the hour.</summary>
        public const int TrafficBuckets = 12;
        const long TrafficBucketMs = HourMs / TrafficBuckets;

        int[] _trafficBucket = Array.Empty<int>();   // edge × TrafficBuckets
        int[] _passesToday = Array.Empty<int>();
        long _trafficBucketIndex = -1;                // the absolute bucket (FaceMs / TrafficBucketMs) last written

        void AllocTraffic(int edges)
        {
            if (_passesToday.Length == edges) return;
            _passesToday = new int[edges];
            _trafficBucket = new int[edges * TrafficBuckets];
            _trafficBucketIndex = -1;
        }

        /// <summary>Clears the buckets the clock has moved past since the last write (at most the whole ring).</summary>
        void AdvanceTraffic()
        {
            long now = FaceMs / TrafficBucketMs;
            if (now == _trafficBucketIndex) return;
            int edges = _passesToday.Length;
            long from = _trafficBucketIndex < 0 || now < _trafficBucketIndex ? now - TrafficBuckets + 1 : _trafficBucketIndex + 1;
            if (now - from >= TrafficBuckets) from = now - TrafficBuckets + 1;
            for (long b = from; b <= now; b++)
            {
                int k = (int)(((b % TrafficBuckets) + TrafficBuckets) % TrafficBuckets);
                for (int e = 0; e < edges; e++) _trafficBucket[e * TrafficBuckets + k] = 0;
            }
            _trafficBucketIndex = now;
        }

        void CountTraffic(int edge)
        {
            if (edge < 0 || edge >= _passesToday.Length) return;
            AdvanceTraffic();
            _passesToday[edge]++;
            _trafficBucket[edge * TrafficBuckets + (int)(_trafficBucketIndex % TrafficBuckets)]++;
        }

        void ResetTrafficDay()
        {
            Array.Clear(_passesToday, 0, _passesToday.Length);
            Array.Clear(_trafficBucket, 0, _trafficBucket.Length);
            _trafficBucketIndex = -1;
        }

        /// <summary>SN-31: copies the counters into the render frame being filled, growing its arrays only on a design change.</summary>
        void WriteTraffic(SkierRenderBuffers.Frame frame)
        {
            int edges = Math.Min(_onEdge.Length, _passesToday.Length);
            if (frame.EdgeOn.Length < edges)
            {
                frame.EdgeOn = new int[edges]; frame.EdgeLastHour = new int[edges]; frame.EdgeToday = new int[edges];
            }
            AdvanceTraffic();
            for (int e = 0; e < edges; e++)
            {
                frame.EdgeOn[e] = _onEdge[e];
                frame.EdgeToday[e] = _passesToday[e];
                int sum = 0, at = e * TrafficBuckets;
                for (int k = 0; k < TrafficBuckets; k++) sum += _trafficBucket[at + k];
                frame.EdgeLastHour[e] = sum;
            }
            frame.EdgeCount = edges;
        }
    }
}
