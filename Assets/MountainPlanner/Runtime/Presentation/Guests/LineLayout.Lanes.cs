using System;
using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Guests
{
    public sealed partial class LineLayout
    {
        /// <summary>Lane <paramref name="c"/>'s point beside sample <paramref name="s"/> of a line <paramref name="width"/> wide.</summary>
        static Vector2 LanePoint(in Sample s, int c, float width) =>
            s.P + new Vector2(s.T.y, -s.T.x) * (-width * 0.5f + ColumnMetres * (c + 0.5f));

        /// <summary>
        /// Task 4.26 BB26: lays the places skiers stand on along the ribbon's centre, as far as it is planned. Each column
        /// walks its own lane at a constant offset from the centre, a skier every <see cref="RowMetres"/> of the lane's own
        /// length, so round a bend the outer lanes hold more than the inner, and a skier moving up one place only walks on
        /// along its lane. Single file, the lanes take turns along the centre (the one with fewest first), a row's length
        /// apart; widening again, the lanes start square. Place d·seats + c is lane c's d-th. Incremental: each call lays
        /// only the centre planned since the last, so places never move.
        /// </summary>
        void LayLanes(Ribbon r)
        {
            int seats = r.Seats;
            float width = r.Width;
            var S = r.Samples;
            if (r.Lanes == null)
            {
                r.Lanes = new List<Vector2>[seats]; r.Faces = new List<Vector2>[seats];
                r.Single = new List<bool>[seats]; r.LaneN = new List<int>[seats];
                r.Walked = new float[seats]; r.Prev = new Vector2[seats];
                for (int c = 0; c < seats; c++)
                {
                    r.Lanes[c] = new List<Vector2>(); r.Faces[c] = new List<Vector2>();
                    r.Single[c] = new List<bool>(); r.LaneN[c] = new List<int>();
                    r.Prev[c] = LanePoint(S[0], c, width);
                    r.Walked[c] = RowMetres * 0.5f;   // the front row stands half a row behind the load point
                }
                r.LaidTo = 1;
            }
            // Nobody stands on the last LagMetres planned (the planner may yet take it back), unless the line is done.
            int end = r.Done ? S.Count : Math.Max(r.LaidTo, S.Count - Mathf.RoundToInt(LagMetres / SampleMetres));
            for (int i = r.LaidTo; i < end; i++)
            {
                var s = S[i];
                if (s.File)
                {
                    if (!r.InFile) { r.InFile = true; r.FileWalk = 0; }
                    var from = S[i - 1].P;
                    float step = (s.P - from).magnitude;
                    r.FileWalk += step;
                    if (r.FileWalk >= RowMetres && step > 1e-6f)
                    {
                        float frac = Mathf.Clamp01(1f - (r.FileWalk - RowMetres) / step);
                        int least = 0;
                        for (int c = 1; c < seats; c++) if (r.Lanes[c].Count < r.Lanes[least].Count) least = c;
                        // Too near another place (where the lanes merge): a little further on, at most WaitMetres.
                        if (Put(r, least, Vector2.Lerp(from, s.P, frac), s.T, true, i, r.FileWalk - RowMetres > WaitMetres)) r.FileWalk -= RowMetres;
                        else if (r.FileWalk - RowMetres > WaitMetres) r.FileWalk -= RowMetres;
                    }
                    continue;
                }
                if (r.InFile)
                {
                    // Full width again: every lane starts a row's length after the file's last skier, square.
                    r.InFile = false;
                    for (int c = 0; c < seats; c++) { r.Prev[c] = LanePoint(s, c, width); r.Walked[c] = r.FileWalk; }
                    continue;
                }
                for (int c = 0; c < seats; c++)
                {
                    var q = LanePoint(s, c, width);
                    float step = (q - r.Prev[c]).magnitude;
                    r.Walked[c] += step;
                    if (r.Walked[c] >= RowMetres && step > 1e-6f)
                    {
                        float frac = Mathf.Clamp01((RowMetres - (r.Walked[c] - step)) / step);
                        bool last = r.Walked[c] - RowMetres > WaitMetres;
                        if (Put(r, c, Vector2.Lerp(r.Prev[c], q, frac), s.T, false, i, last) || last) r.Walked[c] -= RowMetres;
                    }
                    r.Prev[c] = q;
                }
            }
            r.LaidTo = end;
            EmitRows(r);
        }

        /// <summary>
        /// A place in lane <paramref name="c"/>, facing the front of the line: toward the one ahead in its lane when that
        /// one stands about a row ahead, else back along the line. Not laid if it would stand nearer than
        /// <see cref="MinPlaceMetres"/> to another place: tried again a little further on, and passed over (counted) when
        /// <paramref name="last"/>.
        /// </summary>
        bool Put(Ribbon r, int c, Vector2 p, Vector2 t, bool file, int n, bool last)
        {
            if (!Free(p)) { if (last) { r.PassedOver++; if (r.PassedAt == null) r.PassedAt = $"lane {c} at ({p.x:F1}, {p.y:F1}) sample {n} file {file}"; } return false; }
            var lane = r.Lanes[c];
            var face = -t;
            if (lane.Count > 0)
            {
                var toward = lane[lane.Count - 1] - p;
                float gap = toward.magnitude;
                if (gap >= 0.9f * MinLaneGapMetres && gap <= 1.5f * RowMetres) face = toward / gap;
            }
            lane.Add(p);
            r.Faces[c].Add(face);
            r.Single[c].Add(file);
            r.LaneN[c].Add(n);
            _taken[CellKey(p)] = p;
            return true;
        }

        /// <summary>How much further on a place may wait for room (lanes merging into a file), metres.</summary>
        const float WaitMetres = 0.6f;

        /// <summary>Rows of places: row d is every lane's d-th, added once every lane has one.</summary>
        void EmitRows(Ribbon r)
        {
            int seats = r.Seats;
            int depth = int.MaxValue;
            for (int c = 0; c < seats; c++) depth = Math.Min(depth, r.Lanes[c].Count);
            var grid = _grids[r.Lift];
            for (int d = r.Depth; d < depth && r.Out.Count + seats <= MaxSlots; d++)
            {
                r.Depth = d + 1;
                bool file = false;
                int maxN = 0;
                for (int c = 0; c < seats; c++) maxN = Math.Max(maxN, r.LaneN[c][d]);
                bool throat = r.ThroatOpen && maxN <= r.ThroatEnd;
                for (int c = 0; c < seats; c++)
                {
                    var p = r.Lanes[c][d];
                    var face = r.Faces[c][d];
                    // Single file (and the row after it) turn only: who's ahead there isn't the one a row ahead in the lane.
                    bool still = r.Single[c][d] || (d > 0 && r.Single[c][d - 1]);
                    if (!throat && Vary) Jitter(r, c, ref p, ref face, still);
                    r.Out.Add(new Vector3(p.x, Height(p), p.y));
                    r.OutFaces.Add(face);
                    r.OutN.Add(r.LaneN[c][d]);
                    r.OutFile.Add(r.Single[c][d]);
                    bool single = r.Single[c][d];
                    file |= single;
                    if (single) r.WalkPlaces++;
                    if (grid.State(Grid.Cell(p.x), Grid.Cell(p.y)) == Soft) r.SoftPlaces++;
                }
                int last = r.SectionRows.Count - 1;
                if (last >= 0 && r.SectionFile[last] == file) r.SectionRows[last]++;
                else { r.SectionRows.Add(1); r.SectionFile.Add(file); }
                if (throat) r.ThroatRows++;
                else r.ThroatOpen = false;
            }
        }

        /// <summary>Natural variation (owner, 2026-11-24): each skier a little off their mark, keyed by line and place.</summary>
        internal static bool Vary = true;
        /// <summary>The most a skier stands off their mark along the line and across it, metres, and turns, degrees.</summary>
        public const float VaryAlongMetres = 0.12f, VaryAcrossMetres = 0.08f, VaryFacingDegrees = 8f;

        /// <summary>
        /// Moves the place about to be added (place <c>r.Out.Count</c>, lane <paramref name="c"/>) a little along and across
        /// its facing and turns it a little, keyed by the line and the place: the same line stands the same way every
        /// time. Half as far (or not at all) where it would stand nearer than <see cref="MinPlaceMetres"/> to the one
        /// ahead in its lane or beside it in its row.
        /// </summary>
        void Jitter(Ribbon r, int c, ref Vector2 p, ref Vector2 face, bool turnOnly)
        {
            int k = r.Out.Count;
            float along = (2f * Hash01(r.Key, 3 * k) - 1f) * VaryAlongMetres;
            float across = (2f * Hash01(r.Key, 3 * k + 1) - 1f) * VaryAcrossMetres;
            float turn = (2f * Hash01(r.Key, 3 * k + 2) - 1f) * VaryFacingDegrees * Mathf.Deg2Rad;
            var off = turnOnly ? Vector2.zero : face * along + new Vector2(face.y, -face.x) * across;
            var grid = _grids[r.Lift];
            byte ground = grid.State(Grid.Cell(p.x), Grid.Cell(p.y));
            int ahead = k - r.Seats;
            float was = ahead >= 0 ? (Xz(r.Out[ahead]) - p).magnitude : 0;
            // Never nearer anyone than MinPlaceMetres, whichever way they move too: at most half the room there is.
            float room = Room(p), len = off.magnitude;
            if (len > 1e-6f) off *= Mathf.Clamp01((room - MinPlaceMetres) / (2f * len));
            for (int tries = 0; tries < 2; tries++, off *= 0.5f)
            {
                var q = p + off;
                // Never off the ground it was laid on (into the trees), and never further from the one ahead in its lane
                // than a row and a half (when it was within that).
                if (grid.State(Grid.Cell(q.x), Grid.Cell(q.y)) != ground) continue;
                if (ahead >= 0 && was <= LaneGapLimit && (Xz(r.Out[ahead]) - q).magnitude > LaneGapLimit) continue;
                // Where it stands now, for the room of those laid after it.
                if (_taken.TryGetValue(CellKey(p), out var at) && at == p) _taken.Remove(CellKey(p));
                _taken[CellKey(q)] = q;
                p = q;
                break;
            }
            float cs = Mathf.Cos(turn), sn = Mathf.Sin(turn);
            face = new Vector2(face.x * cs - face.y * sn, face.x * sn + face.y * cs);
        }

        static Vector2 Xz(Vector3 v) => new Vector2(v.x, v.z);

        /// <summary>How far the nearest other place (laid, of any line) stands from place <paramref name="p"/>, up to 1.5 m.</summary>
        float Room(Vector2 p)
        {
            float best = 3f * CellMetres;
            int ci = Mathf.FloorToInt(p.x / CellMetres), cj = Mathf.FloorToInt(p.y / CellMetres);
            for (int di = -3; di <= 3; di++)
                for (int dj = -3; dj <= 3; dj++)
                    if (_taken.TryGetValue(((long)(ci + di) << 32) ^ (uint)(cj + dj), out var q))
                    {
                        float d = (q - p).magnitude;
                        if (d > 1e-4f && d < best) best = d;
                    }
            return best;
        }

        /// <summary>The furthest a skier stands behind the one ahead in their lane without it reading as a break, metres.</summary>
        const float LaneGapLimit = 1.5f * RowMetres - 0.05f;
    }
}
