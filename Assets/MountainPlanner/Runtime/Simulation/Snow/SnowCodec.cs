using System;
using System.IO;
using MountainPlanner.Domain.Weather;

namespace MountainPlanner.Simulation
{
    /// <summary>
    /// Sidecar v9's live snow (task 4.07 PR A2), frozen with the sidecar (0.3 §5). Tile by tile (west to east, then south to
    /// north): a byte 0 for a bare tile (every cell without water), else 1 and the tile's twelve planes in turn, each cell by
    /// cell along the tile's rows as its difference from the cell before (0 before the tile's first) in a short number
    /// (<see cref="SidecarWriter.Var"/>). The planes: water, held water, the surface's held water, the surface and deep
    /// temperatures, brightness, density, type, age, flags, the cycle's day and the water since the last surface event.
    /// Our own integer code, not deflate: Unity's and .NET's deflate write different bytes, and the frozen test compares
    /// bytes. A snowy mid-winter cell costs about 12 bytes, a bare tile one.
    /// </summary>
    internal static class SnowCodec
    {
        const int Planes = 12;

        internal static void Write(SidecarWriter o, SnowField f)
        {
            int width = f.Width, tilesX = (width + SimSnow.TileCells - 1) / SimSnow.TileCells;
            int tiles = tilesX * ((f.Height + SimSnow.TileCells - 1) / SimSnow.TileCells);
            for (int t = 0; t < tiles; t++)
            {
                int x0 = t % tilesX * SimSnow.TileCells, y0 = t / tilesX * SimSnow.TileCells;
                int x1 = Math.Min(width, x0 + SimSnow.TileCells), y1 = Math.Min(f.Height, y0 + SimSnow.TileCells);
                bool any = false;
                for (int y = y0; y < y1 && !any; y++)
                    for (int x = x0; x < x1; x++) if (f.Pack[y * width + x].Swe != 0) { any = true; break; }
                o.U8(any ? 1 : 0);
                if (!any) continue;
                for (int plane = 0; plane < Planes; plane++)
                {
                    long prev = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            long v = Get(f, y * width + x, plane);
                            o.Var(v - prev);
                            prev = v;
                        }
                }
            }
        }

        internal static SnowField Read(SidecarReader r, SimSnow shape, int width, int height)
        {
            var f = new SnowField(width, height);
            int tilesX = (width + SimSnow.TileCells - 1) / SimSnow.TileCells, tilesY = (height + SimSnow.TileCells - 1) / SimSnow.TileCells;
            for (int t = 0; t < tilesX * tilesY; t++)
            {
                byte any = r.Enum(0, 1, "A snow tile's flag");
                if (any == 0) continue;
                int x0 = t % tilesX * SimSnow.TileCells, y0 = t / tilesX * SimSnow.TileCells;
                int x1 = Math.Min(width, x0 + SimSnow.TileCells), y1 = Math.Min(height, y0 + SimSnow.TileCells);
                for (int plane = 0; plane < Planes; plane++)
                {
                    long prev = 0;
                    for (int y = y0; y < y1; y++)
                        for (int x = x0; x < x1; x++)
                        {
                            long v = prev + r.Var();
                            Set(f, y * width + x, plane, v);
                            prev = v;
                        }
                }
                for (int y = y0; y < y1; y++)
                    for (int x = x0; x < x1; x++) Check(f, y * width + x);
            }
            return f;
        }

        static long Get(SnowField f, int c, int plane)
        {
            ref var p = ref f.Pack[c];
            switch (plane)
            {
                case 0: return p.Swe;
                case 1: return p.Liquid;
                case 2: return p.SurfaceLiquid;
                case 3: return p.SurfaceTemp;
                case 4: return p.DeepTemp;
                case 5: return p.Albedo;
                case 6: return p.Density;
                case 7: return f.Type[c];
                case 8: return f.Age[c];
                case 9: return f.Flags[c];
                case 10: return f.CycleDay[c];
                default: return f.NewSince[c];
            }
        }

        static void Set(SnowField f, int c, int plane, long v)
        {
            ref var p = ref f.Pack[c];
            switch (plane)
            {
                case 0: p.Swe = Int(v, 0, int.MaxValue, "water"); break;
                case 1: p.Liquid = Int(v, 0, int.MaxValue, "held water"); break;
                case 2: p.SurfaceLiquid = Int(v, 0, int.MaxValue, "the surface's water"); break;
                case 3: p.SurfaceTemp = Int(v, -10_000, 0, "the surface's temperature"); break;
                case 4: p.DeepTemp = Int(v, -1_000_000, 0, "the deep temperature"); break;
                case 5: p.Albedo = Int(v, 0, 1_000_000, "brightness"); break;
                case 6: p.Density = Int(v, 0, 1_000_000, "density"); break;
                case 7: f.Type[c] = (byte)Int(v, 0, (int)SnowType.WP, "type"); break;
                case 8: f.Age[c] = (byte)Int(v, 0, 255, "age"); break;
                case 9: f.Flags[c] = (byte)Int(v, 0, 31, "flags"); break;
                case 10: f.CycleDay[c] = (byte)Int(v, 0, 255, "the cycle's day"); break;
                default: f.NewSince[c] = (ushort)Int(v, 0, ushort.MaxValue, "new snow"); break;
            }
        }

        static int Int(long v, int lo, int hi, string what)
        {
            if (v < lo || v > hi) throw new InvalidDataException($"A snow cell's {what} holds {v} (expected {lo}–{hi}).");
            return (int)v;
        }

        static void Check(SnowField f, int c)
        {
            ref var p = ref f.Pack[c];
            if (p.Liquid > p.Swe) throw new InvalidDataException("A snow cell holds more water than it has.");
            if (p.Swe == 0 && (f.Type[c] != 0 || p.Density != 0)) throw new InvalidDataException("A bare snow cell has a type.");
        }
    }

    public sealed partial class SimWorld
    {
        // ================= the sidecar's snow block (v9) =================

        internal void WriteSnowState(SidecarWriter o)
        {
            int mode = Snow.Live ? 2 : Snow.Set ? 1 : 0;
            o.U8(mode); o.I32(Snow.Width); o.I32(Snow.Height);
            if (mode == 1) o.U16s(Snow.Cells);
            if (mode != 2) return;
            o.I64(_snowLandAt); o.I64(_snowHour);
            WriteLedger(o, _snowToday); WriteLedger(o, _snowLastDay);
            var landed = Snow.TileHashes;
            for (int t = 0; t < landed.Length; t++) o.U64(landed[t]);
            if (_snowLandAt >= 0)
            {
                WriteLedger(o, _snowPending);
                o.I64(_snowPendingWater);
                for (int t = 0; t < landed.Length; t++) o.U64(Snow.TileHashNext[t]);
            }
            // The field's planes are the save's bulk (about 200 ms to encode at 11 km): the worker only copies them, and the
            // encoding runs when the bytes are asked for, on the save's own task (SimStateCapture).
            var copy = TakeSaveCopy();
            Snow.Field.CopyTo(copy);
            o.Defer(copy, this);
        }

        internal void ReadSnowState(SidecarReader r, int version)
        {
            if (version < SimSidecar.LiveSnowVersion)
            {
                bool set = r.Bool();
                int w0 = r.Count(1 << 16), h0 = r.Count(1 << 16);
                if ((long)w0 * h0 > 64_000_000) throw new InvalidDataException("The snow field is too big.");
                var mm0 = new ushort[w0 * h0];
                r.U16s(mm0);
                Snow.Load(w0, h0, set, mm0);
                return;
            }
            int mode = r.Enum(0, 2, "The snow block's mode");
            int width = r.Count(1 << 16), height = r.Count(1 << 16);
            if ((long)width * height > 64_000_000) throw new InvalidDataException("The snow field is too big.");
            if (mode < 2)
            {
                var mm = new ushort[width * height];
                if (mode == 1) r.U16s(mm);
                Snow.Load(width, height, mode == 1, mm);
                return;
            }
            _snowLandAt = r.I64(); _snowHour = r.I64();
            if (_snowLandAt < -1 || _snowHour < -1) throw new InvalidDataException("The snow's hour is out of range.");
            _snowToday = ReadLedger(r); _snowLastDay = ReadLedger(r);
            int tiles = ((width + SimSnow.TileCells - 1) / SimSnow.TileCells) * ((height + SimSnow.TileCells - 1) / SimSnow.TileCells);
            var landed = new ulong[tiles];
            for (int t = 0; t < tiles; t++) landed[t] = r.U64();
            ulong[] next = landed;
            if (_snowLandAt >= 0)
            {
                _snowPending = ReadLedger(r);
                _snowPendingWater = r.I64();
                next = new ulong[tiles];
                for (int t = 0; t < tiles; t++) next[t] = r.U64();
            }
            var field = SnowCodec.Read(r, Snow, width, height);
            Snow.LoadLive(width, height, new SnowRules(Pacing), field, landed, next);
            _snowRunning = false;
            RebuildFine(keep: false);   // 2a: the fine field isn't saved; it reseeds from the 8 m snow (2c saves it)
        }

        static void WriteLedger(SidecarWriter o, in SnowLedger l)
        {
            o.I32(l.Day); o.I64(l.Start); o.I64(l.Seeded); o.I64(l.Fell); o.I64(l.Canopy); o.I64(l.ToWater); o.I64(l.Runoff);
            o.I64(l.Sublimated); o.I64(l.Melt); o.I64(l.End); o.I64(l.OffMap);
        }

        static SnowLedger ReadLedger(SidecarReader r) => new SnowLedger
        {
            Day = r.I32(), Start = r.I64(), Seeded = r.I64(), Fell = r.I64(), Canopy = r.I64(), ToWater = r.I64(), Runoff = r.I64(),
            Sublimated = r.I64(), Melt = r.I64(), End = r.I64(), OffMap = r.I64(),
        };
    }
}
