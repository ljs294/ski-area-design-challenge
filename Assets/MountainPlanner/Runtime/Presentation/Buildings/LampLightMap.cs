using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.World.Buildings;
using UnityEngine;

namespace MountainPlanner.Presentation.Buildings
{
    /// <summary>
    /// Task 4.50 slice 3 part 2 (owner 2026-11-25): the building lamps' light on the walls, ground and snow at night. Every
    /// switched-on lamp of every placed building (<see cref="BuildingGlowState.All"/>) goes into one global list the
    /// shaders read through LampLight.hlsl, found through a table of <see cref="LampTiles"/>, so a pixel reads only the
    /// lamps that can reach it (most of the mountain: none, one read). Per lamp, four float4s in the world:
    /// the lens and its range; its colour × strength (candela) and its level class (1 a dusk lamp, 2 one that burns
    /// while its building is in use); its aim and its spread's power (−1 all round); its wall's outward facing and how far
    /// behind the lens the wall is (the light stays on its side). Rebuilt only when buildings come, go or move, or a hut's
    /// lift opens or closes; the levels are two globals (<c>_LampParams</c>.zw), set only when they change. Nothing per
    /// frame but the check. Real light, so the exposure applies as it does to the moon.
    /// </summary>
    public sealed class LampLightMap : IDisposable
    {
        /// <summary>From candela at a metre to the shaders' light units (tuned against moonlit snow; owner review).</summary>
        public const float Gain = 0.003f;   // round 2 pictures: pool centres near blown out at 0.004
        /// <summary>How far the light starts out of its lens, metres (clear of its own fixture).</summary>
        const float OutOfLens = 0.03f;

        static readonly int LampsId = Shader.PropertyToID("_Lamps"), CellsId = Shader.PropertyToID("_LampCells"),
                            IndexId = Shader.PropertyToID("_LampIndex"), GridId = Shader.PropertyToID("_LampGrid"),
                            ParamsId = Shader.PropertyToID("_LampParams");

        readonly LampTiles _tiles = new LampTiles();
        readonly List<Vector4> _lamps = new List<Vector4>();
        readonly List<float> _x = new List<float>(), _z = new List<float>(), _range = new List<float>();
        readonly List<(BuildingGlowState state, Matrix4x4 at, bool inUse)> _built = new List<(BuildingGlowState, Matrix4x4, bool)>();
        GraphicsBuffer _lampBuffer, _cellBuffer, _indexBuffer;
        Vector4 _params = new Vector4(-1, 0, 0, 0);
        bool _any;

        /// <summary>Lamps lit in the list, and tiles in the table (tests, the log).</summary>
        public int LampCount => _x.Count;
        public int TileCount => _tiles.TileCount;
        public LampTiles Tiles => _tiles;
        /// <summary>The list as uploaded, four float4s a lamp (tests).</summary>
        public IReadOnlyList<Vector4> Lamps => _lamps;

        public LampLightMap() => Hide();

        /// <summary>
        /// No lamps, with one-element buffers bound: the shaders declare the buffers, and unbound ones drew black ground on
        /// D3D12 (TrailSurfaceMap, 2026-11-07). Bound before the first scene loads and again whenever the map goes.
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        public static void Hide()
        {
            Shader.SetGlobalVector(ParamsId, Vector4.zero);
            if (_quit || !SystemInfo.supportsComputeShaders) return;
            if (_noneLamps == null || !_noneLamps.IsValid())
            {
                _noneLamps = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 4, 16) { name = "Lamps (none)" };
                _noneCells = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 8) { name = "Lamp tiles (none)" };
                _noneIndex = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 1, 4) { name = "Lamp index (none)" };
                _noneLamps.SetData(new Vector4[4]);
                _noneCells.SetData(new int[2]);
                _noneIndex.SetData(new int[1]);
                Application.quitting += ReleaseNone;
            }
            Shader.SetGlobalBuffer(LampsId, _noneLamps);
            Shader.SetGlobalBuffer(CellsId, _noneCells);
            Shader.SetGlobalBuffer(IndexId, _noneIndex);
            Shader.SetGlobalVector(GridId, Vector4.zero);
        }

        static GraphicsBuffer _noneLamps, _noneCells, _noneIndex;
        static bool _quit;

        static void ReleaseNone()
        {
            Application.quitting -= ReleaseNone;
            _quit = true;
            _noneLamps?.Release(); _noneCells?.Release(); _noneIndex?.Release();
            _noneLamps = _noneCells = _noneIndex = null;
        }

        /// <summary>
        /// Once a frame: rebuilds the list if a building came, went, moved or changed use, then sets the levels:
        /// <paramref name="night"/> (the scene's night weight) for the dusk lamps, × <paramref name="dim"/>
        /// (BuildingGlow.Dim) for the lamps that burn while their building is in use. Returns whether it rebuilt.
        /// </summary>
        public bool Tick(float night, float dim) => Tick(night, dim, BuildingGlowState.All);

        /// <summary>As <see cref="Tick(float, float)"/> over <paramref name="buildings"/> (tests: edit mode registers none).</summary>
        public bool Tick(float night, float dim, IReadOnlyList<BuildingGlowState> buildings)
        {
            if (!SystemInfo.supportsComputeShaders) return false;
            bool rebuilt = false;
            if (Changed(buildings)) { Rebuild(buildings); rebuilt = true; }
            var p = _any && night > 0.004f ? new Vector4(1, Gain, night, night * dim) : Vector4.zero;
            if (p != _params || rebuilt) { _params = p; Shader.SetGlobalVector(ParamsId, p); }
            return rebuilt;
        }

        bool Changed(IReadOnlyList<BuildingGlowState> all)
        {
            int k = 0;
            for (int b = 0; b < all.Count; b++)
            {
                var s = all[b];
                if (s == null || s.Lights == null || !s.isActiveAndEnabled) continue;
                if (k >= _built.Count) return true;
                var was = _built[k++];
                if (was.state != s || was.inUse != s.InUse || was.at != s.transform.localToWorldMatrix) return true;
            }
            return k != _built.Count;
        }

        void Rebuild(IReadOnlyList<BuildingGlowState> all)
        {
            _built.Clear();
            _lamps.Clear(); _x.Clear(); _z.Clear(); _range.Clear();
            for (int b = 0; b < all.Count; b++)
            {
                var s = all[b];
                if (s == null || s.Lights == null || !s.isActiveAndEnabled) continue;
                var m = s.transform.localToWorldMatrix;
                _built.Add((s, m, s.InUse));
                float scale = Mathf.Abs(s.transform.lossyScale.x);
                foreach (var l in s.Lights.Lamps)
                {
                    if (!BuildingGlow.On(l.Schedule, s.InUse)) continue;
                    Add(l, m, scale, l.Schedule == BuildingGlow.Dusk ? 1 : 2);
                }
            }
            _any = _x.Count > 0;
            if (!_any) { Hide(); return; }
            _tiles.Build(_x, _z, _range, _x.Count);
            Upload();
        }

        void Add(in BuildingLights.Lamp l, Matrix4x4 m, float scale, int level)
        {
            var aim = m.MultiplyVector(l.Aim).normalized;
            var at = m.MultiplyPoint3x4(l.Position) + aim * OutOfLens;
            float range = l.RangeMetres * scale;
            bool round = l.ConeDegrees >= 359f;
            // A fixture's spread (round 2): cos^n about its aim, a tenth of its peak at its cone's edge, no hard edge.
            float power = round ? -1f : SpreadPower(l.ConeDegrees);
            // candela along the aim: the lumens over the spread's solid angle, 2π/(n+1) (all round: 4π)
            float candela = round ? l.Lumens / (4f * Mathf.PI) : l.Lumens * (power + 1f) / (2f * Mathf.PI);
            var c = LampColour(l.Kelvin) * candela;
            var facing = l.Facing == Vector3.zero ? Vector3.zero : m.MultiplyVector(l.Facing).normalized;
            _lamps.Add(new Vector4(at.x, at.y, at.z, range));
            _lamps.Add(new Vector4(c.r, c.g, c.b, level));
            _lamps.Add(new Vector4(aim.x, aim.y, aim.z, power));
            _lamps.Add(new Vector4(facing.x, facing.y, facing.z, l.WallMetres * scale + OutOfLens));
            _x.Add(at.x); _z.Add(at.z); _range.Add(range);
        }

        /// <summary>The power n of a cone's cos^n spread: a tenth of the peak at the cone's edge (110°: 4.1; 120°: 3.3).</summary>
        public static float SpreadPower(float coneDegrees)
        {
            float edge = Mathf.Cos(Mathf.Clamp(coneDegrees, 20f, 170f) * 0.5f * Mathf.Deg2Rad);
            return Mathf.Log(0.1f) / Mathf.Log(edge);
        }

        /// <summary>A lamp's colour by its kelvin, linear, its brightest channel 1: the one colour its glare and its light share.</summary>
        public static Color LampColour(int kelvin)
        {
            var c = Mathf.CorrelatedColorTemperatureToRGB(Mathf.Clamp(kelvin, 1000, 20000));
            return c / Mathf.Max(c.r, Mathf.Max(c.g, c.b), 1e-4f);
        }

        void Upload()
        {
            Grow(ref _lampBuffer, _lamps.Count, 16, "Lamps");
            Grow(ref _cellBuffer, Mathf.Max(1, _tiles.TileCount), 8, "Lamp tiles");
            Grow(ref _indexBuffer, Mathf.Max(1, _tiles.Index.Count), 4, "Lamp index");
            _lampBuffer.SetData(_lamps);
            _cellBuffer.SetData(_tiles.Cells, 0, 0, _tiles.Cells.Length);
            if (_tiles.Index.Count > 0) _indexBuffer.SetData(_tiles.Index);
            Shader.SetGlobalBuffer(LampsId, _lampBuffer);
            Shader.SetGlobalBuffer(CellsId, _cellBuffer);
            Shader.SetGlobalBuffer(IndexId, _indexBuffer);
            Shader.SetGlobalVector(GridId, new Vector4(_tiles.Column0, _tiles.Row0, _tiles.Width, _tiles.Height));
        }

        // Room for half as many again, so a resort that grows doesn't resize every time.
        static void Grow(ref GraphicsBuffer b, int count, int stride, string name)
        {
            if (b != null && b.count >= count) return;
            b?.Release();
            b = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(16, count + count / 2), stride) { name = name };
        }

        public void Dispose()
        {
            Hide();
            _lampBuffer?.Release(); _cellBuffer?.Release(); _indexBuffer?.Release();
            _lampBuffer = _cellBuffer = _indexBuffer = null;
            _built.Clear();
            _any = false;
        }
    }
}
