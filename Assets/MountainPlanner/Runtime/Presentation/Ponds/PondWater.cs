using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Ponds
{
    /// <summary>
    /// Task 4.14's pond and reservoir water: one flat opaque mesh per body, built at full pool and moved down to the level
    /// the stored water stands at (PR B, <see cref="SetLevel"/>), over its wet lattice points, each row's run of points one quad, grown a metre at each end
    /// so the water runs under the bank and the ground draws the shoreline through the depth buffer. Built only when the
    /// design's water changes, never per frame; drawn with PondWater.shader, which takes the lakes' ice states by the
    /// body's index in the table (<c>_WaterBodyStates</c>), so a pond freezes with the lakes.
    /// </summary>
    public sealed class PondWater : IDisposable
    {
        public const string ShaderResource = "MountainPlannerWater/PondWater";
        static readonly int BodyId = Shader.PropertyToID("_Body"), SnowOnId = Shader.PropertyToID("_SnowOn");

        /// <summary>One body to draw: its index in the water-body table, its wet points and its water level.</summary>
        public readonly struct Body
        {
            public readonly ulong Id;
            public readonly int TableIndex;
            public readonly WaterMask Wet;
            public readonly double LevelMetres;
            public Body(ulong id, int tableIndex, WaterMask wet, double levelMetres) { Id = id; TableIndex = tableIndex; Wet = wet; LevelMetres = levelMetres; }
        }

        sealed class Drawn
        {
            public GameObject Go;
            public Mesh Mesh;
            public Material Material;
            public WaterMask Wet;
            public double Level;
            public bool Empty;
        }

        readonly Transform _parent;
        readonly Shader _shader;
        readonly Dictionary<ulong, Drawn> _drawn = new Dictionary<ulong, Drawn>();
        bool _snowOn = true, _visible = true;

        public PondWater(Transform parent)
        {
            _parent = parent;
            _shader = Resources.Load<Shader>(ShaderResource);
            if (_shader == null) Debug.LogWarning("PondWater: shader " + ShaderResource + " missing; ponds draw nothing");
        }

        public int Count => _drawn.Count;

        /// <summary>Shows exactly <paramref name="bodies"/>: a body whose wet points and level are unchanged keeps its mesh.</summary>
        public void Show(IReadOnlyList<Body> bodies, EarthworkSite site)
        {
            var keep = new HashSet<ulong>();
            foreach (var b in bodies)
            {
                if (b.Wet == null || b.Wet.Count == 0) continue;
                keep.Add(b.Id);
                if (!_drawn.TryGetValue(b.Id, out var d))
                {
                    if (_shader == null) continue;
                    d = new Drawn { Go = new GameObject("Pond water") };
                    d.Go.SetActive(_visible);
                    d.Go.transform.SetParent(_parent, false);
                    d.Mesh = new Mesh { name = "Pond water", indexFormat = IndexFormat.UInt32 };
                    d.Go.AddComponent<MeshFilter>().sharedMesh = d.Mesh;
                    d.Material = new Material(_shader) { name = "Pond water" };
                    var r = d.Go.AddComponent<MeshRenderer>();
                    r.sharedMaterial = d.Material;
                    r.shadowCastingMode = ShadowCastingMode.Off;
                    r.receiveShadows = true;
                    _drawn[b.Id] = d;
                }
                d.Material.SetFloat(BodyId, b.TableIndex);
                d.Material.SetFloat(SnowOnId, _snowOn ? 1 : 0);
                if (ReferenceEquals(d.Wet, b.Wet) && d.Level == b.LevelMetres) continue;
                d.Wet = b.Wet;
                d.Level = b.LevelMetres;
                Build(d.Mesh, b.Wet, b.LevelMetres, site);
            }
            var gone = new List<ulong>();
            foreach (var kv in _drawn) if (!keep.Contains(kv.Key)) gone.Add(kv.Key);
            foreach (ulong id in gone) { Destroy(_drawn[id]); _drawn.Remove(id); }
        }

        /// <summary>The Snow layer off shows snow-on-ice as bare ice, as the lakes do.</summary>
        public void SetSnowOn(bool on)
        {
            _snowOn = on;
            foreach (var d in _drawn.Values) d.Material.SetFloat(SnowOnId, on ? 1 : 0);
        }

        public void SetVisible(bool on)
        {
            _visible = on;
            foreach (var d in _drawn.Values) d.Go.SetActive(on && !d.Empty);
        }

        /// <summary>
        /// Task 4.14 PR B (WS-02): the water's level now, <paramref name="dropMetres"/> under the level its mesh was built at
        /// (full pool), by moving the mesh: no rebuild, nothing allocated. An empty body (nothing held) isn't drawn.
        /// </summary>
        public void SetLevel(ulong id, float dropMetres, bool empty)
        {
            if (!_drawn.TryGetValue(id, out var d)) return;
            var at = new Vector3(0, -Mathf.Max(0, dropMetres), 0);
            if (d.Go.transform.localPosition != at) d.Go.transform.localPosition = at;
            if (d.Empty == empty) return;
            d.Empty = empty;
            d.Go.SetActive(_visible && !empty);
        }

        /// <summary>The mesh: per lattice row, each run of wet points one quad (half a point each side, plus a metre).</summary>
        public static void Build(Mesh mesh, WaterMask wet, double level, EarthworkSite site)
        {
            var verts = new List<Vector3>();
            var tris = new List<int>();
            var box = wet.Box;
            var lat = site.Lattice;
            float y = (float)level;
            for (int r = box.MinRow; r <= box.MaxRow; r++)
            {
                int c = box.MinColumn;
                while (c <= box.MaxColumn)
                {
                    if (!wet.Has(c, r)) { c++; continue; }
                    int start = c;
                    while (c <= box.MaxColumn && wet.Has(c, r)) c++;
                    float x0 = (float)(lat.X(start) - site.OriginX - 1.5), x1 = (float)(lat.X(c - 1) - site.OriginX + 1.5);
                    float zn = (float)(lat.Y(r) - site.OriginY + 0.5), zs = (float)(lat.Y(r) - site.OriginY - 0.5);
                    int v = verts.Count;
                    verts.Add(new Vector3(x0, y, zs));
                    verts.Add(new Vector3(x0, y, zn));
                    verts.Add(new Vector3(x1, y, zn));
                    verts.Add(new Vector3(x1, y, zs));
                    tris.Add(v); tris.Add(v + 1); tris.Add(v + 2);
                    tris.Add(v); tris.Add(v + 2); tris.Add(v + 3);
                }
            }
            mesh.Clear();
            mesh.SetVertices(verts);
            mesh.SetTriangles(tris, 0);
            var normals = new Vector3[verts.Count];
            for (int i = 0; i < normals.Length; i++) normals[i] = Vector3.up;
            mesh.normals = normals;
            mesh.RecalculateBounds();
        }

        static void Destroy(Drawn d)
        {
            if (d == null) return;
            Kill(d.Go);
            Kill(d.Mesh);
            Kill(d.Material);
        }

        static void Kill(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        public void Dispose()
        {
            foreach (var d in _drawn.Values) Destroy(d);
            _drawn.Clear();
        }
    }
}
