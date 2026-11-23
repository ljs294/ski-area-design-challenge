using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Task 4.29 (SN-37): <see cref="SimCommandKind.SetTrailMowing"/>'s payload, each built run's mowing (a shaped line's
    /// setting already resolved over its run), in trail id order.
    /// </summary>
    public sealed class SimMowing
    {
        public readonly ulong[] TrailIds;
        public readonly bool[] EveryAutumn;

        public SimMowing(ulong[] trailIds, bool[] everyAutumn)
        {
            TrailIds = trailIds ?? Array.Empty<ulong>();
            EveryAutumn = everyAutumn ?? Array.Empty<bool>();
        }

        /// <summary>The payload for <paramref name="runs"/> (trail id, mowing), sorted by id.</summary>
        public static SimMowing From(IReadOnlyList<KeyValuePair<ulong, TrailMowing>> runs)
        {
            int n = runs?.Count ?? 0;
            var ids = new ulong[n];
            var on = new bool[n];
            for (int i = 0; i < n; i++) { ids[i] = runs[i].Key; on[i] = runs[i].Value == TrailMowing.EveryAutumn; }
            Array.Sort(ids, on);
            return new SimMowing(ids, on);
        }

        public string Problem()
        {
            if (EveryAutumn.Length != TrailIds.Length) return "the trails don't line up";
            for (int i = 1; i < TrailIds.Length; i++) if (TrailIds[i] <= TrailIds[i - 1]) return "the trails aren't in id order";
            return null;
        }

        public bool SameAs(SimMowing o)
        {
            if (o == null || o.TrailIds.Length != TrailIds.Length) return false;
            for (int i = 0; i < TrailIds.Length; i++) if (o.TrailIds[i] != TrailIds[i] || o.EveryAutumn[i] != EveryAutumn[i]) return false;
            return true;
        }

        public static readonly SimMowing None = new SimMowing(null, null);
    }

    /// <summary>
    /// Task 4.29: a trail's grass (SN-35), the snow it adds to the cover depth (SN-36) and the autumn mowing (SN-37).
    /// - **State:** per layout trail, keyed by <see cref="_grIds"/> so a save restores before its layout arrives (sidecar
    ///   v15): the height (‰ of brush), the face days packed this winter and a flags byte.
    /// - **The day (<see cref="GrassFaceDay"/>, at each face day's midnight):** yesterday's packed hour counts; on a bare day
    ///   from April 1 the winter just gone acts once (killed or kept) and the grass grows; from September 15 until the trail
    ///   is first covered an Every autumn trail is mown once.
    /// - **The hour (<see cref="GrassPackedHour"/>, in the hour's ratings):** a covered trail open to skiers in the day's
    ///   hours, or groomed, is packed today.
    /// A world whose layout never had trails hashes and saves as before (the goldens).
    /// </summary>
    public sealed partial class SimWorld
    {
        const byte GrKnown = 1, GrActed = 2, GrMown = 4, GrSnowed = 8, GrPackedToday = 16, GrMowOn = 32, GrFlags = 63;

        ulong[] _grIds = Array.Empty<ulong>();
        short[] _grHeight = Array.Empty<short>();
        byte[] _grPacked = Array.Empty<byte>();
        byte[] _grFlags = Array.Empty<byte>();
        SimMowing _mPlan = SimMowing.None;
        bool _mHave;          // the design's mowing came (a reopened save's comes with its first resend)
        int _mowFaceDay = -1; // the first face day on or after Pacing.MowDay

        /// <summary>A layout trail's grass (‰), or a new trail's before its state is sized.</summary>
        internal int GrassPermilleOf(int t)
        {
            var L = _trailLayout;
            if (L != null && _grIds.Length == L.Trails && t >= 0 && t < _grHeight.Length) return _grHeight[t];
            return L != null && t >= 0 && t < L.Trails && L.TrailGraded[t] ? Pacing.GrassNewGradedPermille : Pacing.GrassNewNaturalPermille;
        }

        /// <summary>SN-36: the depth (mm) a fine cell of layout trail <paramref name="t"/> is covered at.</summary>
        int CoverMm(int t) =>
            (_trailLayout.TrailGraded[t] ? Pacing.CoverGradedMm : Pacing.CoverNaturalBaseMm) + Pacing.GrassCoverMm * GrassPermilleOf(t) / 1000;

        /// <summary>SN-36 for the screen: the cover depth (mm) of a trail with <paramref name="grassPermille"/>.</summary>
        public static int CoverNeededMm(Pacing p, bool graded, int grassPermille) =>
            (graded ? p.CoverGradedMm : p.CoverNaturalBaseMm) + p.GrassCoverMm * Math.Max(0, Math.Min(1000, grassPermille)) / 1000;

        // ---------- the layout and the setting ----------

        /// <summary>The per-trail arrays follow the layout by trail id; a trail new to it starts as a new trail (SN-35).</summary>
        void GrassLayoutChanged()
        {
            var L = _trailLayout;
            if (L == null) return;
            int n = L.Trails;
            bool same = _grIds.Length == n;
            for (int t = 0; same && t < n; t++) same = _grIds[t] == L.TrailId[t];
            if (!same)
            {
                var ids = new ulong[n];
                Array.Copy(L.TrailId, ids, n);
                var map = new int[_grIds.Length];
                for (int o = 0; o < _grIds.Length; o++) map[o] = Array.IndexOf(ids, _grIds[o]);
                _grHeight = Remap(_grHeight, map, n); _grPacked = Remap(_grPacked, map, n); _grFlags = Remap(_grFlags, map, n);
                _grIds = ids;
            }
            // A trail built after April 1 has had no winter yet: this year's has already acted for it.
            bool summer = FaceDayNow % Calendar.YearFaceDays >= Calendar.April1FaceDay;
            for (int t = 0; t < n; t++)
            {
                if ((_grFlags[t] & GrKnown) != 0) continue;
                _grHeight[t] = (short)(L.TrailGraded[t] ? Pacing.GrassNewGradedPermille : Pacing.GrassNewNaturalPermille);
                _grPacked[t] = 0;
                _grFlags[t] = (byte)(GrKnown | (summer ? GrActed : 0));
            }
            if (_mHave) MapMowing();
            _trailCardStamp++;
        }

        bool ApplyMowing(object payload)
        {
            if (!(payload is SimMowing m) || m.Problem() != null) return false;
            _mPlan = m;
            _mHave = true;
            MapMowing();
            return true;
        }

        void MapMowing()
        {
            for (int t = 0; t < _grIds.Length; t++)
            {
                int i = Array.BinarySearch(_mPlan.TrailIds, _grIds[t]);
                bool on = i >= 0 && _mPlan.EveryAutumn[i];
                _grFlags[t] = (byte)(on ? _grFlags[t] | GrMowOn : _grFlags[t] & ~GrMowOn);
            }
        }

        int MowFaceDay()
        {
            if (_mowFaceDay >= 0) return _mowFaceDay;
            int j = 0;
            while (j < Calendar.YearFaceDays && Calendar.CalendarOffset(j) < Pacing.MowDay) j++;
            return _mowFaceDay = j;
        }

        // ---------- the day and the hour ----------

        /// <summary>SN-35 and SN-37 at face day <paramref name="j"/>'s midnight (of the label year), by the last hour's cover.</summary>
        internal void GrassFaceDay(int j)
        {
            var L = _trailLayout;
            if (L == null || _grIds.Length != L.Trails || _grIds.Length == 0) return;
            bool spring = j >= Calendar.April1FaceDay;
            bool window = j >= MowFaceDay() || j < Calendar.April1FaceDay;   // September 15 until the next spring
            var p = Pacing;
            for (int t = 0; t < _grIds.Length; t++)
            {
                int f = _grFlags[t];
                if (j == 0) f &= ~GrActed;   // a new label year: its winter is still to come
                if ((f & GrPackedToday) != 0) { if (_grPacked[t] < 255) _grPacked[t]++; f &= ~GrPackedToday; }
                bool bare = TrailCoverPermilleOf(t) < p.GrassBareCoverPermille;
                int h = _grHeight[t];
                if (spring && bare)
                {
                    if ((f & GrActed) == 0)
                    {
                        // The winter just gone acts once, at the first bare day of spring.
                        h = _grPacked[t] >= p.GrassKillPackedDays ? Math.Min(h, p.GrassKilledPermille) : h * p.GrassKeptPermille / 1000;
                        _grPacked[t] = 0;
                        f = (f | GrActed) & ~(GrMown | GrSnowed);
                    }
                    h = Math.Min(1000, h + p.GrassGrowPermilleDay);
                }
                if (window && !bare) f |= GrSnowed;
                if ((f & GrMowOn) != 0 && window && bare && (f & (GrMown | GrSnowed)) == 0)
                {
                    h = Math.Min(h, p.GrassMownPermille);
                    f |= GrMown;
                }
                _grHeight[t] = (short)h;
                _grFlags[t] = (byte)f;
            }
            _trailCardStamp++;
        }

        /// <summary>SN-35: each covered trail open to skiers in the day's hours, or groomed since the last roll, is packed today.</summary>
        internal void GrassPackedHour()
        {
            var L = _trailLayout;
            if (L == null || _grIds.Length != L.Trails) return;
            long sod = FaceMs % DayMs;
            bool skiing = Open && sod >= _todayOpen * 1000L && sod <= _todayStop * 1000L;
            for (int t = 0; t < _grIds.Length; t++)
            {
                if (TrailCoverPermilleOf(t) < Pacing.GrassBareCoverPermille) continue;
                int k = t < _trailNetOf.Length ? _trailNetOf[t] : -1;
                bool open = skiing && k >= 0 && k < _trailRunOpen.Length && _trailRunOpen[k] != 0;
                bool groomed = t < _trailGroomedNow.Length && _trailGroomedNow[t];
                if (open || groomed) _grFlags[t] |= GrPackedToday;
            }
        }

        /// <summary>The snapshot's grass per network trail (with the trail card's bytes).</summary>
        void WriteTrailGrass(SimSnapshot s, int trails)
        {
            int m = Math.Min(trails, s.TrailGrass.Length);
            for (int k = 0; k < m; k++) s.TrailGrass[k] = SimSnapshot.NoGrass;
            var L = _trailLayout;
            if (L == null || _grIds.Length != L.Trails || _trailNetOf.Length != L.Trails) return;
            for (int t = 0; t < L.Trails; t++)
            {
                int k = _trailNetOf[t];
                if (k >= 0 && k < m) s.TrailGrass[k] = (byte)((_grHeight[t] + 5) / 10);
            }
        }

        /// <summary>Tests and the demo: a layout trail's grass (‰) and its packed face days this winter.</summary>
        internal void SetGrass(int t, int permille, int packedDays)
        {
            _grHeight[t] = (short)Math.Max(0, Math.Min(1000, permille));
            _grPacked[t] = (byte)Math.Max(0, Math.Min(255, packedDays));
        }

        internal int GrassPackedDaysOf(int t) => t >= 0 && t < _grPacked.Length ? _grPacked[t] : 0;
        internal bool GrassMownOf(int t) => t >= 0 && t < _grFlags.Length && (_grFlags[t] & GrMown) != 0;
        internal bool GrassMowsOf(int t) => t >= 0 && t < _grFlags.Length && (_grFlags[t] & GrMowOn) != 0;

        // ---------- hash and sidecar ----------

        void HashGrass(ref StateHasher h)
        {
            if (_grIds.Length == 0) return;   // a layout with no trails (the goldens) hashes as before
            h.Add(_grIds.Length);
            for (int t = 0; t < _grIds.Length; t++) { h.Add(_grIds[t]); h.Add(_grHeight[t]); h.Add(_grPacked[t]); h.Add(_grFlags[t]); }
        }

        /// <summary>Sidecar v15 (task 4.29): the trail count, then per trail its id, grass (‰), packed days and flags.</summary>
        void WriteGrassState(SidecarWriter o)
        {
            o.I32(_grIds.Length);
            for (int t = 0; t < _grIds.Length; t++) { o.U64(_grIds[t]); o.U16(_grHeight[t]); o.U8(_grPacked[t]); o.U8(_grFlags[t]); }
        }

        void ReadGrassState(SidecarReader r)
        {
            int n = r.Count(1 << 16);
            _grIds = new ulong[n]; _grHeight = new short[n]; _grPacked = new byte[n]; _grFlags = new byte[n];
            for (int t = 0; t < n; t++)
            {
                _grIds[t] = r.U64();
                _grHeight[t] = (short)r.U16();
                if (_grHeight[t] > 1000) throw new System.IO.InvalidDataException("A trail's grass is past brush.");
                _grPacked[t] = r.U8();
                _grFlags[t] = r.U8();
                if ((_grFlags[t] & ~GrFlags) != 0) throw new System.IO.InvalidDataException("A trail's grass flags aren't ones this build knows.");
            }
        }
    }
}
