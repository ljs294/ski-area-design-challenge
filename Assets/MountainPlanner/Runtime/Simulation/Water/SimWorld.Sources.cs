using System;
using MountainPlanner.Domain.Water;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// WS-02…WS-11 (task 4.14 PR B): the water each snowmaking source holds, moved once a face hour after the lakes, in whole
    /// milligallons. The sources (<see cref="WaterSources"/>) are the design's, built off the main thread and sent with
    /// <see cref="SimCommandKind.SetWaterSources"/>; the per-source records (what it holds, its test draw, its ledger) are
    /// the state, saved in the sidecar and hashed.
    /// - Water time is calendar time (WX-22): a face hour carries SeasonCalendarDays ÷ SeasonDays hours of a stream's flow,
    ///   a spring's, a draw's and the sun's evaporation, as the snow's runoff and the lakes' ice already do.
    /// - In: a dam's stream (WS-03 × the month's factor), a natural pond's or a lake's catchment runoff at each snow landing
    ///   (WS-04), a natural pond's spring, transfers in (WS-11). Out: evaporation over the water's area (WS-07, not while
    ///   frozen), the draw (WS-05, down to MinPool or a lake's drawdown limit), transfers out, and anything over full pool
    ///   spills (WS-06).
    /// - Every term goes in the source's ledger and the resort's (SI-10): start + created + in = held + out + removed.
    /// - Records match a new source table by id: a new source starts full or, chosen, empty (WS-10); an edited one keeps its
    ///   water (what no longer fits spills); a removed one's water is a stated loss (SI-13). A world with no sources hashes
    ///   as before.
    /// </summary>
    public sealed partial class SimWorld
    {
        /// <summary>SI-10: one water ledger, milligallons.</summary>
        public struct WaterLedger
        {
            public long Start, Created, Removed, Inflow, Runoff, Spring, PumpedIn, Draw, PumpedOut, Spill, Evaporation;

            /// <summary>What the ledger says is held now.</summary>
            public long Held => Start + Created + Inflow + Runoff + Spring + PumpedIn - Draw - PumpedOut - Spill - Evaporation - Removed;

            internal void Write(SidecarWriter o)
            {
                o.I64(Start); o.I64(Created); o.I64(Removed); o.I64(Inflow); o.I64(Runoff); o.I64(Spring);
                o.I64(PumpedIn); o.I64(Draw); o.I64(PumpedOut); o.I64(Spill); o.I64(Evaporation);
            }

            internal static WaterLedger Read(SidecarReader r) => new WaterLedger
            {
                Start = r.I64(), Created = r.I64(), Removed = r.I64(), Inflow = r.I64(), Runoff = r.I64(), Spring = r.I64(),
                PumpedIn = r.I64(), Draw = r.I64(), PumpedOut = r.I64(), Spill = r.I64(), Evaporation = r.I64(),
            };

            internal void AddTo(ref StateHasher h)
            {
                h.Add(Start); h.Add(Created); h.Add(Removed); h.Add(Inflow); h.Add(Runoff); h.Add(Spring);
                h.Add(PumpedIn); h.Add(Draw); h.Add(PumpedOut); h.Add(Spill); h.Add(Evaporation);
            }
        }

        /// <summary>Snapshot flags per source.</summary>
        public const byte SourceAtFloor = 1, SourceSpilling = 2, SourceFrozen = 4, SourceIsLake = 8;

        WaterSources _sources;
        int _srcCount;
        ulong[] _srcId = Array.Empty<ulong>();
        WaterBodyKind[] _srcKind = Array.Empty<WaterBodyKind>();
        long[] _srcHeld = Array.Empty<long>();      // milligallons
        int[] _srcTestGpm = Array.Empty<int>();
        short[] _srcLowDay = Array.Empty<short>(), _srcSpillDay = Array.Empty<short>();
        long[] _srcHourIn = Array.Empty<long>(), _srcHourOut = Array.Empty<long>();   // the last hour's, milligallons (not saved: rates for the card)
        byte[] _srcFlags = Array.Empty<byte>();
        WaterLedger[] _srcLedger = Array.Empty<WaterLedger>();
        WaterLedger _waterLedger;
        /// <summary>v1–v9 saves: the first source table fills every source (they were full before the water clock).</summary>
        bool _srcFillAll;
        /// <summary>The landing's runoff per source (µm summed over its cells), preallocated so the landing allocates nothing.</summary>
        long[] _srcRunoffSum = Array.Empty<long>();

        public const long MilliPerGallon = 1000;

        /// <summary>The sources playing now, or null.</summary>
        public WaterSources Sources => _sources;
        public int SourceCount => _srcCount;
        public ulong SourceIdOf(int i) => _srcId[i];
        /// <summary>What a source holds, gallons.</summary>
        public long HeldGallonsOf(int i) => _srcHeld[i] / MilliPerGallon;
        public long HeldMilliOf(int i) => _srcHeld[i];
        public int TestDrawOf(int i) => _srcTestGpm[i];
        public WaterLedger SourceLedgerOf(int i) => _srcLedger[i];
        /// <summary>SI-10: the resort's water ledger, every source together.</summary>
        public WaterLedger ResortWaterLedger => _waterLedger;

        // ---------------- the sources ----------------

        /// <summary>The <see cref="SimCommandKind.SetWaterSources"/> command: matches the records to the sources (see the class).</summary>
        bool ApplyWaterSources(WaterSources t)
        {
            if (t == null || t.Count > Pacing.MaxSources) return false;
            if (_sources != null && _sources.Hash == t.Hash) return true;
            int n = t.Count;
            var id = new ulong[n]; var kind = new WaterBodyKind[n]; var held = new long[n]; var gpm = new int[n];
            var low = new short[n]; var spill = new short[n]; var hin = new long[n]; var hout = new long[n]; var flags = new byte[n];
            var ledger = new WaterLedger[n];
            var taken = new bool[_srcCount];
            for (int i = 0; i < n; i++)
            {
                var s = t[i];
                id[i] = s.Id; kind[i] = s.Kind; low[i] = -1; spill[i] = -1;
                long cap = s.Curve.CapacityGallons * MilliPerGallon;
                int from = -1;
                for (int k = 0; k < _srcCount; k++) if (!taken[k] && _srcId[k] == s.Id) { from = k; break; }
                if (from >= 0)
                {
                    taken[from] = true;
                    held[i] = _srcHeld[from]; gpm[i] = _srcTestGpm[from]; low[i] = _srcLowDay[from]; spill[i] = _srcSpillDay[from];
                    ledger[i] = _srcLedger[from];
                    if (held[i] > cap)
                    {
                        // An edit made it smaller: what no longer fits spills (WS-06).
                        long over = held[i] - cap;
                        held[i] = cap; ledger[i].Spill += over; _waterLedger.Spill += over;
                    }
                }
                else
                {
                    held[i] = s.StartsEmpty && !_srcFillAll ? 0 : cap;
                    ledger[i] = new WaterLedger { Start = held[i] };
                    _waterLedger.Created += held[i];
                }
            }
            for (int k = 0; k < _srcCount; k++)
                if (!taken[k]) _waterLedger.Removed += _srcHeld[k];   // SI-13: a removed source's water is a stated loss
            _srcId = id; _srcKind = kind; _srcHeld = held; _srcTestGpm = gpm; _srcLowDay = low; _srcSpillDay = spill;
            _srcHourIn = hin; _srcHourOut = hout; _srcFlags = flags; _srcLedger = ledger;
            if (_srcRunoffSum.Length < n) _srcRunoffSum = new long[Math.Max(n, Pacing.MaxSources)];
            _srcCount = n;
            _sources = t;
            _srcFillAll = false;
            return true;
        }

        /// <summary>The <see cref="SimCommandKind.SetTestDraw"/> command (WS-05): a source's test draw, gallons a minute.</summary>
        bool ApplyTestDraw(in SimCommand c)
        {
            if (!(c.Payload is TestDraw d)) { Reject(c, SimRefusal.Unknown); return false; }
            int i = _sources != null ? _sources.IndexOf(d.Source) : -1;
            if (i < 0) { Reject(c, SimRefusal.Unknown); return false; }
            if (_srcTestGpm[i] == d.Gpm) { Reject(c, SimRefusal.NoChange); return false; }
            _srcTestGpm[i] = d.Gpm;
            return true;
        }

        // ---------------- the hour ----------------

        /// <summary>Calendar seconds a face hour carries (WX-22).</summary>
        int WaterSeconds => (int)Domain.Weather.WeatherMath.DivRound(Pacing.SeasonCalendarDays * 3600L, Pacing.SeasonDays);

        /// <summary>The hour step's water (after the lakes, so a frozen source knows it).</summary>
        internal void HourStepSources(long hour)
        {
            if (_sources == null || _srcCount == 0) return;
            var p = Pacing;
            long yearHours = (long)Calendar.YearFaceDays * 24;
            int hourOfYear = (int)(hour % yearHours);
            short faceDay = (short)(hourOfYear / 24);
            if (hourOfYear == 0) RollWaterLedgers();
            int seconds = WaterSeconds;
            int month = Calendar.Month(faceDay);
            int factor = p.InflowSeason[Math.Max(1, Math.Min(12, month)) - 1];
            for (int i = 0; i < _srcCount; i++)
            {
                var s = _sources[i];
                ref var L = ref _srcLedger[i];
                long cap = s.Curve.CapacityGallons * MilliPerGallon;
                long hin = 0, hout = 0;
                byte flags = s.Kind == WaterBodyKind.Lake ? SourceIsLake : (byte)0;

                // In: the stream (WS-03) and the spring (WS-04).
                if (s.StreamMilliCfs > 0)
                {
                    long cfs = (long)s.StreamMilliCfs * factor / 1000;                 // milli-cfs this month
                    long q = cfs * 7_480_519L * seconds / 1_000_000;                   // 7.480519 gal a cubic foot → milligallons
                    _srcHeld[i] += q; L.Inflow += q; _waterLedger.Inflow += q; hin += q;
                }
                if (s.SpringGpm > 0)
                {
                    long q = (long)s.SpringGpm * seconds * MilliPerGallon / 60;
                    _srcHeld[i] += q; L.Spring += q; _waterLedger.Spring += q; hin += q;
                }

                // Out: evaporation over the water's area now, not through ice (WS-07, LK-09).
                bool frozen = s.Body >= 0 && s.Body < _lakeCount && _lakeId[s.Body] == s.Id && _lakeFrozen[s.Body] && IceOf(s.Body) > p.IceOpenAt;
                if (frozen) flags |= SourceFrozen;
                else if (_srcHeld[i] > 0)
                {
                    long area = s.Curve.AreaAt(_srcHeld[i] / MilliPerGallon);
                    // µm × m² = ml; 0.264172 gal a litre → milligallons = ml × 0.264172.
                    long q = (long)p.EvapMicronsPerDay * area * seconds / 86_400 * 264_172 / 1_000_000;
                    q = Math.Min(q, _srcHeld[i]);
                    _srcHeld[i] -= q; L.Evaporation += q; _waterLedger.Evaporation += q; hout += q;
                }

                // The draw (WS-05): the test draw now, down to the floor.
                if (_srcTestGpm[i] > 0)
                {
                    long want = (long)_srcTestGpm[i] * seconds * MilliPerGallon / 60;
                    long got = Take(i, want, faceDay);
                    L.Draw += got; _waterLedger.Draw += got; hout += got;
                }

                // Over full pool spills (WS-06).
                if (_srcHeld[i] > cap) hout += Spill(i, cap, faceDay);
                if (_srcHeld[i] <= FloorOf(i) && FloorOf(i) > 0) flags |= SourceAtFloor;
                if (_srcSpillDay[i] == faceDay) flags |= SourceSpilling;
                _srcHourIn[i] = hin; _srcHourOut[i] = hout; _srcFlags[i] = flags;
            }
        }

        /// <summary>WS-05: the water a source keeps back: MinPool of a pond's or reservoir's capacity, a lake's drawdown limit.</summary>
        long FloorOf(int i)
        {
            var s = _sources[i];
            if (s.Kind == WaterBodyKind.Lake) return s.Curve.GallonsBelowFull((long)Pacing.LakeDrawdownMm * 1000) * MilliPerGallon;
            return s.Curve.CapacityGallons * Pacing.MinPoolPermille / 1000 * MilliPerGallon;
        }

        /// <summary>Takes up to <paramref name="want"/> milligallons above the floor; at the floor, the day's alert.</summary>
        long Take(int i, long want, short faceDay)
        {
            long floor = FloorOf(i);
            long got = Math.Max(0, Math.Min(want, _srcHeld[i] - floor));
            _srcHeld[i] -= got;
            if (got < want) LowEvent(i, faceDay);
            return got;
        }

        long Spill(int i, long cap, short faceDay)
        {
            long over = _srcHeld[i] - cap;
            _srcHeld[i] = cap;
            _srcLedger[i].Spill += over; _waterLedger.Spill += over;
            if (_srcSpillDay[i] != faceDay)
            {
                _srcSpillDay[i] = faceDay;
                Events.Add(Tick, SimEventType.SourceSpilling, i, faceDay);
            }
            return over;
        }

        void LowEvent(int i, short faceDay)
        {
            if (_srcLowDay[i] == faceDay) return;
            _srcLowDay[i] = faceDay;
            Events.Add(Tick, _srcKind[i] == WaterBodyKind.Lake ? SimEventType.SourceAtDrawdownLimit : SimEventType.SourceAtMinPool, i, faceDay);
        }

        /// <summary>October 1: each ledger starts over from what it holds (the season's, SI-10).</summary>
        void RollWaterLedgers()
        {
            long all = 0;
            for (int i = 0; i < _srcCount; i++) { _srcLedger[i] = new WaterLedger { Start = _srcHeld[i] }; all += _srcHeld[i]; _srcLowDay[i] = -1; _srcSpillDay[i] = -1; }
            _waterLedger = new WaterLedger { Start = all };
        }

        /// <summary>
        /// WS-04 at the snow's landing: the hour's runoff over each natural pond's and lake's catchment. µm over an 8 m cell is
        /// 64 ml a µm; milligallons = ml × 0.264172.
        /// </summary>
        void CollectRunoff()
        {
            var t = _sources;
            if (t == null || _srcCount == 0 || t.DrainsTo == null || t.Width != Snow.Width || t.Depth != Snow.Height) return;
            var drains = t.DrainsTo;
            var runoff = Snow.Field.RunoffHour;
            for (int i = 0; i < _srcCount; i++) _srcRunoffSum[i] = 0;
            for (int c = 0; c < drains.Length; c++)
            {
                int k = drains[c];
                if (k != 0 && runoff[c] > 0) _srcRunoffSum[k - 1] += runoff[c];
            }
            short faceDay = (short)(FaceMs / DayMs % Calendar.YearFaceDays);
            for (int i = 0; i < _srcCount; i++)
            {
                if (_srcRunoffSum[i] == 0 || !t[i].Natural) continue;
                long q = _srcRunoffSum[i] * WaterBodyTable.CellMetres * WaterBodyTable.CellMetres * 264_172 / 1_000_000;
                _srcHeld[i] += q; _srcLedger[i].Runoff += q; _waterLedger.Runoff += q; _srcHourIn[i] += q;
                long cap = t[i].Curve.CapacityGallons * MilliPerGallon;
                if (_srcHeld[i] > cap) _srcHourOut[i] += Spill(i, cap, faceDay);
            }
        }

        // ---------------- the hooks (4.15, 4.16) ----------------

        /// <summary>
        /// WS-05 (4.16's guns): draws up to <paramref name="gallons"/> from a source, down to its floor; returns the gallons
        /// drawn. Ledgered as the draw.
        /// </summary>
        internal long DrawWater(int source, long gallons)
        {
            if (source < 0 || source >= _srcCount || gallons <= 0) return 0;
            short faceDay = (short)(FaceMs / DayMs % Calendar.YearFaceDays);
            long got = Take(source, gallons * MilliPerGallon, faceDay);
            _srcLedger[source].Draw += got; _waterLedger.Draw += got; _srcHourOut[source] += got;
            return got / MilliPerGallon;
        }

        /// <summary>
        /// WS-11 (4.15's pump house): moves up to <paramref name="gallons"/> from one source to another: no further than the
        /// source's floor, no more than the target holds to full pool. Returns the gallons moved; both sides are ledgered.
        /// </summary>
        internal long TransferWater(int from, int to, long gallons)
        {
            if (from < 0 || to < 0 || from >= _srcCount || to >= _srcCount || from == to || gallons <= 0) return 0;
            long room = Math.Max(0, _sources[to].Curve.CapacityGallons * MilliPerGallon - _srcHeld[to]);
            short faceDay = (short)(FaceMs / DayMs % Calendar.YearFaceDays);
            long got = Take(from, Math.Min(gallons * MilliPerGallon, room), faceDay);
            _srcHeld[to] += got;
            _srcLedger[from].PumpedOut += got; _srcLedger[to].PumpedIn += got;
            _waterLedger.PumpedOut += got; _waterLedger.PumpedIn += got;
            _srcHourOut[from] += got; _srcHourIn[to] += got;
            return got / MilliPerGallon;
        }

        // ---------------- reads ----------------

        void WriteSources(SimSnapshot s)
        {
            int n = _sources == null ? 0 : Math.Min(_srcCount, s.SourceIds.Length);
            s.SourceCount = n;
            int seconds = WaterSeconds;
            for (int i = 0; i < n; i++)
            {
                var src = _sources[i];
                s.SourceIds[i] = _srcId[i];
                s.SourceHeldGallons[i] = _srcHeld[i] / MilliPerGallon;
                s.SourceCapacityGallons[i] = src.Curve.CapacityGallons;
                s.SourceFloorGallons[i] = FloorOf(i) / MilliPerGallon;
                s.SourceBelowFullMicrons[i] = (int)Math.Min(int.MaxValue, src.Curve.BelowFullMicrons(_srcHeld[i] / MilliPerGallon));
                s.SourceInGpm[i] = (int)(_srcHourIn[i] * 60 / MilliPerGallon / seconds);
                s.SourceOutGpm[i] = (int)(_srcHourOut[i] * 60 / MilliPerGallon / seconds);
                s.SourceTestGpm[i] = _srcTestGpm[i];
                s.SourceFlags[i] = _srcFlags[i];
            }
        }

        void HashSources(ref StateHasher h)
        {
            // A world without sources hashes as before, so the goldens' designs (no water) stand.
            if (_srcCount == 0 && _waterLedger.Created == 0 && _waterLedger.Start == 0) return;
            h.Add(_srcCount);
            for (int i = 0; i < _srcCount; i++)
            {
                h.Add(_srcId[i]); h.Add((long)_srcKind[i]); h.Add(_srcHeld[i]); h.Add(_srcTestGpm[i]);
                h.Add(_srcLowDay[i]); h.Add(_srcSpillDay[i]);
                _srcLedger[i].AddTo(ref h);
            }
            _waterLedger.AddTo(ref h);
        }

        // ---------------- the sidecar (v10) ----------------

        void WriteSourceState(SidecarWriter o)
        {
            o.I32(_srcCount);
            for (int i = 0; i < _srcCount; i++)
            {
                o.U64(_srcId[i]); o.U8((byte)_srcKind[i]); o.I64(_srcHeld[i]); o.I32(_srcTestGpm[i]);
                o.I32(_srcLowDay[i]); o.I32(_srcSpillDay[i]);
                _srcLedger[i].Write(o);
            }
            _waterLedger.Write(o);
        }

        void ReadSourceState(SidecarReader r)
        {
            _sources = null;
            _srcFillAll = false;
            int n = r.Count(Pacing.MaxSources);
            SizeSources(n);
            for (int i = 0; i < n; i++)
            {
                _srcId[i] = r.U64();
                _srcKind[i] = (WaterBodyKind)r.Enum((int)WaterBodyKind.Lake, (int)WaterBodyKind.Reservoir, "A source's kind");
                _srcHeld[i] = r.I64();
                _srcTestGpm[i] = r.I32();
                if (_srcHeld[i] < 0 || _srcTestGpm[i] < 0 || _srcTestGpm[i] > TestDraw.MaxGpm) throw new System.IO.InvalidDataException("A source's water is out of range.");
                _srcLowDay[i] = (short)r.I32(); _srcSpillDay[i] = (short)r.I32();
                _srcLedger[i] = WaterLedger.Read(r);
            }
            _waterLedger = WaterLedger.Read(r);
        }

        /// <summary>
        /// v12 (task 4.07 B1 part 2c, finding B2c-4): the hour in flight's runoff, so a save made between an hour's start and
        /// its landing still pours that hour into the ponds when it lands. Only the cells that ran, as index steps and µm; none
        /// when no hour is in flight.
        /// </summary>
        void WriteRunoffState(SidecarWriter o)
        {
            var f = Snow.Live && SnowInFlight ? Snow.Field : null;
            int n = 0;
            if (f != null) for (int c = 0; c < f.Cells; c++) if (f.RunoffHour[c] != 0) n++;
            o.Var(n);
            if (n == 0) return;
            int last = -1;
            for (int c = 0; c < f.Cells; c++)
            {
                int v = f.RunoffHour[c];
                if (v == 0) continue;
                o.Var(c - last); o.Var(v);
                last = c;
            }
        }

        void ReadRunoffState(SidecarReader r)
        {
            long n = r.Var();
            var f = Snow.Live && SnowInFlight ? Snow.Field : null;
            if (f != null) System.Array.Clear(f.RunoffHour, 0, f.Cells);
            if (n == 0) return;
            if (f == null || n < 0 || n > f.Cells) throw new System.IO.InvalidDataException("The hour's runoff doesn't fit the snow.");
            long at = -1;
            for (long i = 0; i < n; i++)
            {
                long step = r.Var(), v = r.Var();
                at += step;
                if (step <= 0 || at >= f.Cells || v == 0 || v < int.MinValue || v > int.MaxValue)
                    throw new System.IO.InvalidDataException("The hour's runoff is out of range.");
                f.RunoffHour[at] = (int)v;
            }
        }

        /// <summary>v1–v9 → v10 (task 4.14 PR B): no water block; every source the design has starts full (they stood full before).</summary>
        void StartSourcesForOlderSave()
        {
            _sources = null;
            SizeSources(0);
            _waterLedger = default;
            _srcFillAll = true;
        }

        void SizeSources(int n)
        {
            _srcCount = n;
            _srcId = new ulong[n]; _srcKind = new WaterBodyKind[n]; _srcHeld = new long[n]; _srcTestGpm = new int[n];
            _srcLowDay = new short[n]; _srcSpillDay = new short[n]; _srcHourIn = new long[n]; _srcHourOut = new long[n];
            _srcFlags = new byte[n]; _srcLedger = new WaterLedger[n];
            if (_srcRunoffSum.Length < n) _srcRunoffSum = new long[Math.Max(n, Pacing.MaxSources)];
        }
    }

    /// <summary>WS-05 (task 4.14 PR B): <see cref="SimCommandKind.SetTestDraw"/>'s payload: a source's test draw in gallons a minute.</summary>
    public sealed class TestDraw
    {
        public const int MaxGpm = 5_000;
        public readonly ulong Source;
        public readonly int Gpm;

        public TestDraw(ulong source, int gpm)
        {
            if (gpm < 0 || gpm > MaxGpm) throw new ArgumentOutOfRangeException(nameof(gpm));
            Source = source; Gpm = gpm;
        }
    }
}
