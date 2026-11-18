using System;
using System.IO;
using System.Text;
using MountainPlanner.Domain.Water;
using MountainPlanner.Simulation;
using NUnit.Framework;

namespace MountainPlanner.Tests
{
    /// <summary>
    /// Task 4.14 PR B, demo 118's report data: a year of water on 4.07's synthetic 2 km mountain under Jackson Hole's frozen
    /// weather, with the snow melting into the catchments. Four sources: a natural pond (starts empty), a pumped-only pond
    /// (starts empty, filled from the lake in November at 100 gpm), a reservoir on a 2 cfs stream (a 1,000 gpm test draw from December 1)
    /// and a lake switched on (a 300 gpm test draw from December 1). Writes daily CSVs to test-results/water.
    /// </summary>
    public sealed class WaterReportTests
    {
        static readonly Pacing P = Pacing.Default;
        const int W = 256, D = 256;
        const ulong LakeId = 0x20, NaturalId = 0x40, PumpedId = 0x41, DamId = 0x50;
        static readonly string[] Names = { "Lake (source)", "Natural pond", "Pumped-only pond", "Reservoir" };
        static readonly ulong[] Ids = { LakeId, NaturalId, PumpedId, DamId };

        static int[] Block(int i0, int j0, int n)
        {
            var cells = new int[n * n];
            for (int j = 0; j < n; j++) for (int i = 0; i < n; i++) cells[j * n + i] = (j0 + j) * W + i0 + i;
            return cells;
        }

        /// <summary>A bowl under <paramref name="cells"/> 8 m cells: 64 points a cell, deepest in the middle.</summary>
        static StageCurve Bowl(int cells, double deepest)
        {
            var b = new StageCurve.Builder();
            int n = (int)Math.Round(Math.Sqrt(cells * 64.0));
            double r = n / 2.0;
            for (int y = 0; y < n; y++)
                for (int x = 0; x < n; x++)
                {
                    double d = Math.Sqrt((x + 0.5 - r) * (x + 0.5 - r) + (y + 0.5 - r) * (y + 0.5 - r)) / r;
                    b.Add(Math.Max(0.3, deepest * (1 - 0.8 * Math.Min(1, d))));
                }
            return b.Build();
        }

        static int Feet(SnowFieldTests.Mountain m, int[] cells)
        {
            long sum = 0;
            foreach (int c in cells) sum += m.Feet[c];
            return (int)(sum / cells.Length);
        }

        [Test, Explicit("Writes demo 118's report data")]
        public void WS_WriteTheReportData()
        {
            string root = Path.GetFullPath(Path.Combine(TestData.Folder("formats"), "..", "..", "test-results", "water"));
            Directory.CreateDirectory(root);
            var m = SnowFieldTests.Shared;

            // The table: the streams along the south edge, a lake low on the west side, three ponds on the lower slopes.
            var mask = new byte[W * D];
            for (int i = 0; i < W; i++) mask[i] = 1;
            int[] lake = Block(8, 200, 12);
            foreach (int c in lake) mask[c] = 2;
            var bodies = new[]
            {
                new WaterBody(WaterBodyKind.Streams, 0, W, -1, 0, 0, m.Feet[0], 0, P.StreamsDepth, false),
                new WaterBody(WaterBodyKind.Lake, LakeId, lake.Length, -1, 0, 0, Feet(m, lake), 0, 120, false),
            };
            int[] natural = Block(40, 150, 5), pumped = Block(30, 60, 4), dam = Block(70, 30, 12);
            var t = WaterBodyTable.Of(W, D, -W / 2 * 8000, -D / 2 * 8000, mask, bodies, P.MaxPonds)
                .WithPond(NaturalId, natural, 60, Feet(m, natural), WaterBodyKind.Pond)
                .WithPond(PumpedId, pumped, 60, Feet(m, pumped), WaterBodyKind.Pond)
                .WithPond(DamId, dam, 150, Feet(m, dam), WaterBodyKind.Reservoir);
            int lb = t.IndexOf(LakeId);
            var list = new[]
            {
                new WaterSource(LakeId, WaterBodyKind.Lake, lb, (int)Math.Round(t[lb].SurfaceFeet * 304.8),
                                StageCurve.Prism(t[lb].AreaSquareMetres, 120L * StageCurve.StepMicrons), 0, 0, true, false),
                new WaterSource(NaturalId, WaterBodyKind.Pond, t.IndexOf(NaturalId), Feet(m, natural) * 305, Bowl(natural.Length, 3.0), 0, 0, true, true),
                new WaterSource(PumpedId, WaterBodyKind.Pond, t.IndexOf(PumpedId), Feet(m, pumped) * 305, Bowl(pumped.Length, 3.0), 0, 0, false, true),
                new WaterSource(DamId, WaterBodyKind.Reservoir, t.IndexOf(DamId), Feet(m, dam) * 305, Bowl(dam.Length, 6.0), 2000, 0, true, false),
            };
            var ofBody = new byte[t.Count];
            Array.Sort(list, (a, b) => a.Id.CompareTo(b.Id));
            for (int i = 0; i < list.Length; i++) if (list[i].Natural && list[i].Kind != WaterBodyKind.Reservoir) ofBody[list[i].Body] = (byte)(i + 1);
            var drains = Catchments.Of(W, D, m.Mm).DrainsTo(t.Mask, ofBody);
            var sources = new WaterSources(list, W, D, drains);

            var w = SnowFieldTests.World(P);
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterBodies, 0, t));
            w.ApplyNow(SimCommand.Of(SimCommandKind.SetWaterSources, 0, sources));
            var cal = w.Calendar;
            int days = cal.YearFaceDays, n = Ids.Length;
            var idx = new int[n];
            for (int k = 0; k < n; k++) idx[k] = w.Sources.IndexOf(Ids[k]);

            // The catchments: cells per source.
            var share = new StringBuilder("source,catchment_cells,catchment_acres,capacity_gal,full_pool_ft,start_gal\n");
            for (int k = 0; k < n; k++)
            {
                int cells = 0;
                foreach (byte b in drains) if (b == idx[k] + 1) cells++;
                var s = sources[idx[k]];
                share.Append($"{Names[k]},{cells},{cells * 64 / 4046.86:0.0},{s.Curve.CapacityGallons},{s.FullPoolMm / 304.8:0},{w.HeldGallonsOf(idx[k])}\n");
            }
            File.WriteAllText(Path.Combine(root, "sources.csv"), share.ToString());

            var csv = new StringBuilder("day,month,dom");
            foreach (var name in Names)
                foreach (var col in new[] { "held gal", "capacity gal", "floor gal", "below full ft", "inflow", "runoff", "spring", "pumped in", "draw", "pumped out", "spill", "evaporation", "frozen" })
                    csv.Append(',').Append(name).Append(' ').Append(col);
            csv.Append(",resort held gal,resort ledger gal,worst hourly imbalance gal\n");
            var last = new SimWorld.WaterLedger[n];
            for (int k = 0; k < n; k++) last[k] = w.SourceLedgerOf(idx[k]);
            long worst = 0;
            bool drawing = false;
            const long TransferPerHour = 100L * 10371 / 60;   // 100 gpm over a face hour's calendar seconds
            SeasonRunner.Run(w, null, days * SimWorld.DayMs - SimWorld.HourMs, (i, x) =>
            {
                int d = (int)(i / 24);
                int month = cal.Month(d % days);
                if (!drawing && month == 12 && w.TestDrawOf(idx[3]) == 0)
                {
                    drawing = true;
                    w.ApplyNow(SimCommand.Of(SimCommandKind.SetTestDraw, 0, new TestDraw(DamId, 1000)));
                    w.ApplyNow(SimCommand.Of(SimCommandKind.SetTestDraw, 0, new TestDraw(LakeId, 300)));
                }
                if (drawing && month == 4)
                {
                    drawing = false;   // snowmaking ends with the season: the draws stop and spring refills
                    w.ApplyNow(SimCommand.Of(SimCommandKind.SetTestDraw, 0, new TestDraw(DamId, 0)));
                    w.ApplyNow(SimCommand.Of(SimCommandKind.SetTestDraw, 0, new TestDraw(LakeId, 0)));
                }
                if (month == 11) w.TransferWater(idx[0], idx[2], TransferPerHour);   // the lake fills the pumped-only pond
                long all = 0;
                for (int k = 0; k < w.SourceCount; k++) all += w.HeldMilliOf(k);
                worst = Math.Max(worst, Math.Abs(all - w.ResortWaterLedger.Held));
                if ((i + 1) % 24 != 0) return;
                csv.Append(d).Append(',').Append(month).Append(',').Append(cal.Day(d % days));
                for (int k = 0; k < n; k++)
                {
                    int s = idx[k];
                    var src = w.Sources[s];
                    var l = w.SourceLedgerOf(s);
                    var p = last[k];
                    if (l.Start != p.Start || l.Inflow < p.Inflow) p = default;   // the year rolled: a fresh ledger
                    long held = w.HeldGallonsOf(s);
                    csv.Append(',').Append(held).Append(',').Append(src.Curve.CapacityGallons)
                       .Append(',').Append(src.Kind == WaterBodyKind.Lake ? src.Curve.GallonsBelowFull(P.LakeDrawdownMm * 1000L) : src.Curve.CapacityGallons * P.MinPoolPermille / 1000)
                       .Append(',').Append((src.Curve.BelowFullMicrons(held) / 304_800.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture));
                    foreach (long v in new[] { l.Inflow - p.Inflow, l.Runoff - p.Runoff, l.Spring - p.Spring, l.PumpedIn - p.PumpedIn, l.Draw - p.Draw,
                                               l.PumpedOut - p.PumpedOut, l.Spill - p.Spill, l.Evaporation - p.Evaporation })
                        csv.Append(',').Append(v / 1000);
                    csv.Append(',').Append(w.IceOf(src.Body) > 0 ? 1 : 0);
                    last[k] = l;
                }
                csv.Append(',').Append(all / 1000).Append(',').Append(w.ResortWaterLedger.Held / 1000).Append(',').Append(worst / 1000).Append('\n');
            });
            File.WriteAllText(Path.Combine(root, "daily.csv"), csv.ToString());
            Assert.That(worst, Is.Zero, "the resort's ledger balances every hour");
        }
    }
}
