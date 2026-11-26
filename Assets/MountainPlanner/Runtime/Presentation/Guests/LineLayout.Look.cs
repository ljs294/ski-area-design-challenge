using System;
using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    public sealed partial class LineLayout
    {
        /// <summary>Task 4.26 BB26: how a line looks, in numbers (tests, the -linelayout probe).</summary>
        public struct LineLook
        {
            public int Places, Rows, Sections, FileSections;
            /// <summary>Steps in walking order (a lane's next place, or the next skier in a file) longer than 1.5 rows or nearer than 0.75 m.</summary>
            public int LaneBreaks;
            /// <summary>Steps of the centre longer than a sample and a half (a jump; never expected).</summary>
            public int CentreBreaks;
            /// <summary>Places a row behind the nearest ahead rather than their lane's (lanes merging into or out of a file).</summary>
            public int Merges;
            public int PassedOver, SoftPlaces, WalkPlaces, Walks, Reframes, ThroatRows;
            /// <summary>The tightest bend of the centre where the line is full width, and of its inner lane, metres.</summary>
            public float TightestCentre, TightestInner;
            /// <summary>The fastest the centre's curvature changes where the line is full width, 1/m per metre.</summary>
            public float MaxCurvatureRate;
            /// <summary>The most a row's places are spread along the line (by the centre's length), anywhere and on straights.</summary>
            public float MaxSkew, MaxSkewStraight;
            /// <summary>Places within 30 m of the load point.</summary>
            public int Within30;
            public float CentreMetres;
            public string FirstBreak;

            public override string ToString() =>
                $"places {Places} rows {Rows} sections {Sections} (file {FileSections}) laneBreaks {LaneBreaks} centreBreaks {CentreBreaks} " +
                $"merges {Merges} passedOver {PassedOver} soft {SoftPlaces} walk {WalkPlaces} walks {Walks} reframes {Reframes} throatRows {ThroatRows} " +
                $"tightest {TightestCentre:F2} inner {TightestInner:F2} rate {MaxCurvatureRate:F2} skew {MaxSkew:F1} straightSkew {MaxSkewStraight:F1} " +
                $"within30 {Within30} centre {CentreMetres:F0} m" + (FirstBreak != null ? " first break: " + FirstBreak : "");
        }

        /// <summary>Task 4.26 BB26: how lift <paramref name="lift"/>'s line looks (empty when it has none).</summary>
        public LineLook Measure(int lift)
        {
            var look = new LineLook { TightestCentre = float.PositiveInfinity, TightestInner = float.PositiveInfinity };
            var r = _ribbons != null && lift >= 0 && lift < _ribbons.Length ? _ribbons[lift] : null;
            if (r == null) return look;
            int seats = r.Seats;
            look.Places = r.Out.Count;
            look.Rows = r.Out.Count / seats;
            look.Sections = r.SectionRows.Count;
            foreach (bool f in r.SectionFile) if (f) look.FileSections++;
            look.PassedOver = r.PassedOver; look.SoftPlaces = r.SoftPlaces; look.WalkPlaces = r.WalkPlaces;
            look.Walks = r.Walks; look.Reframes = r.Reframes; look.ThroatRows = r.ThroatRows;
            look.CentreMetres = r.Length;
            float limit = 1.5f * RowMetres, inner = r.Width * 0.5f - ColumnMetres * 0.5f;
            for (int i = 1; i < r.Samples.Count; i++)
            {
                var s = r.Samples[i];
                if ((s.P - r.Samples[i - 1].P).magnitude > SampleMetres * 1.5f)
                {
                    look.CentreBreaks++;
                    look.FirstBreak ??= $"centre jump at sample {i} ({s.P.x:F1},{s.P.y:F1})";
                }
                if (!s.File && !r.Samples[i - 1].File)
                    look.MaxCurvatureRate = Mathf.Max(look.MaxCurvatureRate, Mathf.Abs(s.K - r.Samples[i - 1].K) / SampleMetres);
                if (!s.File && Mathf.Abs(s.K) > 1e-4f)
                {
                    float rad = 1f / Mathf.Abs(s.K);
                    look.TightestCentre = Mathf.Min(look.TightestCentre, rad);
                    look.TightestInner = Mathf.Min(look.TightestInner, rad - inner);
                }
            }
            // Walking order: each skier about a row behind the one ahead in its lane, or (where the lanes merge into
            // single file, take turns in it, or widen again) a row or so behind the nearest skier ahead of it along the
            // line, half the line's width off at most. Anything further is a break.
            float merge = Mathf.Sqrt(limit * limit + r.Width * r.Width * 0.25f) + 0.05f;
            for (int k = 0; k < r.Out.Count; k++)
            {
                if (k >= seats && !r.OutFile[k] && !r.OutFile[k - seats])
                {
                    float lane = Flat(r.Out[k], r.Out[k - seats]);
                    if (lane <= limit && lane >= MinPlaceMetres - 1e-3f) continue;
                }
                float best = float.MaxValue;
                for (int q = 0; q < r.Out.Count; q++)
                    if (q != k && (r.OutN[q] < r.OutN[k] || (r.OutN[q] == r.OutN[k] && q < k)))
                        best = Mathf.Min(best, Flat(r.Out[q], r.Out[k]));
                if (best == float.MaxValue) continue;   // the front of the line
                if (best <= merge && best >= MinPlaceMetres - 1e-3f) { look.Merges++; continue; }
                look.LaneBreaks++;
                look.FirstBreak ??= $"place {k}: {best:F2} m from the nearest ahead at ({r.Out[k].x:F1},{r.Out[k].z:F1})";
            }
            for (int d = 0; d < look.Rows; d++)
            {
                int lo = int.MaxValue, hi = int.MinValue;
                bool straight = true;
                for (int c = 0; c < seats; c++)
                {
                    int n = r.OutN[d * seats + c];
                    lo = Math.Min(lo, n); hi = Math.Max(hi, n);
                    if (Mathf.Abs(r.Samples[n].K) > 0.02f || r.Samples[n].File) straight = false;
                }
                float skew = (hi - lo) * SampleMetres;
                look.MaxSkew = Mathf.Max(look.MaxSkew, skew);
                if (straight) look.MaxSkewStraight = Mathf.Max(look.MaxSkewStraight, skew);
            }
            foreach (var p in r.Out) if (new Vector2(p.x - r.Load.x, p.z - r.Load.y).sqrMagnitude <= 900f) look.Within30++;
            if (r.PassedAt != null) look.FirstBreak ??= "passed over: " + r.PassedAt;
            return look;
        }

        /// <summary>Task 4.26 BB26 (tests): the line's centre, front first: where, how sharply it bends, whether single file.</summary>
        internal IEnumerable<(Vector2 P, float K, bool File)> CentreOf(int lift)
        {
            var r = _ribbons != null && lift >= 0 && lift < _ribbons.Length ? _ribbons[lift] : null;
            if (r == null) yield break;
            foreach (var s in r.Samples) yield return (s.P, s.K, s.File);
        }

        static float Flat(Vector3 a, Vector3 b) => new Vector2(a.x - b.x, a.z - b.z).magnitude;

        static void Break(ref LineLook look, Vector3 a, Vector3 b, float limit, string what)
        {
            float d = new Vector2(a.x - b.x, a.z - b.z).magnitude;
            if (d <= limit && d >= MinPlaceMetres - 1e-3f) return;
            look.LaneBreaks++;
            look.FirstBreak ??= $"{what}: {d:F2} m at ({a.x:F1},{a.z:F1})";
        }

        /// <summary>Task 4.26 BB26: how many rows of lift <paramref name="lift"/>'s line stand in its throat, square to the lift.</summary>
        public int ThroatRows(int lift)
        {
            if (_ribbons != null) return lift >= 0 && lift < _ribbons.Length && _ribbons[lift] != null ? _ribbons[lift].ThroatRows : 0;
            var l = _lifts != null && lift >= 0 && lift < _lifts.Count ? _lifts[lift] : default;
            return Mathf.Max(1, Mathf.CeilToInt((Mathf.Max(0, l.Behind) + TerminalMarginMetres + l.Seats * ColumnMetres * 0.5f) / RowMetres));
        }
    }
}
