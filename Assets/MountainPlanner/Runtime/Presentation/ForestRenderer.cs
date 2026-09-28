using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation
{
    /// <summary>
    /// Our own GPU-instanced forest (0.3 §4.5, TR2). All trees live in one GPU buffer; each frame a
    /// compute shader (ForestCull.compute) culls them against the camera and picks each tree's LOD by
    /// its height on screen, then a few hundred indirect draws (prototype × LOD × submesh) render every
    /// visible tree with the tree shader (TreeInstanced.shader). The CPU cost doesn't grow with the
    /// number of trees, and nothing is allocated per frame.
    /// </summary>
    public sealed class ForestRenderer : IDisposable
    {
        public const int Lods = 4;
        /// <summary>
        /// Screen-height fractions where LOD0→1, 1→2, 2→card and card→culled. The full models stay on well
        /// into the middle distance, where the sparser LODs made forests look thin (owner review).
        /// </summary>
        public static readonly Vector4 Transitions = new Vector4(0.15f, 0.06f, 0.025f, 0.003f);

        /// <summary>
        /// Crown width per LOD: simpler LODs and cards are drawn a little wider, so a distant stand closes
        /// up the way a real canopy does instead of showing every gap.
        /// </summary>
        public static readonly float[] LodWidth = { 1f, 1.1f, 1.25f, 1.4f };

        /// <summary>
        /// Snow on branches per LOD: full up close, less in the distance, so forests keep their dark
        /// green against the snowfield and read as lush rather than frosted.
        /// </summary>
        public static readonly float[] LodSnow = { 1f, 0.6f, 0.35f, 0.25f };

        readonly ComputeShader _cull;
        readonly int _clear, _cullKernel, _writeArgs;
        readonly GraphicsBuffer _trees, _nativeHeights, _prototypeStart, _visible, _counts, _args, _drawCounter;
        readonly List<Draw> _draws = new List<Draw>();
        readonly Vector4[] _planes = new Vector4[6];
        readonly Plane[] _planeScratch = new Plane[6];
        readonly List<Material> _materials = new List<Material>();
        readonly List<int> _materialLods = new List<int>();
        readonly int _treeCount, _countSlots;
        public int TreeCount => _treeCount;
        public int DrawCount => _draws.Count;
        /// <summary>Snow on the branches, 0 (bare) to 1 (fresh snowfall); iteration 1 opens at 1.</summary>
        public float SnowLoad { get; private set; } = 1;

        /// <summary>Sets the snow load on every tree (the tree shader's TR4 input; later driven by weather).</summary>
        public void SetSnowLoad(float load)
        {
            SnowLoad = Mathf.Clamp01(load);
            for (int i = 0; i < _materials.Count; i++)
            {
                int l = _materialLods[i];
                _materials[i].SetFloat("_SnowLoad", SnowLoad * _lodSnow[l]);
                if (l == Lods - 1) _materials[i].SetFloat("_SnowFlat", 0.2f * SnowLoad);
            }
        }

        struct Draw
        {
            public Mesh Mesh;
            public RenderParams Params;
        }

        readonly float[] _lodSnow;

        /// <summary>Per-LOD tuning for things that aren't trees (rocks keep their size and snow at every distance).</summary>
        public static readonly float[] RockLodWidth = { 1f, 1f, 1f, 1.05f }, RockLodSnow = { 1f, 1f, 1f, 1f };

        public ForestRenderer(TreePrototypeSet set, ForestInstance[] trees, ComputeShader cull, Shader shader, float snowLoad = 1,
                              float[] lodWidth = null, float[] lodSnow = null)
        {
            lodWidth ??= LodWidth;
            _lodSnow = lodSnow ?? LodSnow;
            _cull = cull;
            _clear = cull.FindKernel("Clear");
            _cullKernel = cull.FindKernel("Cull");
            _writeArgs = cull.FindKernel("WriteArgs");
            int prototypes = set.Prefabs.Length;
            _treeCount = trees.Length;
            _countSlots = prototypes * Lods;

            var perPrototype = new int[prototypes];
            foreach (var t in trees) perPrototype[t.Prototype]++;
            var start = new uint[prototypes];
            for (int p = 1; p < prototypes; p++) start[p] = start[p - 1] + (uint)perPrototype[p - 1];

            _trees = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, trees.Length), Marshal.SizeOf<ForestInstance>());
            if (trees.Length > 0) _trees.SetData(trees);
            _nativeHeights = new GraphicsBuffer(GraphicsBuffer.Target.Structured, prototypes, sizeof(float));
            _nativeHeights.SetData(set.NativeHeights);
            _prototypeStart = new GraphicsBuffer(GraphicsBuffer.Target.Structured, prototypes, sizeof(uint));
            _prototypeStart.SetData(start);
            _visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, trees.Length * Lods), sizeof(uint));
            _counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _countSlots, sizeof(uint));

            // One indirect draw per prototype, LOD and visible submesh.
            var args = new List<GraphicsBuffer.IndirectDrawIndexedArgs>();
            var counters = new List<uint>();
            var bounds = new Bounds(Vector3.zero, new Vector3(40000, 20000, 40000));
            for (int p = 0; p < prototypes; p++)
            {
                var group = set.Prefabs[p].GetComponent<LODGroup>();
                var lods = group.GetLODs();
                for (int l = 0; l < Lods && l < lods.Length; l++)
                {
                    var renderer = lods[l].renderers[0];
                    var mesh = renderer.GetComponent<MeshFilter>().sharedMesh;
                    var materials = renderer.sharedMaterials;
                    for (int s = 0; s < mesh.subMeshCount && s < materials.Length; s++)
                    {
                        var source = materials[s];
                        if (source == null || source.name.StartsWith("Hidden")) continue;     // winter: no summer leaves
                        var material = new Material(shader) { name = source.name + " (forest)" };
                        material.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                        bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON");
                        material.SetFloat("_Cutoff", cutout ? 0.4f : 0);   // a little lower than 0.5: soft edges would otherwise thin the crowns
                        material.SetFloat("_SnowLoad", snowLoad * _lodSnow[l]);
                        if (source.HasProperty("_Triplanar"))
                        {
                            material.SetFloat("_Triplanar", source.GetFloat("_Triplanar"));
                            material.SetFloat("_TriplanarScale", source.GetFloat("_TriplanarScale"));
                        }
                        // Cards have no snow mask: a light flat dusting on their upper side.
                        if (l == Lods - 1 && lodSnow == null) material.SetFloat("_SnowFlat", 0.2f * snowLoad);
                        _materials.Add(material);
                        _materialLods.Add(l);
                        var props = new MaterialPropertyBlock();
                        props.SetInt("_VisibleOffset", (int)(l * trees.Length + start[p]));
                        props.SetFloat("_LodWidth", lodWidth[l]);
                        props.SetBuffer("_Trees", _trees);
                        props.SetBuffer("_Visible", _visible);
                        _draws.Add(new Draw
                        {
                            Mesh = mesh,
                            Params = new RenderParams(material)
                            {
                                worldBounds = bounds, matProps = props, receiveShadows = true,
                                shadowCastingMode = l <= 1 ? ShadowCastingMode.On : ShadowCastingMode.Off,
                            },
                        });
                        args.Add(new GraphicsBuffer.IndirectDrawIndexedArgs
                        {
                            indexCountPerInstance = mesh.GetIndexCount(s), startIndex = mesh.GetIndexStart(s),
                            baseVertexIndex = mesh.GetBaseVertex(s), instanceCount = 0, startInstance = 0,
                        });
                        counters.Add((uint)(p * Lods + l));
                    }
                }
            }
            _args = new GraphicsBuffer(GraphicsBuffer.Target.IndirectArguments | GraphicsBuffer.Target.Structured, Mathf.Max(1, args.Count),
                                       GraphicsBuffer.IndirectDrawIndexedArgs.size);
            _args.SetData(args);
            _drawCounter = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, counters.Count), sizeof(uint));
            _drawCounter.SetData(counters);

            _cull.SetBuffer(_clear, "_Counts", _counts);
            _cull.SetBuffer(_cullKernel, "_Trees", _trees);
            _cull.SetBuffer(_cullKernel, "_NativeHeights", _nativeHeights);
            _cull.SetBuffer(_cullKernel, "_PrototypeStart", _prototypeStart);
            _cull.SetBuffer(_cullKernel, "_Visible", _visible);
            _cull.SetBuffer(_cullKernel, "_Counts", _counts);
            _cull.SetBuffer(_writeArgs, "_Counts", _counts);
            _cull.SetBuffer(_writeArgs, "_Args", _args);
            _cull.SetBuffer(_writeArgs, "_DrawCounter", _drawCounter);
        }

        /// <summary>Culls and draws the forest for a camera. Call once per frame before rendering.</summary>
        public void Render(Camera camera)
        {
            if (_treeCount == 0 || camera == null) return;
            GeometryUtility.CalculateFrustumPlanes(camera, _planeScratch);
            for (int i = 0; i < 6; i++) _planes[i] = new Vector4(_planeScratch[i].normal.x, _planeScratch[i].normal.y, _planeScratch[i].normal.z, _planeScratch[i].distance);
            _cull.SetInt("_TreeCount", _treeCount);
            _cull.SetInt("_CountSlots", _countSlots);
            _cull.SetInt("_DrawCount", _draws.Count);
            _cull.SetVectorArray("_Planes", _planes);
            _cull.SetVector("_CameraPosition", camera.transform.position);
            // Same measure as Unity's LODGroup: screen height fraction times the quality LOD bias.
            _cull.SetFloat("_ScreenScale", QualitySettings.lodBias / (2f * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad)));
            _cull.SetVector("_Transitions", Transitions);
            _cull.Dispatch(_clear, (_countSlots + 63) / 64, 1, 1);
            _cull.Dispatch(_cullKernel, (_treeCount + 63) / 64, 1, 1);
            _cull.Dispatch(_writeArgs, (_draws.Count + 63) / 64, 1, 1);
            for (int d = 0; d < _draws.Count; d++)
                Graphics.RenderMeshIndirect(_draws[d].Params, _draws[d].Mesh, _args, 1, d);
        }

        public void Dispose()
        {
            foreach (var b in new[] { _trees, _nativeHeights, _prototypeStart, _visible, _counts, _args, _drawCounter }) b?.Dispose();
            foreach (var m in _materials) UnityEngine.Object.Destroy(m);
            _materials.Clear();
        }
    }

    /// <summary>Draws a <see cref="ForestRenderer"/> every frame for the main camera and frees it with the resort.</summary>
    public sealed class ForestView : MonoBehaviour
    {
        public ForestRenderer Renderer;
        public Camera Camera;

        void LateUpdate() => Renderer?.Render(Camera != null ? Camera : Camera.main);

        void OnDestroy()
        {
            Renderer?.Dispose();
            Renderer = null;
        }
    }
}
