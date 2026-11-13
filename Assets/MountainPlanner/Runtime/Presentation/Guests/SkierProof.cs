using System;
using System.Collections.Generic;
using MountainPlanner.Simulation;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// Task 4.13 PR C2, <c>-skierproof [lod|lod0]</c>: what real 3D skiers would cost, measured before anyone models one
    /// (owner, 2026-11-09: a proof only). Every dot within <see cref="Reach"/> of the camera and in view becomes a plain
    /// grey stand-in figure (a lathed body, two skis, two poles; built here, never shipped, not art) at the triangle
    /// budget a real model would have at that distance (<see cref="BandMetres"/>, <see cref="TriangleBudget"/>), turned
    /// the way it last moved. Dots further away still draw; a dot with a figure is hidden. A Burst job picks the figures
    /// from the frame's placed dots (no readback, no per-skier objects) into one buffer by band, and one
    /// <see cref="Graphics.RenderMeshPrimitives"/> per band draws them, lit and casting shadows as a real model would.
    /// <c>lod0</c> draws every figure at the closest band's mesh, the worst case. Nothing is allocated per frame.
    /// </summary>
    public sealed class SkierProof : IDisposable
    {
        public const string ShaderResource = "MountainPlannerGuests/SkierProof";
        /// <summary>The far edge of each band: LOD0 to 30 m, LOD1 to 100 m, LOD2 to 250 m (the study's budgets).</summary>
        public static readonly float[] BandMetres = { 30f, 100f, 250f };
        /// <summary>The most triangles each band's mesh may have.</summary>
        public static readonly int[] TriangleBudget = { 1500, 400, 80 };
        public const float Reach = 250f;
        public const int Bands = 3;

        readonly int _cap;
        readonly Material _material;
        readonly Mesh[] _meshes = new Mesh[Bands];
        readonly int[] _meshTriangles = new int[Bands];
        readonly MaterialPropertyBlock[] _props = new MaterialPropertyBlock[Bands];
        readonly Plane[] _planeScratch = new Plane[6];
        NativeArray<float4> _near;     // Bands × cap: band b's figures from b × cap (xyz feet, w band + heading share)
        NativeArray<int> _counts;      // per band
        NativeArray<float3> _last;     // per record: where its dot was last frame
        NativeArray<float> _heading;   // per record: the way it last moved (radians from +z)
        NativeArray<uint> _lastSerial;
        NativeArray<float4> _planes;
        GraphicsBuffer _buffer;
        static readonly int NearId = Shader.PropertyToID("_Near"), BaseId = Shader.PropertyToID("_Base");

        /// <summary>Every figure at the closest band's mesh (<c>-skierproof lod0</c>): the worst case.</summary>
        public bool AllLod0;

        // Logs: per frame, summed over the run.
        readonly System.Diagnostics.Stopwatch _watch = new System.Diagnostics.Stopwatch();
        long _frames, _figures, _triangles;
        int _mostFigures;
        double _selectMs;
        readonly long[] _perBand = new long[Bands];

        public SkierProof(Shader shader, int cap)
        {
            _cap = Math.Max(1, cap);
            _material = shader != null ? new Material(shader) { hideFlags = HideFlags.HideAndDontSave } : null;
            for (int b = 0; b < Bands; b++)
            {
                _meshes[b] = BuildStandIn(b);
                _meshTriangles[b] = (int)(_meshes[b].GetIndexCount(0) / 3);
                _props[b] = new MaterialPropertyBlock();
            }
            _near = new NativeArray<float4>(Bands * _cap, Allocator.Persistent);
            _counts = new NativeArray<int>(Bands, Allocator.Persistent);
            _last = new NativeArray<float3>(_cap, Allocator.Persistent);
            _heading = new NativeArray<float>(_cap, Allocator.Persistent);
            _lastSerial = new NativeArray<uint>(_cap, Allocator.Persistent);
            _planes = new NativeArray<float4>(6, Allocator.Persistent);
            _buffer = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Bands * _cap, 16);
        }

        static void Release(UnityEngine.Object o)
        {
            if (o == null) return;
            if (Application.isPlaying) UnityEngine.Object.Destroy(o); else UnityEngine.Object.DestroyImmediate(o);
        }

        /// <summary>Band <paramref name="band"/>'s figures this frame (tests, logs).</summary>
        public int CountIn(int band) => band >= 0 && band < Bands ? _counts[band] : 0;

        /// <summary>The figures this frame.</summary>
        public int Figures { get; private set; }

        /// <summary>
        /// After the dots are placed and before they upload: picks the figures from <paramref name="dots"/> and hides their
        /// dots (w = hidden), so the frame draws each skier once.
        /// </summary>
        public void Select(NativeArray<float4> dots, NativeArray<SkierRender> records, int count, Camera camera)
        {
            Figures = 0;
            if (camera == null || count <= 0) return;
            _watch.Restart();
            GeometryUtility.CalculateFrustumPlanes(camera, _planeScratch);
            for (int i = 0; i < 6; i++) _planes[i] = new float4(_planeScratch[i].normal, _planeScratch[i].distance);
            var job = new SelectJob
            {
                Dots = dots, Records = records, Count = math.min(count, _cap), Cap = _cap,
                Camera = camera.transform.position, Planes = _planes, AllLod0 = AllLod0,
                Near0 = BandMetres[0], Near1 = BandMetres[1], Far = Reach,
                Last = _last, Heading = _heading, LastSerial = _lastSerial,
                Out = _near, Counts = _counts,
            };
            job.Run();
            for (int b = 0; b < Bands; b++)
            {
                int n = _counts[b];
                if (n > 0) _buffer.SetData(_near, b * _cap, b * _cap, n);
                Figures += n;
                _perBand[b] += n;
                _triangles += (long)n * _meshTriangles[AllLod0 ? 0 : b];
            }
            _selectMs += _watch.Elapsed.TotalMilliseconds;
            _frames++;
            _figures += Figures;
            if (Figures > _mostFigures) _mostFigures = Figures;
        }

        /// <summary>One draw per band with figures in it (after <see cref="Select"/>).</summary>
        public void Draw(Camera camera)
        {
            if (_material == null || camera == null) return;
            _material.SetBuffer(NearId, _buffer);
            for (int b = 0; b < Bands; b++)
            {
                int n = _counts[b];
                if (n <= 0) continue;
                _props[b].SetInteger(BaseId, b * _cap);
                var rp = new RenderParams(_material)
                {
                    camera = camera,
                    matProps = _props[b],
                    worldBounds = new Bounds(Vector3.zero, Vector3.one * 100_000f),
                    shadowCastingMode = ShadowCastingMode.On,
                    receiveShadows = true,
                };
                Graphics.RenderMeshPrimitives(rp, _meshes[AllLod0 ? 0 : b], 0, n);
            }
        }

        /// <summary>The run's figures for the log ("[SkierProof] …").</summary>
        public string Summary()
        {
            if (_frames == 0) return "no frames";
            double f = _frames;
            return $"{(AllLod0 ? "lod0" : "lod")}: {_figures / f:F0} figures a frame on average (most {_mostFigures}; by band " +
                   $"{_perBand[0] / f:F0}/{_perBand[1] / f:F0}/{_perBand[2] / f:F0}), {_triangles / f / 1000.0:F0}k triangles a frame, " +
                   $"selection {_selectMs / f:F3} ms a frame; meshes {_meshTriangles[0]}/{_meshTriangles[1]}/{_meshTriangles[2]} triangles";
        }

        [BurstCompile]
        struct SelectJob : IJob
        {
            public NativeArray<float4> Dots;
            [ReadOnly] public NativeArray<SkierRender> Records;
            [ReadOnly] public NativeArray<float4> Planes;
            public int Count, Cap;
            public float3 Camera;
            public bool AllLod0;
            public float Near0, Near1, Far;
            public NativeArray<float3> Last;
            public NativeArray<float> Heading;
            public NativeArray<uint> LastSerial;
            [WriteOnly] public NativeArray<float4> Out;
            public NativeArray<int> Counts;

            public void Execute()
            {
                int c0 = 0, c1 = 0, c2 = 0;
                for (int i = 0; i < Count; i++)
                {
                    float4 d = Dots[i];
                    uint serial = Records[i].Serial;
                    if (d.w < 0) { LastSerial[i] = 0; continue; }
                    float3 p = d.xyz;
                    // The way it last moved; a skier standing still keeps the last heading it had.
                    if (LastSerial[i] == serial)
                    {
                        float2 delta = p.xz - Last[i].xz;
                        if (math.lengthsq(delta) > 1e-4f) Heading[i] = math.atan2(delta.x, delta.y);
                    }
                    else Heading[i] = 0;
                    LastSerial[i] = serial;
                    Last[i] = p;
                    float dist = math.distance(p, Camera);
                    if (dist > Far || !InView(p + new float3(0, 0.9f, 0))) continue;
                    int band = AllLod0 || dist <= Near0 ? 0 : dist <= Near1 ? 1 : 2;
                    float share = Heading[i] / (2f * math.PI);
                    share -= math.floor(share);
                    var figure = new float4(p, (Records[i].Band & 3) + share * 0.998f);
                    if (band == 0) Out[c0++] = figure;
                    else if (band == 1) Out[Cap + c1++] = figure;
                    else Out[2 * Cap + c2++] = figure;
                    Dots[i] = new float4(p, SkierPlaceJob.Hidden);
                }
                Counts[0] = c0; Counts[1] = c1; Counts[2] = c2;
            }

            bool InView(float3 p)
            {
                for (int k = 0; k < 6; k++)
                {
                    float4 pl = Planes[k];
                    if (math.dot(pl.xyz, p) + pl.w < -1.2f) return false;   // a figure's bounding radius
                }
                return true;
            }
        }

        // ---------- the stand-in meshes ----------

        /// <summary>
        /// A standing skier's silhouette as (height, radius) in metres, leaning forward (+z) a little: feet, knees, hips,
        /// waist, chest, shoulders, neck, head.
        /// </summary>
        static readonly Vector2[] Profile =
        {
            new Vector2(0.00f, 0.10f), new Vector2(0.10f, 0.11f), new Vector2(0.45f, 0.12f), new Vector2(0.80f, 0.17f),
            new Vector2(1.00f, 0.16f), new Vector2(1.30f, 0.21f), new Vector2(1.42f, 0.22f), new Vector2(1.50f, 0.08f),
            new Vector2(1.55f, 0.10f), new Vector2(1.65f, 0.11f), new Vector2(1.74f, 0.07f), new Vector2(1.78f, 0.00f),
        };

        /// <summary>
        /// Band <paramref name="band"/>'s stand-in: a lathed body of <c>sides × (rings − 1) × 2</c> triangles plus skis
        /// and poles, sized to fill most of <see cref="TriangleBudget"/> (1,440, 396 and 64 triangles). Vertex colour
        /// red marks the jacket (the shader tints it by ability, as the dots are).
        /// </summary>
        public static Mesh BuildStandIn(int band)
        {
            int sides = band == 0 ? 24 : band == 1 ? 12 : 6;
            int rings = band == 0 ? 30 : band == 1 ? 16 : 6;
            int poleSides = band == 0 ? 6 : band == 1 ? 3 : 0;
            var v = new List<Vector3>();
            var c = new List<Color>();
            var t = new List<int>();
            Lathe(v, c, t, sides, rings);
            Ski(v, c, t, -0.12f, box: band < 2);
            Ski(v, c, t, 0.12f, box: band < 2);
            if (poleSides > 0)
            {
                Pole(v, c, t, poleSides, new Vector3(-0.32f, 1.00f, 0.30f), new Vector3(-0.36f, 0f, 0.05f));
                Pole(v, c, t, poleSides, new Vector3(0.32f, 1.00f, 0.30f), new Vector3(0.36f, 0f, 0.05f));
            }
            var mesh = new Mesh { name = "SkierStandIn" + band, hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertices(v);
            mesh.SetColors(c);
            mesh.SetTriangles(t, 0);
            mesh.RecalculateNormals();
            mesh.bounds = new Bounds(new Vector3(0, 0.9f, 0.2f), new Vector3(1.0f, 1.9f, 1.9f));
            return mesh;
        }

        static float RadiusAt(float h, out float lean)
        {
            lean = 0.15f * h / 1.78f;   // the body leans forward over the skis
            for (int k = 1; k < Profile.Length; k++)
            {
                if (h > Profile[k].x) continue;
                float u = (h - Profile[k - 1].x) / Mathf.Max(1e-4f, Profile[k].x - Profile[k - 1].x);
                return Mathf.Lerp(Profile[k - 1].y, Profile[k].y, u);
            }
            return 0;
        }

        static void Lathe(List<Vector3> v, List<Color> c, List<int> t, int sides, int rings)
        {
            int start = v.Count;
            float top = Profile[Profile.Length - 1].x;
            for (int r = 0; r < rings; r++)
            {
                float h = top * r / (rings - 1);
                float radius = RadiusAt(h, out float lean);
                bool jacket = h >= 0.85f && h <= 1.45f;
                for (int s = 0; s <= sides; s++)
                {
                    float a = 2f * Mathf.PI * s / sides;
                    v.Add(new Vector3(Mathf.Sin(a) * radius, h, Mathf.Cos(a) * radius * 0.8f + lean));
                    c.Add(jacket ? Color.red : Color.black);
                }
            }
            int row = sides + 1;
            for (int r = 0; r < rings - 1; r++)
                for (int s = 0; s < sides; s++)
                {
                    int a = start + r * row + s, b = a + row;
                    t.Add(a); t.Add(b); t.Add(a + 1);
                    t.Add(a + 1); t.Add(b); t.Add(b + 1);
                }
        }

        static void Ski(List<Vector3> v, List<Color> c, List<int> t, float x, bool box)
        {
            const float half = 0.045f, back = -0.7f, front = 0.95f, thick = 0.03f;
            int s = v.Count;
            if (!box)
            {
                v.Add(new Vector3(x - half, 0.01f, back)); v.Add(new Vector3(x + half, 0.01f, back));
                v.Add(new Vector3(x + half, 0.01f, front)); v.Add(new Vector3(x - half, 0.01f, front));
                for (int k = 0; k < 4; k++) c.Add(Color.black);
                t.Add(s); t.Add(s + 2); t.Add(s + 1); t.Add(s); t.Add(s + 3); t.Add(s + 2);
                return;
            }
            for (int k = 0; k < 8; k++)
            {
                v.Add(new Vector3(x + ((k & 1) == 0 ? -half : half), (k & 2) == 0 ? 0f : thick, (k & 4) == 0 ? back : front));
                c.Add(Color.black);
            }
            int[] faces = { 0, 1, 3, 2,  4, 6, 7, 5,  0, 4, 5, 1,  2, 3, 7, 6,  0, 2, 6, 4,  1, 5, 7, 3 };
            for (int f = 0; f < faces.Length; f += 4)
            {
                t.Add(s + faces[f]); t.Add(s + faces[f + 1]); t.Add(s + faces[f + 2]);
                t.Add(s + faces[f]); t.Add(s + faces[f + 2]); t.Add(s + faces[f + 3]);
            }
        }

        static void Pole(List<Vector3> v, List<Color> c, List<int> t, int sides, Vector3 from, Vector3 to)
        {
            const float radius = 0.012f;
            var axis = (to - from).normalized;
            var side = Vector3.Cross(axis, Vector3.forward).normalized;
            var up = Vector3.Cross(side, axis);
            int s = v.Count;
            for (int end = 0; end < 2; end++)
                for (int k = 0; k < sides; k++)
                {
                    float a = 2f * Mathf.PI * k / sides;
                    v.Add((end == 0 ? from : to) + (side * Mathf.Cos(a) + up * Mathf.Sin(a)) * radius);
                    c.Add(Color.black);
                }
            for (int k = 0; k < sides; k++)
            {
                int a = s + k, b = s + (k + 1) % sides;
                t.Add(a); t.Add(a + sides); t.Add(b);
                t.Add(b); t.Add(a + sides); t.Add(b + sides);
            }
        }

        public void Dispose()
        {
            if (_near.IsCreated) _near.Dispose();
            if (_counts.IsCreated) _counts.Dispose();
            if (_last.IsCreated) _last.Dispose();
            if (_heading.IsCreated) _heading.Dispose();
            if (_lastSerial.IsCreated) _lastSerial.Dispose();
            if (_planes.IsCreated) _planes.Dispose();
            _buffer?.Release(); _buffer = null;
            foreach (var m in _meshes) Release(m);
            Release(_material);
        }
    }
}
