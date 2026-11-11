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
    /// re-running from October 1, WX-56); the top-station gusts in the lift block turn from whole mph to tenths.
    /// </summary>
    public static class SimSidecar
    {
        public const int Version = 5;
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

        internal static byte[] Write(SimWorld w)
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
            return o.ToArray();
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
        /// starts the weather from October 1.
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

        public byte[] ToArray()
        {
            var a = new byte[_n];
            Buffer.BlockCopy(_b, 0, a, 0, _n);
            return a;
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
