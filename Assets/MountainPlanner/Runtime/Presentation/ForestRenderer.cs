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
    /// visible tree: mesh LODs with TreeInstanced.shader, far trees as impostors (TreeImpostor.shader,
    /// one quad each). The CPU cost doesn't grow with the number of trees, and nothing is allocated per
    /// frame.
    /// </summary>
    public sealed class ForestRenderer : IDisposable
    {
        public const int Lods = 4;
        /// <summary>
        /// Screen-height fractions where LOD0→1, 1→2, 2→impostor and impostor→culled. For a 20 m tree at the
        /// PC preset's LOD bias of 2: LOD0 to about 140 m, LOD1 to 350 m, LOD2 to 700 m, the impostor beyond.
        /// The LODs keep LOD0's crown, brightness and snow (TreeImport's fidelity check), so the full model no
        /// longer has to stay on far into the distance.
        /// </summary>
        public static readonly Vector4 Transitions = new Vector4(0.25f, 0.10f, 0.05f, 0.003f);

        /// <summary>
        /// Distance fade, continuous so nothing pops at a LOD switch (TreeCommon.hlsl): between x and y metres,
        /// branches keep z of their snow (forests keep their dark green against the snowfield) and crowns
        /// grow by w (a far canopy closes up rather than showing every gap).
        /// </summary>
        public static readonly Vector4 Fade = new Vector4(150f, 1600f, 0.35f, 0.08f);

        readonly ComputeShader _cull;
        readonly int _clear, _cullKernel, _writeArgs;
        readonly GraphicsBuffer _trees, _nativeHeights, _reach, _prototypeStart, _visible, _counts, _args, _drawCounter;
        readonly List<Draw> _draws = new List<Draw>();
        readonly Vector4[] _planes = new Vector4[6];
        readonly Plane[] _planeScratch = new Plane[6];
        readonly List<Material> _materials = new List<Material>();
        readonly int _treeCount, _countSlots;
        public int TreeCount => _treeCount;
        public int DrawCount => _draws.Count;
        /// <summary>GPU memory of the forest's own buffers (trees, visible lists, counters, draw arguments).</summary>
        public long GpuBytes
        {
            get
            {
                long total = 0;
                foreach (var b in new[] { _trees, _nativeHeights, _reach, _prototypeStart, _visible, _counts, _args, _drawCounter })
                    if (b != null) total += (long)b.count * b.stride;
                return total;
            }
        }
        /// <summary>-1: LOD by screen size. 0–3: draw every visible tree at that LOD (lineups and reviews).</summary>
        public int ForcedLod = -1;
        readonly double[] _slotTriangles;
        /// <summary>Snow on the branches, 0 (bare) to 1 (fresh snowfall); iteration 1 opens at 1.</summary>
        public float SnowLoad { get; private set; } = 1;

        /// <summary>Sets the snow load on every tree (the tree shader's TR4 input; later driven by weather).</summary>
        public void SetSnowLoad(float load)
        {
            SnowLoad = Mathf.Clamp01(load);
            foreach (var m in _materials) m.SetFloat("_SnowLoad", SnowLoad);
        }

        /// <summary>The wind in the trees (TR4), advanced and handed to the tree shader once a frame by <see cref="Render"/>.</summary>
        public ForestWind Wind { get; } = new ForestWind();
        /// <summary>
        /// LODs that sway. Farther LODs move under a pixel, and the wind code slows every vertex it's compiled
        /// into even where it's skipped (TreeInstanced.shader), so they're drawn without it.
        /// </summary>
        public const int WindLods = 2;
        readonly List<Material> _windMaterials = new List<Material>();
        int _windFrame = -1;
        bool _windOn;

        struct Draw
        {
            public Mesh Mesh;
            public RenderParams Params;
        }

        public ForestRenderer(TreePrototypeSet set, ForestInstance[] trees, ComputeShader cull, Shader shader, Shader impostorShader, float snowLoad = 1)
        {
            SnowLoad = snowLoad;
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
            // How far each prototype reaches sideways from its trunk (LOD0 bounds), so wide, low models (krummholz
            // mats) are culled and switch LOD by their real size, not just their height.
            var reach = new float[prototypes];
            for (int p = 0; p < prototypes; p++)
            {
                var lods = set.Prefabs[p].GetComponent<LODGroup>().GetLODs();
                var b = lods[0].renderers[0].GetComponent<MeshFilter>().sharedMesh.bounds;
                reach[p] = Mathf.Max(new Vector2(b.min.x, b.min.z).magnitude, new Vector2(b.max.x, b.max.z).magnitude,
                                     new Vector2(b.min.x, b.max.z).magnitude, new Vector2(b.max.x, b.min.z).magnitude);
            }
            _reach = new GraphicsBuffer(GraphicsBuffer.Target.Structured, prototypes, sizeof(float));
            _reach.SetData(reach);
            _prototypeStart = new GraphicsBuffer(GraphicsBuffer.Target.Structured, prototypes, sizeof(uint));
            _prototypeStart.SetData(start);
            _visible = new GraphicsBuffer(GraphicsBuffer.Target.Structured, Mathf.Max(1, trees.Length * Lods), sizeof(uint));
            _counts = new GraphicsBuffer(GraphicsBuffer.Target.Structured, _countSlots, sizeof(uint));

            // One indirect draw per prototype, LOD and visible submesh, for the prototypes this forest has: a
            // site uses a few of the library's species, and every draw costs CPU in each pass even when empty.
            var args = new List<GraphicsBuffer.IndirectDrawIndexedArgs>();
            var counters = new List<uint>();
            var bounds = new Bounds(Vector3.zero, new Vector3(40000, 20000, 40000));
            for (int p = 0; p < prototypes; p++)
            {
                if (perPrototype[p] == 0) continue;
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
                        Material material;
                        if (source.HasProperty("_ImpAlbedo"))
                        {
                            // The impostor: one camera-facing quad, lit like the mesh LODs.
                            material = new Material(impostorShader) { name = source.name + " (forest)" };
                            foreach (string t in new[] { "_ImpAlbedo", "_ImpData" }) material.SetTexture(t, source.GetTexture(t));
                            foreach (string f in new[] { "_ImpFrames", "_ImpFrameSize" }) material.SetFloat(f, source.GetFloat(f));
                            foreach (string v in new[] { "_ImpCenter", "_ImpSize" }) material.SetVector(v, source.GetVector(v));
                        }
                        else
                        {
                            material = new Material(shader) { name = source.name + " (forest)" };
                            material.SetTexture("_BaseMap", source.GetTexture("_BaseMap"));
                            bool cutout = source.IsKeywordEnabled("_ALPHATEST_ON");
                            material.SetFloat("_Cutoff", cutout ? 0.4f : 0);   // a little lower than 0.5: soft edges would otherwise thin the crowns
                            material.SetFloat("_Foliage", cutout ? 1 : 0);
                            // Bark relief (the shader builds the tangent frame itself).
                            var bump = !cutout && source.HasProperty("_BumpMap") ? source.GetTexture("_BumpMap") : null;
                            if (bump != null)
                            {
                                material.SetTexture("_BumpMap", bump);
                                material.SetFloat("_BumpScale", 1f);
                            }
                            if (l < WindLods) _windMaterials.Add(material);
                        }
                        material.SetFloat("_SnowLoad", snowLoad);
                        material.SetVector("_TreeFade", Fade);
                        material.SetFloat("_Brightness", set.Brightness(p, l));
                        material.SetFloat("_SnowScale", set.Snow(p, l));
                        _materials.Add(material);
                        var props = new MaterialPropertyBlock();
                        props.SetInt("_VisibleOffset", (int)(l * trees.Length + start[p]));
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
            _slotTriangles = new double[_countSlots];
            for (int d = 0; d < args.Count; d++) _slotTriangles[counters[d]] += args[d].indexCountPerInstance / 3.0;

            _cull.SetBuffer(_clear, "_Counts", _counts);
            _cull.SetBuffer(_cullKernel, "_Trees", _trees);
            _cull.SetBuffer(_cullKernel, "_NativeHeights", _nativeHeights);
            _cull.SetBuffer(_cullKernel, "_Reach", _reach);
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
            if (_windFrame != Time.frameCount)
            {
                _windFrame = Time.frameCount;
                Wind.Advance(Time.deltaTime);
                Shader.SetGlobalVector(ForestWind.WindId, Wind.ShaderValue);
                if (_windOn != Wind.Strength > 0)
                {
                    _windOn = Wind.Strength > 0;   // calm: no wind code at all
                    foreach (var m in _windMaterials)
                        if (_windOn) m.EnableKeyword("_WIND"); else m.DisableKeyword("_WIND");
                }
            }
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
            _cull.SetInt("_ForcedLod", ForcedLod);
            _cull.Dispatch(_clear, (_countSlots + 63) / 64, 1, 1);
            _cull.Dispatch(_cullKernel, (_treeCount + 63) / 64, 1, 1);
            _cull.Dispatch(_writeArgs, (_draws.Count + 63) / 64, 1, 1);
            for (int d = 0; d < _draws.Count; d++)
                Graphics.RenderMeshIndirect(_draws[d].Params, _draws[d].Mesh, _args, 1, d);
        }

        /// <summary>
        /// Reads back the last frame's visible trees per LOD and the triangles they drew (benchmarks). The
        /// callback runs a frame or two later, on the main thread.
        /// </summary>
        public void RequestLodCounts(Action<int[], double> done)
        {
            AsyncGPUReadback.Request(_counts, r =>
            {
                if (r.hasError) { done(new int[Lods], 0); return; }
                var data = r.GetData<uint>();
                var perLod = new int[Lods];
                double triangles = 0;
                for (int slot = 0; slot < data.Length; slot++)
                {
                    perLod[slot % Lods] += (int)data[slot];
                    triangles += data[slot] * _slotTriangles[slot];
                }
                done(perLod, triangles);
            });
        }

        public void Dispose()
        {
            foreach (var b in new[] { _trees, _nativeHeights, _reach, _prototypeStart, _visible, _counts, _args, _drawCounter }) b?.Dispose();
            foreach (var m in _materials) UnityEngine.Object.Destroy(m);
            _materials.Clear();
            _windMaterials.Clear();
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
