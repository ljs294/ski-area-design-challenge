using System;
using System.IO;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// SI-12 v2: the simulation's sidecar, a frozen binary format (0.3 §5) beside a save: <c>sim/&lt;sha256&gt;.sim</c>.
    /// Little-endian throughout:
    /// <code>
    /// "MPSM"  u16 SidecarVersion  u16 0
    /// replay header (SI-22): u64 Pacing contents hash, i32 Pacing version, u64 resort seed, u64 design revision hash,
    ///                        i32 start label year, i64 tick
    /// the world's state (SimWorld.WriteState, in its own order)
    /// u64 the state hash (SI-08) the reader must reach
    /// </code>
    /// A bump adds a step to <see cref="Upgrade"/> and a <c>TestData/formats/v&lt;N&gt;-sim</c> fixture; v1's stays unedited.
    /// v2 (task 4.11) appends the lift-operations block (lines, riders, carriers, holds, the day's lift ledger, the way down).
    /// v4 (task 4.12) appends the guests: every skier on the hill, the day's crowd and its ledgers, the reputations per
    /// archetype (GU-18), SN-24's wear counters and the Popularity cells. v3 was held for task 4.05's weather and never
    /// written (the guests merged first). v5 (task 4.05) appends the weather: the rows playing and any waiting for October 1,
    /// each with its reference points, and what the generator was reset with and the hour it reached (it is rebuilt by
    /// re-running from October 1, WX-56); the top-station gusts in the lift block turn from whole mph to tenths. v6 (task
    /// 4.05 PR C) appends the forecast's issue (the hour and whether it was the evening's; the sheet itself is made again from
    /// it, WX-32) and the named events' running counts and the alerts' dedupe days (WX-41, WX-42). v7 (task 4.09) appends the lakes: the body table's hash the records were made against, then each body's record (kind,
    /// id, centroid and radius for matching, the lag, the ice as h², the snow's water, the remainders, the ledger counters and
    /// the dates). The table itself is the area's, rebuilt at open and resent; a v6 save's bodies start as open water.
    /// v8 (task 4.07 PR A1) writes the weather rows as rows format 2, with WX-26 v2's dew point by hour (`DewDiurnal`); an
    /// older sidecar's rows read as format 1, the dew point following the air (κ = 0), and hash as before.
    /// v9 (task 4.07 PR A2) writes the snow block in place as a mode byte (0 never set, 1 static depths, 2 live) and the
    /// field's size; static depths as before; live, the hour in flight (its landing and hour, the ledgers, both tile-hash
    /// sets: a save mid-hour holds the hour's result and lands it as it would have) and the snow itself, tile by tile (a bare
    /// tile is one byte), each plane as differences along the tile's rows in short numbers (<see cref="SnowCodec"/>). An
    /// older sidecar's depths read as static depths; the snow's inputs then make them the seed.
    /// v10 (task 4.14 PR B) appends the water block: per source (id order) its id, kind, the water it holds (milligallons),
    /// its test draw, its alert days and its season ledger, then the resort's ledger (SI-10). The sources themselves are the
    /// design's, rebuilt and resent; an older sidecar's sources start full when they arrive (they stood full before).
    /// v11 (task 4.24 PR B) appends the ride-down
    /// block: per lift, the top line's and the down riders' chain ends and counts and today's rides down (RD-2); the guest
    /// block's skier state may then be 8 (in line at the top) or 9 (riding down). An older sidecar opens with no top lines
    /// and nobody riding down, and hashes as before (the new state folds only when it holds something).
    /// v12 (task 4.07 B1 part 2c) appends the trail snow block: the day's trail counters and the fold remainder, then
    /// (when the trails' board is live) the late season, the readouts, the board per trail of the design's network (by
    /// id: open, label, walk, cover, rating), the guests' snow table with the network's shape hash, the layout's hash, fine
    /// size and grid with each layout trail's label, walk, cover, rating and main surface, and the fine cells
    /// (<see cref="FineSnowCodec"/>), then the runoff of the snow's hour in flight (the cells that ran, as index steps and
    /// µm). An older sidecar's trail snow seeds from the 8 m snow when the layout arrives, and its hour in flight lands with
    /// no runoff.
    /// v13 (task 4.25) appends the grooming block: whether a fleet ever came, then the fleet (number, model, drive, tiller,
    /// season minutes), the crew pins and each trail's resolved setting and priority by id, each layout trail's state by id
    /// (order, powder nights, warnings and row, last night, the share done, groomed tonight), tonight (the shifts, crews,
    /// tasks and their pieces), last night's figures and the warnings ring. An older sidecar has no fleet: the design's
    /// arrives with the first SetGrooming (save v17 gives an older save its free fleet).
    /// </summary>
    public static class SimSidecar
    {
        public const int Version = 13;
        /// <summary>The first version with the grooming block (task 4.25; the number cleared by 0.0 2026-11-20).</summary>
        public const int GroomingVersion = 13;
        /// <summary>The first version with the trail snow block (task 4.07 B1 part 2c; the number confirmed with 0.0).</summary>
        public const int TrailSnowVersion = 12;
        /// <summary>The first version with the water block (task 4.14 PR B; the number confirmed with 0.0 at merge).</summary>
        public const int WaterVersion = 10;
        /// <summary>The first version with the ride-down block and guest states 8 and 9 (task 4.24 PR B).</summary>
        public const int RideDownVersion = 11;
        /// <summary>The first version with the snow block's mode byte and the live snow (task 4.07 PR A2).</summary>
        public const int LiveSnowVersion = 9;
        /// <summary>The first version with the lake block (task 4.09).</summary>
        public const int LakesVersion = 7;
        /// <summary>The first sidecar whose weather rows are rows format 2 (task 4.07 PR A1).</summary>
        internal const int WeatherRowsTwo = 8;
        public const string Extension = ".sim";
        static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'S', (byte)'M' };

        /// <summary>The header a reader checks before it trusts the state (SI-22: a mismatch is said, never silently replayed).</summary>
        public readonly struct Header
        {
            public readonly int SidecarVersion;
            public readonly ulong PacingHash;
            public readonly int PacingVersion;
            public readonly ulong ResortSeed;
            public readonly ulong DesignRevisionHash;
            public readonly int StartYear;
            public readonly long Tick;

            public Header(int sidecarVersion, ulong pacingHash, int pacingVersion, ulong resortSeed, ulong designRevisionHash, int startYear, long tick)
            {
                SidecarVersion = sidecarVersion; PacingHash = pacingHash; PacingVersion = pacingVersion; ResortSeed = resortSeed;
                DesignRevisionHash = designRevisionHash; StartYear = startYear; Tick = tick;
            }
        }

        /// <summary>The format version a sidecar's bytes say they are, or −1 when they aren't a sidecar.</summary>
        public static int VersionOf(byte[] bytes)
        {
            if (bytes == null || bytes.Length < 8) return -1;
            for (int i = 0; i < 4; i++) if (bytes[i] != Magic[i]) return -1;
            return bytes[4] | bytes[5] << 8;
        }

        /// <summary>Reads only the header.</summary>
        public static Header ReadHeader(byte[] bytes)
        {
            var r = Open(bytes, out int version);
            return ReadHeader(r, version);
        }

        static Header ReadHeader(SidecarReader r, int version) =>
            new Header(version, r.U64(), r.I32(), r.U64(), r.U64(), r.I32(), r.I64());

        internal static byte[] Write(SimWorld w) => Capture(w).Encode();

        /// <summary>The state at this tick, copied but not yet encoded (the worker's part of a save).</summary>
        internal static SimStateCapture Capture(SimWorld w)
        {
            var o = new SidecarWriter();
            o.Bytes(Magic);
            o.U16(Version);
            o.U16(0);
            o.U64(w.Pacing.ContentsHash);
            o.I32(Pacing.Version);
            o.U64(w.Design.ResortSeed);
            o.U64(w.Design.RevisionHash);
            o.I32(w.StartYear);
            o.I64(w.Tick);
            w.WriteState(o);
            o.U64(w.Hash());
            return new SimStateCapture(o);
        }

        /// <summary>
        /// Opens a sidecar onto a new world over <paramref name="design"/> (the save's own design). Throws
        /// <see cref="InvalidDataException"/> when the bytes are damaged or don't add up to the hash they carry, and
        /// <see cref="NotSupportedException"/> for a newer sidecar. <paramref name="header"/> says what it was made with.
        /// </summary>
        /// <param name="weather">The area's weather now (task 4.05): a sidecar from before v5 starts it from October 1 of its
        /// year; a v5 sidecar keeps the rows it ran with, and different rows here wait for the next October 1 (WX-32 v2).</param>
        public static SimWorld Read(byte[] bytes, Pacing pacing, SimDesign design, SimSite site, out Header header, SimWeather weather = null)
        {
            if (pacing == null) throw new ArgumentNullException(nameof(pacing));
            if (design == null) throw new ArgumentNullException(nameof(design));
            bytes = Upgrade(bytes);
            var r = Open(bytes, out int version);
            try
            {
                header = ReadHeader(r, version);
                if (header.StartYear < 2 || header.StartYear > 9000) throw new InvalidDataException("The simulation's start year is out of range.");
                var w = new SimWorld(pacing, design, site, header.StartYear, restoring: true);
                w.ReadState(r, header);
                ulong expected = r.U64();
                if (!r.AtEnd) throw new InvalidDataException("The simulation's state has bytes after its end.");
                // The pacing and the design are in the hash (SI-22): with either different the hash can't match, so it's
                // checked only when both are the ones the state was saved with.
                bool sameDesign = header.DesignRevisionHash == design.RevisionHash && header.ResortSeed == design.ResortSeed;
                if (header.PacingHash == pacing.ContentsHash && sameDesign && w.Hash() != expected)
                    throw new InvalidDataException("The simulation's state doesn't add up to its hash.");
                if (!sameDesign) w.RefoldDesign();
                // After the hash: the weather the area has now is an input like a new design revision, never the saved state.
                if (header.SidecarVersion < 5) w.StartWeatherForOlderSave(weather);
                else w.UseWeatherAtLoad(weather);
                return w;
            }
            catch (EndOfStreamException) { throw new InvalidDataException("The simulation's state is cut short."); }
            catch (ArgumentOutOfRangeException e) { throw new InvalidDataException("The simulation's state is damaged: " + e.Message); }
        }

        /// <summary>
        /// One step per version bump, oldest first. v1 → v2: v1 has no lift-operations block, and the reader leaves the lines
        /// and chairs empty, the lift ledger at zero and the holds as saved (<see cref="SimWorld"/>'s ReadState reads the block
        /// only from v2), so the bytes need no rewrite. v4 → v5 likewise: the reader multiplies the saved gusts by ten and
        /// starts the weather from October 1, and v5 → v6: the reader starts the events empty and the forecast unissued. v6 → v7:
        /// no lake block, every body open water with no lag. v7 → v8: the reader reads each weather block's rows as format 1
        /// (κ = 0), and they hash as they did. v8 → v9: the reader reads the old snow block (set, size, depths) as static
        /// depths. v9 → v10: no water block; the sources start full when they arrive.
        /// </summary>
        static byte[] Upgrade(byte[] bytes)
        {
            int v = VersionOf(bytes);
            if (v < 0) throw new InvalidDataException("This isn't a simulation sidecar.");
            if (v > Version) throw new NotSupportedException($"The simulation was saved by a newer game (sidecar {v}; this version reads up to {Version}).");
            if (v < 1) throw new InvalidDataException($"Sidecar version {v} doesn't exist.");
            return bytes;
        }

        static SidecarReader Open(byte[] bytes, out int version)
        {
            version = VersionOf(bytes);
            if (version < 0) throw new InvalidDataException("This isn't a simulation sidecar.");
            var r = new SidecarReader(bytes);
            r.Skip(8);
            return r;
        }
    }

    /// <summary>The sidecar's little-endian writer (no allocation per value; one buffer that grows).</summary>
    internal sealed class SidecarWriter
    {
        byte[] _b = new byte[4096];
        int _n;

        void Room(int k)
        {
            if (_n + k <= _b.Length) return;
            Array.Resize(ref _b, Math.Max(_b.Length * 2, _n + k));
        }

        public void Bytes(byte[] v) { Room(v.Length); Buffer.BlockCopy(v, 0, _b, _n, v.Length); _n += v.Length; }
        public void U8(int v) { Room(1); _b[_n++] = (byte)v; }
        public void Bool(bool v) => U8(v ? 1 : 0);
        public void U16(int v) { Room(2); _b[_n++] = (byte)v; _b[_n++] = (byte)(v >> 8); }
        public void I32(int v) { Room(4); for (int i = 0; i < 4; i++) _b[_n++] = (byte)(v >> (8 * i)); }
        public void U64(ulong v) { Room(8); for (int i = 0; i < 8; i++) _b[_n++] = (byte)(v >> (8 * i)); }
        public void I64(long v) => U64(unchecked((ulong)v));

        public void U16s(ushort[] v)
        {
            Room(v.Length * 2);
            for (int i = 0; i < v.Length; i++) { _b[_n++] = (byte)v[i]; _b[_n++] = (byte)(v[i] >> 8); }
        }

        /// <summary>A signed number in as few bytes as it needs (zigzag, then 7 bits a byte, low first: v9's snow planes).</summary>
        public void Var(long v)
        {
            ulong z = unchecked((ulong)((v << 1) ^ (v >> 63)));
            Room(10);
            while (z >= 0x80) { _b[_n++] = (byte)(z | 0x80); z >>= 7; }
            _b[_n++] = (byte)z;
        }

        // Encodings to write at their place in the bytes, when the bytes are asked for (task 4.07 PR A2's save split; 2c
        // adds the trail snow).
        System.Collections.Generic.List<(int At, Action<SidecarWriter> Write)> _deferred;

        /// <summary>Marks <paramref name="field"/>'s encoding here, done in <see cref="ToArray"/>; the field goes back to
        /// <paramref name="owner"/> for its next save once it's encoded.</summary>
        internal void Defer(SnowField field, SimWorld owner) =>
            Defer(o => { SnowCodec.Write(o, field); owner?.ReturnSaveCopy(field); }, "the 8 m snow");

        /// <summary>Marks an encoding here, run in <see cref="ToArray"/> on whichever thread asks for the bytes; its size is
        /// noted under <paramref name="name"/> (<see cref="DeferredSizes"/>, for the save's log).</summary>
        internal void Defer(Action<SidecarWriter> write, string name = null)
        {
            (_deferred ??= new System.Collections.Generic.List<(int, Action<SidecarWriter>)>()).Add((_n, write));
            (_deferredNames ??= new System.Collections.Generic.List<string>()).Add(name ?? "a block");
        }

        System.Collections.Generic.List<string> _deferredNames;

        /// <summary>After <see cref="ToArray"/>: each deferred block's name and encoded bytes, in order (diagnostics).</summary>
        internal System.Collections.Generic.List<(string Name, int Bytes)> DeferredSizes { get; private set; }

        public byte[] ToArray()
        {
            if (_deferred == null)
            {
                var a = new byte[_n];
                Buffer.BlockCopy(_b, 0, a, 0, _n);
                return a;
            }
            var o = new SidecarWriter();
            int from = 0, k = 0;
            DeferredSizes = new System.Collections.Generic.List<(string, int)>();
            foreach (var d in _deferred)
            {
                o.Room(d.At - from); Buffer.BlockCopy(_b, from, o._b, o._n, d.At - from); o._n += d.At - from;
                int at = o._n;
                d.Write(o);
                DeferredSizes.Add((_deferredNames[k++], o._n - at));
                from = d.At;
            }
            o.Room(_n - from); Buffer.BlockCopy(_b, from, o._b, o._n, _n - from); o._n += _n - from;
            _deferred = null;
            _b = o._b; _n = o._n;
            return ToArray();
        }
    }

    /// <summary>
    /// A world's state copied at a tick boundary, not yet encoded (task 4.07 PR A2's save split): <see cref="Encode"/> gives
    /// the sidecar's bytes, the same as <see cref="SimSidecar.Write"/> would have at that tick, on whatever thread calls it.
    /// </summary>
    public sealed class SimStateCapture
    {
        readonly object _gate = new object();
        SidecarWriter _o;
        byte[] _bytes;

        internal SimStateCapture(SidecarWriter o) { _o = o; }

        /// <summary>After <see cref="Encode"/>: the deferred blocks' sizes, e.g. "the 8 m snow 3.80 MB, the trail snow 1.20 MB".</summary>
        public string BlockSizes { get; private set; } = "";

        public byte[] Encode()
        {
            lock (_gate)
            {
                if (_bytes == null)
                {
                    _bytes = _o.ToArray();
                    var sizes = _o.DeferredSizes;
                    if (sizes != null)
                    {
                        var text = new System.Text.StringBuilder();
                        foreach (var (name, bytes) in sizes) text.Append(text.Length > 0 ? ", " : "").Append(name).Append(' ').Append((bytes / 1e6).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)).Append(" MB");
                        BlockSizes = text.ToString();
                    }
                    _o = null;
                }
                return _bytes;
            }
        }
    }

    /// <summary>The sidecar's reader: throws <see cref="EndOfStreamException"/> past the end.</summary>
    internal sealed class SidecarReader
    {
        readonly byte[] _b;
        int _at;

        public SidecarReader(byte[] b) { _b = b; }
        public bool AtEnd => _at == _b.Length;

        void Need(int k) { if (_at + k > _b.Length) throw new EndOfStreamException(); }
        public void Skip(int k) { Need(k); _at += k; }
        public byte U8() { Need(1); return _b[_at++]; }

        public bool Bool()
        {
            byte v = U8();
            if (v > 1) throw new InvalidDataException("A yes/no byte holds " + v + ".");
            return v == 1;
        }

        public int U16() { Need(2); int v = _b[_at] | _b[_at + 1] << 8; _at += 2; return v; }

        public int I32()
        {
            Need(4);
            int v = 0;
            for (int i = 0; i < 4; i++) v |= _b[_at++] << (8 * i);
            return v;
        }

        public ulong U64()
        {
            Need(8);
            ulong v = 0;
            for (int i = 0; i < 8; i++) v |= (ulong)_b[_at++] << (8 * i);
            return v;
        }

        public long I64() => unchecked((long)U64());

        /// <summary>A count, refused beyond <paramref name="max"/>.</summary>
        public int Count(int max)
        {
            int n = I32();
            if (n < 0 || n > max) throw new InvalidDataException($"A count of {n} is out of range (0–{max}).");
            return n;
        }

        public void U16s(ushort[] into)
        {
            Need(into.Length * 2);
            for (int i = 0; i < into.Length; i++) { into[i] = (ushort)(_b[_at] | _b[_at + 1] << 8); _at += 2; }
        }

        /// <summary>The writer's <c>Var</c>: refused past ten bytes.</summary>
        public long Var()
        {
            ulong z = 0;
            for (int shift = 0; ; shift += 7)
            {
                if (shift > 63) throw new InvalidDataException("A short number runs past ten bytes.");
                byte b = U8();
                z |= (ulong)(b & 0x7F) << shift;
                if (b < 0x80) break;
            }
            return unchecked((long)(z >> 1) ^ -(long)(z & 1));
        }

        /// <summary>A skier slot, refused outside 0 to <paramref name="cap"/>−1 (GU-01: the lines are chains over slots).</summary>
        public int Slot(int cap)
        {
            int s = I32();
            if (s < 0 || s >= cap) throw new InvalidDataException($"A skier slot of {s} is out of range (0–{cap - 1}).");
            return s;
        }

        /// <summary>A slot, or −1 for "none".</summary>
        public int SlotOrNone(int cap)
        {
            int s = I32();
            if (s < -1 || s >= cap) throw new InvalidDataException($"A skier slot of {s} is out of range (−1–{cap - 1}).");
            return s;
        }

        /// <summary>A whole number, refused outside <paramref name="lo"/> to <paramref name="hi"/>.</summary>
        public int Ranged(int lo, int hi, string what)
        {
            int v = I32();
            if (v < lo || v > hi) throw new InvalidDataException($"{what} holds {v} (expected {lo}–{hi}).");
            return v;
        }

        /// <summary>An enum byte, refused when it isn't one of <paramref name="lo"/>–<paramref name="hi"/>.</summary>
        public byte Enum(int lo, int hi, string what)
        {
            byte v = U8();
            if (v < lo || v > hi) throw new InvalidDataException($"{what} holds {v}.");
            return v;
        }
    }
}
