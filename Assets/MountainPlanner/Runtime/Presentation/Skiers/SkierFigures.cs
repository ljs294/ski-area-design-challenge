using System;
using MountainPlanner.Presentation.Guests;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Skiers
{
    /// <summary>
    /// Task 4.22: the guests drawn as skiers and snowboarders where the dots used to be (owner, 2026-11-12: straight into
    /// the game). The figures are the Blender pipeline's (<see cref="SkierFigureData"/>), skinned in the vertex shader from
    /// baked bone matrices (no Animator, nothing per guest on the CPU but one Burst pass): one draw per figure and LOD,
    /// twelve in all. A guest farther than the last LOD's distance stays a dot (<see cref="DotCutoff"/>). Nothing is
    /// allocated per frame.
    /// </summary>
    public sealed class SkierFigures : IDisposable
    {
        public const string ShaderResource = "MountainPlannerSkiers/SkierFigure";
        public const string DataResource = "MountainPlannerSkiers/skiers";
        /// <summary>The LODs that cast a shadow (budgets.json: the three nearest).</summary>
        public const int ShadowLods = 3;

        static readonly string[] Styles = { "green", "blue", "black", "expert" };
        static readonly int BonesId = Shader.PropertyToID("_Bones"), BoneCountId = Shader.PropertyToID("_BoneCount"),
                            OutfitsId = Shader.PropertyToID("_Outfits"), FiguresId = Shader.PropertyToID("_Figures"),
                            BaseId = Shader.PropertyToID("_Base"), InfluencesId = Shader.PropertyToID("_Influences");

        readonly Material _material;
        readonly Mesh[] _meshes = new Mesh[SkierFigureJob.Groups];
        readonly int[] _influences = new int[SkierFigureJob.Groups];
        readonly MaterialPropertyBlock[] _props = new MaterialPropertyBlock[SkierFigureJob.Groups];
        readonly GraphicsBuffer[] _bones = new GraphicsBuffer[SkierFigureJob.Kinds];
        readonly GraphicsBuffer[] _outfits = new GraphicsBuffer[SkierFigureJob.Kinds];
        readonly Plane[] _planes = new Plane[6];
        readonly int _cap;
        GraphicsBuffer _figures;
        NativeArray<float4> _planeData;
        NativeArray<float> _until;
        NativeArray<int2> _clips;
        NativeArray<uint> _prevSerial;
        NativeArray<float3> _prevPosition;
        NativeArray<float> _odometer;
        NativeArray<byte> _lastLod;
        NativeArray<FigureInstance> _scratch, _sorted;
        NativeArray<byte> _scratchGroup;
        NativeArray<int> _starts;
        NativeArray<float4> _noSeats;
        NativeArray<uint> _noSeatKeys;
        readonly float _hysteresis;
        readonly int _outfitCount;
        float _lodScale = 1f;

        public bool Visible = true;
        /// <summary>The three nearest LODs cast shadows (off: none do).</summary>
        public bool Shadows = true;
        /// <summary>Outfits per figure kind (the keyed outfit is a serial modulo this).</summary>
        public int Outfits => _outfitCount;
        /// <summary>Figures drawn last frame.</summary>
        public int Drawn { get; private set; }
        public bool Ready => _material != null && _figures != null;

        /// <summary>Loads the figures from Resources; null (dots only) when the shader or the baked figures are missing.</summary>
        public static SkierFigures Load(int cap)
        {
            var shader = Resources.Load<Shader>(ShaderResource);
            var asset = Resources.Load<TextAsset>(DataResource);
            if (shader == null || asset == null)
            {
                Debug.LogWarning($"[SkierFigures] no {(shader == null ? "shader" : "baked figures")}: the guests stay dots");
                return null;
            }
            try { return new SkierFigures(SkierFigureData.Read(asset.bytes), shader, cap); }
            catch (Exception e)
            {
                Debug.LogWarning($"[SkierFigures] the baked figures did not load ({e.Message}): the guests stay dots");
                return null;
            }
        }

        public SkierFigures(SkierFigureData data, Shader shader, int cap)
        {
            _cap = Math.Max(1, cap);
            _material = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _hysteresis = data.Hysteresis;
            _until = new NativeArray<float>(data.UntilMetres, Allocator.Persistent);
            _clips = new NativeArray<int2>(SkierFigureJob.Kinds * SkierFigureJob.Slots, Allocator.Persistent);
            string[] kinds = { "skier", "boarder" };
            for (int k = 0; k < SkierFigureJob.Kinds; k++)
            {
                var kind = data.Find(kinds[k]) ?? throw new InvalidOperationException($"no {kinds[k]} in the baked figures");
                if (kind.Lods.Length != SkierFigureJob.Lods) throw new InvalidOperationException($"{kind.Name}: {kind.Lods.Length} LODs");
                _outfitCount = kind.Outfits;
                for (int l = 0; l < SkierFigureJob.Lods; l++)
                {
                    int g = k * SkierFigureJob.Lods + l;
                    _meshes[g] = BuildMesh(kind, l);
                    _influences[g] = l < 2 ? 4 : l < 4 ? 2 : 1;
                }
                _bones[k] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kind.Matrices.Length / 4, 16);
                _bones[k].SetData(kind.Matrices);
                _outfits[k] = new GraphicsBuffer(GraphicsBuffer.Target.Structured, kind.Colours.Length, 4);
                _outfits[k].SetData(kind.Colours);
                for (int s = 0; s < SkierFigureJob.Slots; s++) _clips[k * SkierFigureJob.Slots + s] = Range(kind, k, s);
                for (int l = 0; l < SkierFigureJob.Lods; l++)
                {
                    int g = k * SkierFigureJob.Lods + l;
                    var props = new MaterialPropertyBlock();
                    props.SetBuffer(BonesId, _bones[k]);
                    props.SetInt(BoneCountId, kind.Bones);
                    props.SetBuffer(OutfitsId, _outfits[k]);
                    props.SetInt(InfluencesId, _influences[g]);
                    _props[g] = props;
                }
            }
            _planeData = new NativeArray<float4>(6, Allocator.Persistent);
            _prevSerial = new NativeArray<uint>(_cap, Allocator.Persistent);
            _prevPosition = new NativeArray<float3>(_cap, Allocator.Persistent);
            _odometer = new NativeArray<float>(_cap, Allocator.Persistent);
            _lastLod = new NativeArray<byte>(_cap, Allocator.Persistent);
            // Room for every guest and as many chair riders again.
            _scratch = new NativeArray<FigureInstance>(2 * _cap, Allocator.Persistent);
            _sorted = new NativeArray<FigureInstance>(2 * _cap, Allocator.Persistent);
            _scratchGroup = new NativeArray<byte>(2 * _cap, Allocator.Persistent);
            _starts = new NativeArray<int>(SkierFigureJob.Groups + 1, Allocator.Persistent);
            _noSeats = new NativeArray<float4>(1, Allocator.Persistent);
            _noSeatKeys = new NativeArray<uint>(1, Allocator.Persistent);
            _figures = new GraphicsBuffer(GraphicsBuffer.Target.Structured, 2 * _cap, 32);
            for (int g = 0; g < SkierFigureJob.Groups; g++) _props[g].SetBuffer(FiguresId, _figures);
        }

        static int2 Range(SkierFigureData.Kind kind, int k, int slot)
        {
            string name;
            if (slot < SkierFigureJob.SlotTravel)
            {
                int style = slot / SkierFigureJob.Pitches, pitch = (int)((slot % SkierFigureJob.Pitches) * SkierFigureJob.PitchStep);
                name = k == 0 ? $"turn_{Styles[style]}_{pitch}" : $"turn_board_{pitch}";
            }
            else if (slot == SkierFigureJob.SlotTravel) name = "travel";
            else if (slot == SkierFigureJob.SlotStand) name = "stand";
            else if (slot == SkierFigureJob.SlotIdle2) name = k == 0 ? "lean" : "queue";
            else if (slot == SkierFigureJob.SlotIdle3) name = k == 0 ? "rest" : "queue";
            else name = "seated";
            int c = kind.ClipIndex(name);
            if (c < 0) throw new InvalidOperationException($"{kind.Name} has no clip {name}");
            return new int2(kind.Clips[c].First, kind.Clips[c].Frames);
        }

        /// <summary>The first frame and frame count of a named clip of figure kind 0 (skier) or 1 (snowboarder).</summary>
        public int2 ClipRange(int kind, int slot) => _clips[kind * SkierFigureJob.Slots + slot];

        static Mesh BuildMesh(SkierFigureData.Kind kind, int lod)
        {
            var src = kind.Lods[lod];
            int n = src.Length / SkierFigureData.VertexBytes;
            // The file keeps position, bones, weights, colour; Unity's vertex layout wants position, colour, uv0, uv1.
            var dst = new byte[src.Length];
            for (int v = 0; v < n; v++)
            {
                int o = v * SkierFigureData.VertexBytes;
                Buffer.BlockCopy(src, o, dst, o, 12);
                Buffer.BlockCopy(src, o + 20, dst, o + 12, 4);
                Buffer.BlockCopy(src, o + 12, dst, o + 16, 4);
                Buffer.BlockCopy(src, o + 16, dst, o + 20, 4);
            }
            var mesh = new Mesh { name = $"{kind.Name}_LOD{lod}", hideFlags = HideFlags.HideAndDontSave };
            mesh.SetVertexBufferParams(n,
                new VertexAttributeDescriptor(VertexAttribute.Position, VertexAttributeFormat.Float32, 3),
                new VertexAttributeDescriptor(VertexAttribute.Color, VertexAttributeFormat.UNorm8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord0, VertexAttributeFormat.UInt8, 4),
                new VertexAttributeDescriptor(VertexAttribute.TexCoord1, VertexAttributeFormat.UNorm8, 4));
            mesh.SetVertexBufferData(dst, 0, 0, dst.Length, 0, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            var index = new ushort[n];
            for (int i = 0; i < n; i++) index[i] = (ushort)i;
            mesh.SetIndexBufferParams(n, IndexFormat.UInt16);
            mesh.SetIndexBufferData(index, 0, 0, n, MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.SetSubMesh(0, new SubMeshDescriptor(0, n), MeshUpdateFlags.DontRecalculateBounds | MeshUpdateFlags.DontValidateIndices);
            mesh.bounds = new Bounds(new Vector3(0, 1, 0), new Vector3(4, 4, 4));
            mesh.UploadMeshData(true);
            return mesh;
        }

        /// <summary>
        /// How far the camera's eye a guest becomes a dot: the last LOD's distance, which budgets.json gives for a 60° view
        /// at 1080p, turned into this camera's metres (a narrower view or a taller screen keeps figures farther out).
        /// </summary>
        public float DotCutoff(Camera camera) => _until[_until.Length - 1] / LodScaleOf(camera);

        static float LodScaleOf(Camera camera)
        {
            if (camera == null) return 1f;
            float t = Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Tan(30f * Mathf.Deg2Rad);
            return t * 1080f / Mathf.Max(1, camera.pixelHeight);
        }

        /// <summary>Once a frame, after <see cref="SkierLayer.Place"/>: every guest's figure, sorted for the draws.</summary>
        public void Place(SkierLayer layer, Camera camera, float realSeconds, SkierChairRiders riders = null)
        {
            Drawn = 0;
            if (!Ready || layer == null || camera == null || !layer.Ready) { ClearStarts(); return; }
            GeometryUtility.CalculateFrustumPlanes(camera, _planes);
            for (int i = 0; i < 6; i++) _planeData[i] = new float4(_planes[i].normal, _planes[i].distance);
            _lodScale = LodScaleOf(camera);
            var job = new SkierFigureJob
            {
                Records = layer.Records, Dots = layer.Dots, Facing = layer.Facing, Count = Math.Min(layer.Count, _cap),
                CameraPosition = camera.transform.position, Planes = _planeData, LodScale = _lodScale, Until = _until,
                Hysteresis = _hysteresis, RealSeconds = realSeconds, Outfits = _outfitCount, Clips = _clips,
                PrevSerial = _prevSerial, PrevPosition = _prevPosition, Odometer = _odometer, LastLod = _lastLod,
                Scratch = _scratch, ScratchGroup = _scratchGroup, Sorted = _sorted, Starts = _starts,
                Seated = riders != null ? riders.Seated : _noSeats, SeatedKey = riders != null ? riders.SeatedKey : _noSeatKeys,
                SeatedCount = riders != null ? riders.Count : 0,
            };
            job.Run();
            Drawn = _starts[SkierFigureJob.Groups];
            if (Drawn > 0) _figures.SetData(_sorted, 0, 0, Drawn);
        }

        void ClearStarts()
        {
            if (_starts.IsCreated) for (int g = 0; g <= SkierFigureJob.Groups; g++) _starts[g] = 0;
        }

        public void Draw(Camera camera)
        {
            if (!Visible || !Ready || camera == null || Drawn == 0) return;
            for (int g = 0; g < SkierFigureJob.Groups; g++)
            {
                int start = _starts[g], count = _starts[g + 1] - start;
                if (count <= 0) continue;
                _props[g].SetInt(BaseId, start);
                int lod = g % SkierFigureJob.Lods;
                var rp = new RenderParams(_material)
                {
                    camera = camera,
                    worldBounds = new Bounds(Vector3.zero, Vector3.one * 100_000f),
                    matProps = _props[g],
                    shadowCastingMode = Shadows && lod < ShadowLods ? ShadowCastingMode.On : ShadowCastingMode.Off,
                    receiveShadows = true,
                };
                Graphics.RenderMeshPrimitives(rp, _meshes[g], 0, count);
            }
        }

        public void Dispose()
        {
            foreach (var m in _meshes)
            {
                if (m == null) continue;
                if (Application.isPlaying) UnityEngine.Object.Destroy(m);
                else UnityEngine.Object.DestroyImmediate(m);   // EditMode tests
            }
            foreach (var b in _bones) b?.Release();
            foreach (var b in _outfits) b?.Release();
            _figures?.Release(); _figures = null;
            if (_planeData.IsCreated) _planeData.Dispose();
            if (_until.IsCreated) _until.Dispose();
            if (_clips.IsCreated) _clips.Dispose();
            if (_prevSerial.IsCreated) _prevSerial.Dispose();
            if (_prevPosition.IsCreated) _prevPosition.Dispose();
            if (_odometer.IsCreated) _odometer.Dispose();
            if (_lastLod.IsCreated) _lastLod.Dispose();
            if (_scratch.IsCreated) _scratch.Dispose();
            if (_sorted.IsCreated) _sorted.Dispose();
            if (_scratchGroup.IsCreated) _scratchGroup.Dispose();
            if (_starts.IsCreated) _starts.Dispose();
            if (_noSeats.IsCreated) _noSeats.Dispose();
            if (_noSeatKeys.IsCreated) _noSeatKeys.Dispose();
            if (_material != null && Application.isPlaying) UnityEngine.Object.Destroy(_material);
            else if (_material != null) UnityEngine.Object.DestroyImmediate(_material);
        }
    }
}
