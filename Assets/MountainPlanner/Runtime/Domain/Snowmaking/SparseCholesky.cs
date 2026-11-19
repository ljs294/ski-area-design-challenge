using System;
using System.Collections.Generic;

namespace MountainPlanner.Domain.Snowmaking
{
    /// <summary>
    /// A sparse Cholesky factorisation (LLᵀ) of a symmetric positive-definite matrix with a fixed pattern, for HY-06's
    /// Newton steps (plan v2, M6: exact, fixed cost and deterministic, where MapLibre's Jacobi CG wandered on stiff pumps).
    /// The ordering (minimum degree, ties to the lowest index) and the fill pattern are worked out once per network
    /// shape; each step refactorises numerically into the same arrays and allocates nothing.
    /// </summary>
    public sealed class SparseCholesky
    {
        public int Size { get; }
        /// <summary>Nonzeros of L, the diagonal included.</summary>
        public int NonZeros => _rowIdx.Length;

        readonly int[] _perm;      // new index → old index
        readonly int[] _inv;       // old index → new index
        readonly int[] _colStart;  // CSC of L in the new order: column j's rows are _rowIdx[_colStart[j] .. _colStart[j+1]), the diagonal first
        readonly int[] _rowIdx;
        readonly double[] _val;
        readonly double[] _a;      // the matrix to factor, on L's pattern (lower triangle, new order)
        readonly int[] _rowStart;  // for each row j, the columns k < j with L[j,k] ≠ 0, and where L[j,k] sits
        readonly int[] _rowCol;
        readonly int[] _rowPos;
        readonly double[] _work;
        readonly int[] _workPos;   // row → position in the current column, or -1
        readonly double[] _y;

        /// <summary>Plans the factorisation of an <paramref name="n"/> × <paramref name="n"/> matrix whose off-diagonal
        /// nonzeros are <paramref name="edges"/> (pairs of old indices, either order, repeats allowed).</summary>
        public SparseCholesky(int n, IReadOnlyList<(int, int)> edges)
        {
            Size = n;
            var adj = new List<int>[n];
            for (int i = 0; i < n; i++) adj[i] = new List<int>();
            foreach (var (a, b) in edges)
            {
                if (a == b) continue;
                adj[a].Add(b);
                adj[b].Add(a);
            }
            _perm = MinimumDegree(n, adj);
            _inv = new int[n];
            for (int i = 0; i < n; i++) _inv[_perm[i]] = i;

            // Symbolic elimination in the new order: column j of L holds j and every later neighbour left after
            // eliminating 0..j-1. A column's pattern is its own neighbours above j, merged with its children's.
            var cols = new SortedSet<int>[n];
            var parent = new int[n];
            var children = new List<int>[n];
            for (int j = 0; j < n; j++) children[j] = new List<int>();
            for (int j = 0; j < n; j++)
            {
                var s = new SortedSet<int>();
                foreach (int o in adj[_perm[j]]) { int k = _inv[o]; if (k > j) s.Add(k); }
                foreach (int c in children[j]) foreach (int k in cols[c]) if (k > j) s.Add(k);
                cols[j] = s;
                parent[j] = s.Count > 0 ? s.Min : -1;
                if (parent[j] >= 0) children[parent[j]].Add(j);
            }
            _colStart = new int[n + 1];
            for (int j = 0; j < n; j++) _colStart[j + 1] = _colStart[j] + 1 + cols[j].Count;
            _rowIdx = new int[_colStart[n]];
            for (int j = 0; j < n; j++)
            {
                int p = _colStart[j];
                _rowIdx[p++] = j;
                foreach (int k in cols[j]) _rowIdx[p++] = k;
            }
            _val = new double[_rowIdx.Length];
            _a = new double[_rowIdx.Length];

            var rowLists = new List<(int col, int pos)>[n];
            for (int i = 0; i < n; i++) rowLists[i] = new List<(int, int)>();
            for (int k = 0; k < n; k++)
                for (int p = _colStart[k] + 1; p < _colStart[k + 1]; p++) rowLists[_rowIdx[p]].Add((k, p));
            _rowStart = new int[n + 1];
            for (int i = 0; i < n; i++) _rowStart[i + 1] = _rowStart[i] + rowLists[i].Count;
            _rowCol = new int[_rowStart[n]];
            _rowPos = new int[_rowStart[n]];
            for (int i = 0; i < n; i++)
            {
                int p = _rowStart[i];
                foreach (var (c, pos) in rowLists[i]) { _rowCol[p] = c; _rowPos[p] = pos; p++; }
            }
            _work = new double[n];
            _workPos = new int[n];
            for (int i = 0; i < n; i++) _workPos[i] = -1;
            _y = new double[n];
        }

        /// <summary>Where entry (i, j) of the matrix (old indices) sits in <see cref="Clear"/>/<see cref="Add"/>'s store.</summary>
        public int Slot(int i, int j)
        {
            int a = _inv[i], b = _inv[j];
            if (a < b) { int t = a; a = b; b = t; }
            for (int p = _colStart[b]; p < _colStart[b + 1]; p++) if (_rowIdx[p] == a) return p;
            throw new ArgumentException("That entry isn't in the pattern.");
        }

        public void Clear() => Array.Clear(_a, 0, _a.Length);
        public void Add(int slot, double v) => _a[slot] += v;

        /// <summary>Factors the assembled matrix. False if it isn't positive definite.</summary>
        public bool Factor()
        {
            int n = Size;
            for (int j = 0; j < n; j++)
            {
                int c0 = _colStart[j], c1 = _colStart[j + 1];
                for (int p = c0; p < c1; p++) { _work[_rowIdx[p]] = _a[p]; _workPos[_rowIdx[p]] = p; }
                // Every earlier column k with L[j,k] ≠ 0 updates column j: w[i] -= L[i,k]·L[j,k] for rows i ≥ j of column k.
                for (int q = _rowStart[j]; q < _rowStart[j + 1]; q++)
                {
                    int k = _rowCol[q], pjk = _rowPos[q];
                    double ljk = _val[pjk];
                    for (int p = pjk; p < _colStart[k + 1]; p++) _work[_rowIdx[p]] -= _val[p] * ljk;
                }
                double d = _work[j];
                if (!(d > 0) || double.IsInfinity(d))
                {
                    for (int p = c0; p < c1; p++) { _work[_rowIdx[p]] = 0; _workPos[_rowIdx[p]] = -1; }
                    return false;
                }
                d = Math.Sqrt(d);
                _val[c0] = d;
                for (int p = c0 + 1; p < c1; p++) _val[p] = _work[_rowIdx[p]] / d;
                for (int p = c0; p < c1; p++) { _work[_rowIdx[p]] = 0; _workPos[_rowIdx[p]] = -1; }
            }
            return true;
        }

        /// <summary>Solves A x = b after <see cref="Factor"/>; b and x are in the old order, and may be the same array.</summary>
        public void Solve(double[] b, double[] x)
        {
            int n = Size;
            for (int i = 0; i < n; i++) _y[i] = b[_perm[i]];
            for (int j = 0; j < n; j++)
            {
                int c0 = _colStart[j];
                double v = _y[j] / _val[c0];
                _y[j] = v;
                for (int p = c0 + 1; p < _colStart[j + 1]; p++) _y[_rowIdx[p]] -= _val[p] * v;
            }
            for (int j = n - 1; j >= 0; j--)
            {
                int c0 = _colStart[j];
                double v = _y[j];
                for (int p = c0 + 1; p < _colStart[j + 1]; p++) v -= _val[p] * _y[_rowIdx[p]];
                _y[j] = v / _val[c0];
            }
            for (int i = 0; i < n; i++) x[_perm[i]] = _y[i];
        }

        /// <summary>
        /// A minimum-degree ordering on the elimination graph, ties to the lowest index (deterministic). Networks are close to
        /// trees, so leaves go first and fill stays small.
        /// </summary>
        static int[] MinimumDegree(int n, List<int>[] adj)
        {
            var nb = new HashSet<int>[n];
            for (int i = 0; i < n; i++) nb[i] = new HashSet<int>(adj[i]);
            var done = new bool[n];
            var order = new int[n];
            // Buckets by degree, each a sorted set so the lowest index comes first.
            var buckets = new SortedDictionary<int, SortedSet<int>>();
            var degree = new int[n];
            for (int i = 0; i < n; i++) { degree[i] = nb[i].Count; Bucket(degree[i]).Add(i); }
            var scratch = new List<int>();
            for (int step = 0; step < n; step++)
            {
                int d = -1;
                foreach (var kv in buckets) if (kv.Value.Count > 0) { d = kv.Key; break; }
                int v = buckets[d].Min;
                buckets[d].Remove(v);
                done[v] = true;
                order[step] = v;
                scratch.Clear();
                foreach (int u in nb[v]) if (!done[u]) scratch.Add(u);
                scratch.Sort();
                // Eliminating v joins its remaining neighbours into a clique.
                foreach (int u in scratch)
                {
                    nb[u].Remove(v);
                    foreach (int w in scratch) if (w != u) nb[u].Add(w);
                    if (nb[u].Count != degree[u])
                    {
                        buckets[degree[u]].Remove(u);
                        degree[u] = nb[u].Count;
                        Bucket(degree[u]).Add(u);
                    }
                }
                nb[v].Clear();
            }
            return order;

            SortedSet<int> Bucket(int deg)
            {
                if (!buckets.TryGetValue(deg, out var s)) buckets[deg] = s = new SortedSet<int>();
                return s;
            }
        }
    }
}
