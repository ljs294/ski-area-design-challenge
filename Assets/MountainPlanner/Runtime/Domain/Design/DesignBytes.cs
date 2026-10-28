using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The document's canonical bytes (owner ruling P3-03 Q4): a little-endian binary form of shape version
    /// <see cref="DesignDocument.ShapeVersion"/>, written in ID order. Equal documents give equal bytes, so goldens and
    /// undo checks compare these (SH-04). It is not the save file: task 04 picks that encoding and freezes it.
    /// </summary>
    public static class DesignBytes
    {
        static readonly byte[] Magic = { (byte)'M', (byte)'P', (byte)'D', (byte)'S' };

        public static byte[] Encode(DesignDocument doc)
        {
            var w = new Writer();
            w.Bytes(Magic);
            w.U16(DesignDocument.ShapeVersion);
            w.U64(doc.Seed);
            w.I64(doc.NextOrdinal);
            w.I32(doc.Trails.Count);
            foreach (var t in doc.Trails) WriteTrail(w, t);
            w.I32(doc.Lifts.Count);
            foreach (var l in doc.Lifts) WriteLift(w, l);
            // Shape 4 (task 15): the terrain strokes.
            w.I32(doc.Strokes.Count);
            foreach (var k in doc.Strokes) WriteStroke(w, k);
            w.I32(doc.Areas.Count);   // shape 5 (NW-15)
            foreach (var a in doc.Areas)
            {
                w.U64(a.Id.Value);
                w.Str(a.Name);
                w.I32(a.Ends.Count);
                foreach (var e in a.Ends)
                {
                    w.U64(e.Lift.Value);
                    w.U8(e.Bottom ? (byte)1 : (byte)0);
                }
            }
            w.I32(doc.Lines.Count);   // shape 7 (NT43): the trails the player shaped, as spans of runs
            foreach (var line in doc.Lines)
            {
                w.U64(line.Id.Value);
                w.Str(line.Name);
                w.I32(line.Spans.Count);
                foreach (var sp in line.Spans)
                {
                    w.U64(sp.Piece.Value);
                    WriteAnchor(w, sp.From);
                    WriteAnchor(w, sp.To);
                    w.I32(sp.FromMm);
                    w.I32(sp.ToMm);
                }
            }
            w.I32(doc.Cuts.Count);    // shape 7 (NT45): the points cut on runs
            foreach (var c in doc.Cuts)
            {
                w.U64(c.Piece.Value);
                w.Point(c.At);
            }
            return w.ToArray();
        }

        static void WriteAnchor(Writer w, Anchor a)
        {
            w.U8((byte)a.Kind);
            w.U64(a.Other);
            w.U8(a.End ? (byte)1 : (byte)0);
            w.I32(a.K);
            w.Point(a.At);
        }

        static Anchor ReadAnchor(Reader r) => Anchor.Of((AnchorKind)r.U8(), r.U64(), r.Bool(), r.I32(), r.Point());   // left to right

        /// <summary>One trail's bytes, for comparing trails (NW-10's split-then-join test).</summary>
        public static byte[] Encode(Trail trail)
        {
            var w = new Writer();
            WriteTrail(w, trail);
            return w.ToArray();
        }

        /// <summary>One lift's bytes, for comparing lifts (an edit that changes nothing).</summary>
        public static byte[] Encode(Lift lift)
        {
            var w = new Writer();
            WriteLift(w, lift);
            return w.ToArray();
        }

        public static bool Same(Lift a, Lift b) => Same(Encode(a), Encode(b));

        /// <summary>Reads <see cref="Encode(DesignDocument)"/>'s bytes back; anything malformed throws <see cref="FormatException"/>.</summary>
        public static DesignDocument Decode(byte[] bytes)
        {
            var r = new Reader(bytes);
            for (int i = 0; i < Magic.Length; i++) if (r.U8() != Magic[i]) throw new FormatException("Not a design document.");
            int version = r.U16();
            if (version != DesignDocument.ShapeVersion) throw new FormatException($"Design shape {version} isn't readable here (this build reads {DesignDocument.ShapeVersion}).");
            ulong seed = r.U64();
            long next = r.I64();
            var trails = new Trail[r.Count(1 << 20)];
            for (int i = 0; i < trails.Length; i++) trails[i] = ReadTrail(r);
            var lifts = new Lift[r.Count(1 << 20)];
            for (int i = 0; i < lifts.Length; i++) lifts[i] = ReadLift(r);
            var strokes = new TerrainStroke[r.Count(DesignRules.MaxStrokes)];
            for (int i = 0; i < strokes.Length; i++) strokes[i] = ReadStroke(r);
            var areas = new AreaRecord[r.Count(1 << 16)];
            for (int i = 0; i < areas.Length; i++)
            {
                var id = new AreaId(r.U64());
                string name = r.Str();
                var ends = new AreaEnd[r.Count(1 << 16)];
                for (int k = 0; k < ends.Length; k++) ends[k] = new AreaEnd(new LiftId(r.U64()), r.U8() != 0);
                areas[i] = new AreaRecord(id, name, ends);
            }
            var lines = new TrailLine[r.Count(1 << 20)];
            for (int i = 0; i < lines.Length; i++)
            {
                var id = new TrailLineId(r.U64());
                string name = r.Str();
                var spans = new Span[r.Count(4096)];
                for (int k = 0; k < spans.Length; k++)
                {
                    var piece = new TrailId(r.U64());
                    var from = ReadAnchor(r);
                    var to = ReadAnchor(r);
                    spans[k] = new Span(piece, from, to, r.I32(), r.I32());
                }
                lines[i] = new TrailLine(id, name, spans);
            }
            var cuts = new TrailCut[r.Count(1 << 20)];
            for (int i = 0; i < cuts.Length; i++) cuts[i] = new TrailCut(new TrailId(r.U64()), r.Point());
            if (!r.AtEnd) throw new FormatException("Trailing bytes after the design document.");
            var doc = DesignDocument.From(seed, next, trails, lifts, strokes, areas, lines, cuts);
            // Canonical means written in order: a reordered file is not this format.
            if (!Same(Encode(doc), bytes)) throw new FormatException("The design document isn't in canonical order.");
            return doc;
        }

        public static bool Same(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            for (int i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
            return true;
        }

        static void WriteTrail(Writer w, Trail t)
        {
            w.U64(t.Id.Value);
            w.Str(t.Name);
            w.U8((byte)t.Stage);
            w.U8((byte)t.Settings.Grading);
            w.U8((byte)t.Settings.Kind);
            w.U8((byte)t.Settings.Clearing);
            w.OptPoint(t.Head);
            w.OptPoint(t.Tail);
            // Shape 8 (sections finding 7): the clearing's rings, each its points in order.
            w.I32(t.Shape.Rings.Count);
            foreach (var ring in t.Shape.Rings)
            {
                w.I32(ring.Length);
                foreach (var p in ring) w.Point(p);
            }
        }

        static Trail ReadTrail(Reader r)
        {
            var id = new TrailId(r.U64());
            string name = r.Str();
            var stage = (WorkStage)r.U8();
            var settings = new TrailSettings((TrailGrading)r.U8(), (TrailKind)r.U8(), (TrailClearing)r.U8());
            PointMm? head = r.OptPoint();
            PointMm? tail = r.OptPoint();
            var rings = new PointMm[r.Count(DesignRules.MaxShapeRings)][];
            for (int i = 0; i < rings.Length; i++)
            {
                var ring = new PointMm[r.Count(DesignRules.MaxShapePoints)];
                for (int k = 0; k < ring.Length; k++) ring[k] = r.Point();
                rings[i] = ring;
            }
            return new Trail(id, name, stage, settings, TrailShape.FromRings(rings), head, tail);
        }

        static void WriteLift(Writer w, Lift l)
        {
            w.U64(l.Id.Value);
            w.Str(l.Name);
            w.U8((byte)l.Stage);
            w.Str(l.CatalogId);
            w.Point(l.Bottom);
            w.Point(l.Top);
            w.U8((byte)l.TowerMode);
            w.U8((byte)l.Stance);
            w.I32(l.Towers.Count);
            foreach (var t in l.Towers)
            {
                w.U64(t.Id.Value);
                w.I32(t.AlongMm);
                w.I32(t.HeightMm);
                w.U8((byte)t.Kind);   // shape 3 (LT44)
            }
            // Shape 2 (task 09): the terminal levels, absolute whole mm (SK14), or none.
            w.U8((byte)(l.Levels.HasValue ? 1 : 0));
            if (l.Levels is LiftLevels v)
            {
                w.I32(v.BottomMm);
                w.I32(v.TopMm);
            }
        }

        static Lift ReadLift(Reader r)
        {
            var id = new LiftId(r.U64());
            string name = r.Str();
            var stage = (WorkStage)r.U8();
            string catalog = r.Str();
            var bottom = r.Point();
            var top = r.Point();
            var mode = (TowerMode)r.U8();
            var stance = (TowerStance)r.U8();
            var towers = new Tower[r.Count(DesignRules.MaxTowersPerLift)];
            for (int i = 0; i < towers.Length; i++)
            {
                var towerId = new TowerId(r.U64());
                int along = r.I32(), height = r.I32();
                byte kind = r.U8();
                if (kind > (byte)TowerKind.Combination) throw new FormatException("A tower's kind isn't one this build knows.");
                towers[i] = new Tower(towerId, along, height, (TowerKind)kind);
            }
            LiftLevels? levels = null;
            byte has = r.U8();
            if (has > 1) throw new FormatException("A lift's levels flag isn't 0 or 1.");
            if (has == 1) levels = new LiftLevels(r.I32(), r.I32());
            return new Lift(id, name, stage, catalog, bottom, top, mode, stance, towers, levels);
        }

        static void WriteStroke(Writer w, TerrainStroke k)
        {
            var s = k.Settings;
            w.U64(k.Id.Value);
            w.U8((byte)s.Kind);
            w.I32(s.RadiusMm);
            w.I32(s.FalloffPermille);
            w.I32(s.StepMm);
            w.I32(s.StrengthPermille);
            w.I32(s.TargetMm);
            w.I32(s.StartMm);
            w.Point(s.StartAt);
            w.Point(s.TargetAt);
            w.I32(s.LimitMm);
            w.I32(k.Dabs.Count);
            foreach (var d in k.Dabs)
            {
                w.Point(d.At);
                w.U16(d.WeightPermille);
            }
        }

        static TerrainStroke ReadStroke(Reader r)
        {
            var id = new TerrainStrokeId(r.U64());
            var kind = (BrushKind)r.U8();
            var s = new BrushSettings(kind, r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.I32(), r.Point(), r.Point(), r.I32());   // left to right
            var dabs = new BrushDab[r.Count(DesignRules.MaxDabsPerStroke)];
            for (int i = 0; i < dabs.Length; i++) dabs[i] = new BrushDab(r.Point(), r.U16());
            return new TerrainStroke(id, s, dabs);
        }

        sealed class Writer
        {
            readonly MemoryStream _s = new MemoryStream();
            public void Bytes(byte[] b) => _s.Write(b, 0, b.Length);
            public void U8(byte v) => _s.WriteByte(v);
            public void U16(int v) { U8((byte)v); U8((byte)(v >> 8)); }
            public void I32(int v) { for (int i = 0; i < 4; i++) U8((byte)(v >> (8 * i))); }
            public void U64(ulong v) { for (int i = 0; i < 8; i++) U8((byte)(v >> (8 * i))); }
            public void I64(long v) => U64((ulong)v);
            public void Point(PointMm p) { I32(p.X); I32(p.Y); }

            public void OptPoint(PointMm? p)
            {
                U8(p.HasValue ? (byte)1 : (byte)0);
                if (p.HasValue) Point(p.Value);
            }

            public void Str(string s)
            {
                var b = Encoding.UTF8.GetBytes(s ?? "");
                I32(b.Length);
                Bytes(b);
            }

            public byte[] ToArray() => _s.ToArray();
        }

        sealed class Reader
        {
            readonly byte[] _b;
            int _i;
            public Reader(byte[] b) { _b = b ?? throw new FormatException("No bytes."); }
            public bool AtEnd => _i == _b.Length;

            public byte U8()
            {
                if (_i >= _b.Length) throw new FormatException("The design document ends early.");
                return _b[_i++];
            }

            public int U16() => U8() | (U8() << 8);

            public int I32()
            {
                int v = 0;
                for (int i = 0; i < 4; i++) v |= U8() << (8 * i);
                return v;
            }

            public ulong U64()
            {
                ulong v = 0;
                for (int i = 0; i < 8; i++) v |= (ulong)U8() << (8 * i);
                return v;
            }

            public long I64() => (long)U64();
            public PointMm Point() => new PointMm(I32(), I32());

            public bool Bool()
            {
                byte v = U8();
                if (v > 1) throw new FormatException("A flag isn't 0 or 1.");
                return v == 1;
            }

            public PointMm? OptPoint() => Bool() ? Point() : (PointMm?)null;

            public int Count(int max)
            {
                int n = I32();
                if (n < 0 || n > max) throw new FormatException("A count is out of range.");
                return n;
            }

            public string Str()
            {
                int n = Count(4096);
                if (_i + n > _b.Length) throw new FormatException("The design document ends early.");
                try
                {
                    var s = new UTF8Encoding(false, true).GetString(_b, _i, n);
                    _i += n;
                    return s;
                }
                catch (ArgumentException e)
                {
                    throw new FormatException("A name isn't valid UTF-8.", e);
                }
            }
        }
    }
}
