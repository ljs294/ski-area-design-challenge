using System;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// GU-13 v2: why a skier's satisfaction moved. The ids are stable and saved: the tab, the day report and the economy
    /// read the sums by reason, so "what is costing me guests" is answered from one place and never computed twice.
    /// </summary>
    public enum SatisfactionReason : ushort
    {
        None = 0,
        /// <summary>Time in a line past the free minutes (GU-13).</summary>
        Wait = 1,
        /// <summary>The snow it skied this lap (GU-13).</summary>
        SnowQuality = 2,
        /// <summary>A trail posted easier than it is (GU-17).</summary>
        Misrated = 3,
        /// <summary>A trail opened Marginal (GU-17; 4.07 opens them).</summary>
        Marginal = 4,
        /// <summary>A run with more skiers on it than it comfortably holds (GU-13).</summary>
        Crowd = 5,
        /// <summary>Sent down the natural ground because a trail closed under it (GU-11).</summary>
        Stranded = 6,
    }

    /// <summary>
    /// What the day counted, per lift and per trail. LO-12's lift ledger stays as task 4.11 built it; this is what guests
    /// add, in one table the day report and (later) the economy read rows out of rather than recomputing.
    /// </summary>
    public enum SimMetric : ushort
    {
        None = 0,
        /// <summary>Skiers who boarded this lift today (LO-12's rides, counted per skier rather than per carrier).</summary>
        Boardings = 1,
        /// <summary>SN-24: skiers who finished a segment of this trail today.</summary>
        Passes = 2,
        /// <summary>GU-13: satisfaction this lift's lines cost, thousandths, summed over skiers.</summary>
        WaitPenalty = 3,
        /// <summary>GU-13: skier-passes down this trail that found it more crowded than it comfortably holds.</summary>
        CrowdPenalty = 4,
        /// <summary>GU-17: skier-passes down this trail while its sign claimed it was easier than it is.</summary>
        MisratePenalty = 5,
        Revenue = 6,
        Spend = 7,
    }

    public sealed partial class SimWorld
    {
        public const int Reasons = 8;
        public const int Metrics = 8;

        /// <summary>GU-13: satisfaction moved per archetype per reason today, thousandths (the ledger, saved and hashed).</summary>
        long[] _reasonSum = new long[Pacing.Archetypes * Reasons];
        /// <summary>What each lift and each trail did to the day, by <see cref="SimMetric"/>.</summary>
        long[] _liftMetric = Array.Empty<long>(), _trailMetric = Array.Empty<long>();
        /// <summary>4.21's Popularity cells: today's share of the busiest, 0–100, kept from yesterday until the first skier.</summary>
        byte[] _liftPop = Array.Empty<byte>(), _trailPop = Array.Empty<byte>();
        bool _popFromToday;
        bool _popDirty;

        void AllocLedger(int trails)
        {
            if (_liftMetric.Length != Metrics * Pacing.MaxLifts)
            {
                _liftMetric = new long[Metrics * Pacing.MaxLifts];
                _liftPop = new byte[Pacing.MaxLifts];
            }
            if (_trailMetric.Length != Metrics * Math.Max(1, trails))
            {
                _trailMetric = new long[Metrics * Math.Max(1, trails)];
                _trailPop = new byte[Math.Max(1, trails)];
            }
        }

        void Metric(SimMetric m, bool lift, int index, long add)
        {
            if (index < 0) return;
            if (m == SimMetric.Boardings || m == SimMetric.Passes) _popDirty = true;
            if (lift)
            {
                if (index < Pacing.MaxLifts) _liftMetric[(int)m * Pacing.MaxLifts + index] += add;
                return;
            }
            int trails = _trailPop.Length;
            if (index < trails) _trailMetric[(int)m * trails + index] += add;
        }

        /// <summary>What a lift did today (LO-12's ledger and the Guests tab).</summary>
        public long LiftMetric(int lift, SimMetric m) =>
            lift >= 0 && lift < Pacing.MaxLifts ? _liftMetric[(int)m * Pacing.MaxLifts + lift] : 0;

        /// <summary>What a trail did today.</summary>
        public long TrailMetric(int trail, SimMetric m) =>
            trail >= 0 && trail < _trailPop.Length ? _trailMetric[(int)m * _trailPop.Length + trail] : 0;

        /// <summary>GU-13: satisfaction an archetype lost or gained for a reason today, thousandths.</summary>
        public long ReasonSum(int archetype, SatisfactionReason reason) =>
            archetype >= 0 && archetype < Pacing.Archetypes && (int)reason < Reasons ? _reasonSum[archetype * Reasons + (int)reason] : 0;

        // ================= satisfaction (GU-13 v2) =================

        /// <summary>
        /// GU-13 v2: moves a skier's satisfaction and says why. The per-skier event goes in the ring only for the skier
        /// the player has selected: at tens of thousands of skiers the ring would drop almost everything, so the **sums**
        /// are the ledger and the ring carries the detail of the one skier anybody is looking at.
        /// </summary>
        void Delta(int s, SatisfactionReason reason, int thousandths)
        {
            if (thousandths == 0) return;
            int v = _guests.Sat[s] + thousandths;
            if (v < 0) v = 0; else if (v > 1000) v = 1000;
            int moved = v - _guests.Sat[s];
            _guests.Sat[s] = (short)v;
            int a = _guests.Arch[s];
            if (a < Pacing.Archetypes) _reasonSum[a * Reasons + (int)reason] += moved;
            if (_inspectSerial != 0 && _guests.Serial[s] == _inspectSerial)
                Events.Add(Tick, SimEventType.SatisfactionDelta, s, moved, (ushort)reason);
        }

        /// <summary>GU-13: the wait this skier has just finished costs it, past the free minutes. Charged once.</summary>
        void ChargeWait(int s, long t)
        {
            long waited = t - _guests.JoinTick[s];
            if (waited <= 0) return;
            _guests.WaitTicks[s] += (int)Math.Min(int.MaxValue, waited);
            long realMs = waited * Pacing.TickMs;
            long overMinutes = realMs - Pacing.WaitFreeMinutes * 60_000L;
            int lift = _guests.Lift[s];
            if (overMinutes > 0)
            {
                int cost = (int)(overMinutes * Pacing.WaitPenaltyPerMinute / 60_000);
                if (cost > 0)
                {
                    Delta(s, SatisfactionReason.Wait, -cost);
                    Metric(SimMetric.WaitPenalty, true, lift, cost);
                }
            }
            _guests.JoinTick[s] = t;
        }

        /// <summary>
        /// GU-13, GU-17: the lap this skier has just finished settles up — the snow it skied, and what the trails it chose
        /// cost it: a sign that lied, a run opened Marginal, a run too crowded to enjoy.
        /// </summary>
        void CloseLap(int s)
        {
            if (_guests.LapTicks[s] <= 0) { _guests.LapFlags[s] = 0; return; }
            int quality = (int)Math.Min(1000, (long)_guests.LapQuality[s] / _guests.LapTicks[s]);
            int sat = _guests.Sat[s];
            int blended = (sat * (1000 - Pacing.LapQualityWeight) + quality * Pacing.LapQualityWeight) / 1000;
            Delta(s, SatisfactionReason.SnowQuality, blended - sat);
            byte flags = _guests.LapFlags[s];
            if ((flags & 1) != 0) Delta(s, SatisfactionReason.Misrated, -Pacing.MisratePenalty);
            if ((flags & 2) != 0) Delta(s, SatisfactionReason.Marginal, -Pacing.MarginalPenalty * MarginalPhaseFactor());
            if ((flags & 4) != 0) Delta(s, SatisfactionReason.Crowd, -Pacing.CrowdPenalty);
            _guests.LapQuality[s] = 0; _guests.LapTicks[s] = 0; _guests.LapFlags[s] = 0;
        }

        /// <summary>
        /// GU-17: opening early or hanging on late is normal and costs little; a white ribbon in February is a choice the
        /// mountain pays double for.
        /// </summary>
        int MarginalPhaseFactor()
        {
            int day = SeasonDay;
            bool shoulder = day <= Pacing.ShoulderDays || day > Pacing.SeasonDays - Pacing.ShoulderDays;
            return Pacing.MarginalPhase[shoulder ? 0 : 1];
        }

        // ================= Popularity (4.21's cells) =================

        /// <summary>
        /// Today's passes per trail and boardings per lift, as a share of the busiest, 0–100. Yesterday's figures stand
        /// until the first skier of the day, so the column never blinks to nothing at 8:30.
        /// </summary>
        void UpdatePopularity()
        {
            if (!_popDirty) return;
            _popDirty = false;
            long bestLift = 0, bestTrail = 0;
            for (int i = 0; i < _liftCount; i++) bestLift = Math.Max(bestLift, LiftMetric(i, SimMetric.Boardings));
            int trails = _trailPop.Length;
            for (int k = 0; k < trails; k++) bestTrail = Math.Max(bestTrail, TrailMetric(k, SimMetric.Passes));
            if (bestLift <= 0 && bestTrail <= 0) return;
            _popFromToday = true;
            for (int i = 0; i < _liftCount && i < _liftPop.Length; i++)
                _liftPop[i] = bestLift <= 0 ? (byte)0 : (byte)(LiftMetric(i, SimMetric.Boardings) * 100 / bestLift);
            for (int k = 0; k < trails; k++)
                _trailPop[k] = bestTrail <= 0 ? (byte)0 : (byte)(TrailMetric(k, SimMetric.Passes) * 100 / bestTrail);
        }

        /// <summary>A lift's Popularity, 0–100, or −1 before any skier has ridden anything (the cell reads "—").</summary>
        public int LiftPopularity(int lift) =>
            !_popFromToday || lift < 0 || lift >= _liftPop.Length ? -1 : _liftPop[lift];

        /// <summary>A trail's Popularity, 0–100, or −1 before any skier has skied anything.</summary>
        public int TrailPopularity(int trail) =>
            !_popFromToday || trail < 0 || trail >= _trailPop.Length ? -1 : _trailPop[trail];

        /// <summary>SN-24: this hour's passes on a segment in a lateral band, which 4.07's wear pass reads and clears.</summary>
        public int PassesOf(int edge, int lane)
        {
            int at = edge * Pacing.WearBands + lane;
            return at >= 0 && at < _passes.Length ? _passes[at] : 0;
        }

        /// <summary>SN-24's hand-over: 4.07 takes the hour's counts and the row starts the next hour at zero.</summary>
        public void TakeHourPasses(int[] into)
        {
            if (into != null && into.Length >= _passes.Length) Array.Copy(_passes, into, _passes.Length);
            Array.Clear(_passes, 0, _passes.Length);
        }

        void ResetGuestLedger()
        {
            Array.Clear(_reasonSum, 0, _reasonSum.Length);
            if (_liftMetric.Length > 0) Array.Clear(_liftMetric, 0, _liftMetric.Length);
            if (_trailMetric.Length > 0) Array.Clear(_trailMetric, 0, _trailMetric.Length);
        }
    }
}
