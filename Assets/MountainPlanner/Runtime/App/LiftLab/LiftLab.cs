using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Lifts;
using MountainPlanner.World.Lifts;
using UnityEngine;
using UnityEngine.InputSystem;

namespace MountainPlanner.App
{
    /// <summary>
    /// The Lift Lab (decision LP1): a standalone scene for reviewing the lift assets hands-on and measuring
    /// what they cost, away from the mountain viewer.
    ///   1 drive terminal · 2 return terminal · 3 chair · 4 line-up · 5 stress (20 lifts with towers, ~500 chairs)
    ///   6 towers (every tower head type on a full tower)
    ///   Tab next asset · L LOD auto/0/1/2/3 · N snow · C livery colour · T turntable · G ground on/off
    ///   B benchmark · P screenshot · H help · Esc quit (camera: DebugFlyCamera's keys)
    /// Unattended: -mode drive|return|chair|lineup|stress|empty|towers, -lod n, -snow 0..1, -view yaw,pitch,distance,
    /// -screenshot file.png (captures once settled, then quits), -benchmark file.json (runs, writes, quits).
    /// </summary>
    public sealed class LiftLab : MonoBehaviour
    {
        public LiftModelSet Models;
        /// <summary>The imported lift materials (the prefabs' shared materials), copied at runtime per livery.</summary>
        public Material Structure;
        public Material Glass;
        public Material Chair;
        public Material RopeMaterial;
        public Material GroundMaterial;
        public DebugFlyCamera Camera;
        public LiftLabOverlay Overlay;

        public const float TerminalSpacing = 175f;   // along the line, stress layout
        public const float LiftSpacing = 25f;        // side by side, stress layout
        public const float ChairSpacing = 13.8f;     // 2.3 m/s x 3600 s x 4 seats / 2400 pph (roadmap 10)

        static readonly Color[] Liveries =
        {
            new Color(0.72f, 0.12f, 0.09f), new Color(0.10f, 0.26f, 0.62f), new Color(0.11f, 0.42f, 0.22f), new Color(0.93f, 0.70f, 0.10f),
            new Color(0.12f, 0.12f, 0.13f), new Color(0.90f, 0.90f, 0.88f), new Color(0.90f, 0.42f, 0.08f), new Color(0.05f, 0.47f, 0.50f),
        };
        static readonly string[] ModeNames = { "drive", "return", "chair", "lineup", "stress", "empty", "towers" };
        /// <summary>The tower head types (LP11), in the line-up's order.</summary>
        static readonly string[] TowerHeads = { "tower_s4", "tower_s6", "tower_b8", "tower_d8", "tower_c8" };
        const float TowerRope = 9.5f;                // rope height of the towers in the towers mode

        enum Mode { Drive, Return, Chair, Lineup, Stress, Empty, Towers }

        readonly List<GameObject> _spawned = new List<GameObject>();
        readonly Dictionary<GameObject, int[]> _lodTriangles = new Dictionary<GameObject, int[]>();
        LiftMaterials _materials;
        Mode _mode = Mode.Drive;
        GameObject _focus;
        int _forcedLod = -1;
        float _snow = 1f;
        int _livery;
        bool _turntable;
        GameObject _ground;
        FrameStats _stats;
        bool _benchmarking;
        float _nextOverlay;
        readonly float[] _recent = new float[240];
        int _recentCount;
        readonly StringBuilder _text = new StringBuilder(512);

        public string OverlayText { get; private set; } = "";
        public bool Help { get; private set; } = true;

        void Start()
        {
            Application.targetFrameRate = -1;
            DynamicGI.UpdateEnvironment();   // ambient light from the sky: the scene is built in code, nothing is baked
            SkyReflection();
            _materials = new LiftMaterials(Structure, Glass, Chair);
            _stats = new FrameStats();
            _ground = MakeGround();
            string[] args = Environment.GetCommandLineArgs();
            string mode = Arg(args, "-mode");
            if (mode != null && Array.IndexOf(ModeNames, mode) >= 0) _mode = (Mode)Array.IndexOf(ModeNames, mode);
            if (float.TryParse(Arg(args, "-snow"), NumberStyles.Float, CultureInfo.InvariantCulture, out float snow)) _snow = snow;
            if (int.TryParse(Arg(args, "-lod"), out int lod)) _forcedLod = lod;
            Show(_mode);
            string view = Arg(args, "-view");
            if (view != null && Camera != null)
            {
                var v = view.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                Camera.SetAngles(v[0], v[1]);
                Camera.Frame(Camera.Target, v[2]);
            }
            string bench = Arg(args, "-benchmark");
            if (bench != null) StartCoroutine(Benchmark(bench, true));
            string shot = Arg(args, "-screenshot");
            if (shot != null) StartCoroutine(CaptureAndQuit(shot));
        }

        /// <summary>A sky-only realtime reflection probe, rendered once. Without baked lighting the default
        /// reflection is black, and metallic parts (galvanised steel, the chairs) would render nearly black.</summary>
        static void SkyReflection()
        {
            var probe = new GameObject("SkyReflection").AddComponent<ReflectionProbe>();
            probe.mode = UnityEngine.Rendering.ReflectionProbeMode.Realtime;
            probe.refreshMode = UnityEngine.Rendering.ReflectionProbeRefreshMode.ViaScripting;
            probe.timeSlicingMode = UnityEngine.Rendering.ReflectionProbeTimeSlicingMode.AllFacesAtOnce;
            probe.clearFlags = UnityEngine.Rendering.ReflectionProbeClearFlags.Skybox;
            probe.cullingMask = 0;
            probe.size = new Vector3(20000f, 20000f, 20000f);
            probe.resolution = 128;
            probe.RenderProbe();
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        GameObject MakeGround()
        {
            var g = GameObject.CreatePrimitive(PrimitiveType.Plane);
            g.name = "Snow";
            g.transform.localScale = new Vector3(400f, 1f, 400f);   // 4 km square
            Destroy(g.GetComponent<Collider>());
            var r = g.GetComponent<MeshRenderer>();
            r.sharedMaterial = GroundMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            return g;
        }

        // -- building the scene --------------------------------------------------------------------
        void Clear()
        {
            foreach (var go in _spawned) Destroy(go);
            _spawned.Clear();
            _focus = null;
        }

        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Color livery)
        {
            var go = Instantiate(prefab, position, rotation);
            _materials.Apply(go, livery);
            _spawned.Add(go);
            if (!_lodTriangles.ContainsKey(prefab)) _lodTriangles[prefab] = LodTriangles(prefab);
            var group = go.GetComponent<LODGroup>();
            if (group != null) group.ForceLOD(_forcedLod);
            return go;
        }

        static int[] LodTriangles(GameObject prefab)
        {
            var group = prefab.GetComponent<LODGroup>();
            if (group == null) return Array.Empty<int>();
            return group.GetLODs().Select(l => l.renderers.Where(r => r != null).Sum(r =>
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return 0;
                int t = 0;
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) t += (int)mf.sharedMesh.GetIndexCount(s) / 3;
                return t;
            })).ToArray();
        }

        GameObject Prefab(string part) => Models != null ? Models.Find("sessellift_fgq4_" + part) : null;

        void Show(Mode mode)
        {
            _mode = mode;
            Clear();
            var livery = Liveries[_livery % Liveries.Length];
            var rope = LiftRope();
            switch (mode)
            {
                case Mode.Drive:
                case Mode.Return:
                case Mode.Chair:
                {
                    string part = mode == Mode.Drive ? "drive" : mode == Mode.Return ? "return" : "chair";
                    var prefab = Prefab(part);
                    if (prefab == null) break;
                    float lift = mode == Mode.Chair ? rope : 0f;
                    _focus = Spawn(prefab, new Vector3(0, lift, 0), Quaternion.identity, livery);
                    FrameOn(_focus, mode == Mode.Chair ? 7f : 26f, mode == Mode.Chair ? 205f : 215f, 16f);
                    break;
                }
                case Mode.Lineup:
                {
                    var drive = Prefab("drive");
                    var ret = Prefab("return");
                    var chair = Prefab("chair");
                    if (ret != null) Spawn(ret, new Vector3(-9f, 0, 0), Quaternion.identity, livery);
                    if (drive != null) _focus = Spawn(drive, new Vector3(9f, 0, 0), Quaternion.identity, livery);
                    if (chair != null) Spawn(chair, new Vector3(0, rope, 10f), Quaternion.identity, livery);
                    if (_focus != null) FrameOn(_focus, 30f, 200f, 18f);
                    break;
                }
                case Mode.Stress:
                    BuildStress();
                    break;
                case Mode.Towers:
                {
                    var chair = Prefab("chair");
                    for (int i = 0; i < TowerHeads.Length; i++)
                    {
                        var head = SpawnTower(TowerHeads[i], new Vector3((i - (TowerHeads.Length - 1) / 2f) * 9f, 0, 0), Quaternion.identity, TowerRope, livery);
                        if (head == null) continue;
                        if (TowerHeads[i] == "tower_b8") _focus = head;
                        var rig = head.GetComponent<LiftRig>();
                        foreach (string side in new[] { "left", "right" })   // a short rope through each head, a chair on the right
                        {
                            var s = rig.Socket("rope_" + side).position;
                            AddRope(s + Vector3.back * 16f, s + Vector3.forward * 16f, side == "right" ? chair : null, livery, true);
                        }
                    }
                    if (_focus != null) FrameOn(_focus, 34f, 205f, 12f);
                    break;
                }
                case Mode.Empty:
                    if (Camera != null) { Camera.Frame(new Vector3(0, 2, TerminalSpacing / 2), 120f); Camera.SetAngles(200f, 20f); }
                    break;
            }
            _materials.SetSnowLoad(_snow);
        }

        /// <summary>A line tower of the given head type, its rope at ropeHeight above position: the base, as many 1 m mast
        /// sections as fit, and the head; the base rides up or down by the remainder (under half a metre), as a
        /// footing would be set. Returns the head.</summary>
        GameObject SpawnTower(string headId, Vector3 position, Quaternion rotation, float ropeHeight, Color livery)
        {
            var head = Prefab(headId);
            var mast = Prefab("tower_mast");
            var foundation = Prefab("tower_base");
            if (head == null || mast == null || foundation == null) return null;
            float foot = foundation.GetComponent<LiftRig>().Socket("mast_foot").localPosition.y;
            float ropeOnHead = head.GetComponent<LiftRig>().Socket("rope_right").localPosition.y;
            int sections = Mathf.Max(1, Mathf.RoundToInt(ropeHeight - foot - ropeOnHead));
            float lift = ropeHeight - (foot + sections + ropeOnHead);
            Spawn(foundation, position + rotation * new Vector3(0, lift, 0), rotation, livery);
            for (int i = 0; i < sections; i++)
                Spawn(mast, position + rotation * new Vector3(0, lift + foot + i, 0), rotation, livery);
            return Spawn(head, position + rotation * new Vector3(0, lift + foot + sections, 0), rotation, livery);
        }

        float LiftRope()
        {
            var ret = Prefab("return");
            var rig = ret != null ? ret.GetComponent<LiftRig>() : null;
            var s = rig != null ? rig.Socket("rope_right_bw") : null;
            return s != null ? s.localPosition.y : 3.039f;
        }

        /// <summary>20 lifts side by side, each a return and a drive terminal facing each other 175 m apart, two line
        /// towers between them (the head types in turn) and ropes with chairs every 13.8 m on both sides: about
        /// 500 chairs and 40 towers.</summary>
        void BuildStress()
        {
            var drive = Prefab("drive");
            var ret = Prefab("return");
            var chair = Prefab("chair");
            if (drive == null || ret == null) return;
            const int lifts = 20;
            for (int i = 0; i < lifts; i++)
            {
                float x = (i - (lifts - 1) / 2f) * LiftSpacing;
                var livery = Liveries[i % Liveries.Length];
                var r = Spawn(ret, new Vector3(x, 0, 0), Quaternion.identity, livery);
                var d = Spawn(drive, new Vector3(x, 0, TerminalSpacing), Quaternion.Euler(0, 180f, 0), livery);
                if (i == lifts / 2) _focus = r;
                var rr = r.GetComponent<LiftRig>();
                var dr = d.GetComponent<LiftRig>();
                // towers face the return (their platform side, +Z, points downhill) and carry the rope at its height
                float rope = rr.Socket("rope_right_out").position.y;
                for (int k = 1; k <= 2; k++)
                    SpawnTower(TowerHeads[(i * 2 + k) % TowerHeads.Length], new Vector3(x, 0, TerminalSpacing * k / 3f), Quaternion.Euler(0, 180f, 0), rope, livery);
                // the return's right rope meets the drive's left rope, and vice versa
                AddRope(rr.Socket("rope_right_out").position, dr.Socket("rope_left_out").position, chair, livery, true);
                AddRope(rr.Socket("rope_left_out").position, dr.Socket("rope_right_out").position, chair, livery, false);
            }
            if (Camera != null) { Camera.Frame(new Vector3(0, 3, 20f), 140f); Camera.SetAngles(205f, 22f); }
        }

        void AddRope(Vector3 from, Vector3 to, GameObject chair, Color livery, bool uphill)
        {
            var go = new GameObject("Rope");
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = RopeMaterial;
            line.widthMultiplier = 0.045f;
            line.positionCount = 2;
            line.SetPosition(0, from);
            line.SetPosition(1, to);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _spawned.Add(go);
            if (chair == null) return;
            var dir = (to - from).normalized;
            float length = Vector3.Distance(from, to);
            // chairs face the way they travel: uphill on the right rope, down on the left; outboard is +X in both
            var rotation = Quaternion.LookRotation(uphill ? dir : -dir, Vector3.up);
            for (float t = ChairSpacing * 0.5f; t < length; t += ChairSpacing)
                Spawn(chair, from + dir * t, rotation, livery);
        }

        void FrameOn(GameObject go, float distance, float yaw, float pitch)
        {
            if (Camera == null) return;
            var b = Bounds(go);
            Camera.MinDistance = 1.5f;
            Camera.Frame(b.center, distance);
            Camera.SetAngles(yaw, pitch);
        }

        static Bounds Bounds(GameObject go)
        {
            var rs = go.GetComponentsInChildren<Renderer>();
            if (rs.Length == 0) return new Bounds(go.transform.position, Vector3.one);
            var b = rs[0].bounds;
            foreach (var r in rs) b.Encapsulate(r.bounds);
            return b;
        }

        // -- input and overlay ---------------------------------------------------------------------
        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _recent[_recentCount++ % _recent.Length] = dt * 1000f;
            if (_benchmarking) { _stats.Record(dt); return; }
            var keys = Keyboard.current;
            if (keys != null)
            {
                if (keys.digit1Key.wasPressedThisFrame) Show(Mode.Drive);
                if (keys.digit2Key.wasPressedThisFrame) Show(Mode.Return);
                if (keys.digit3Key.wasPressedThisFrame) Show(Mode.Chair);
                if (keys.digit4Key.wasPressedThisFrame) Show(Mode.Lineup);
                if (keys.digit5Key.wasPressedThisFrame) Show(Mode.Stress);
                if (keys.digit6Key.wasPressedThisFrame) Show(Mode.Towers);
                if (keys.tabKey.wasPressedThisFrame) Show(_mode < Mode.Chair ? _mode + 1 : Mode.Drive);
                if (keys.lKey.wasPressedThisFrame) CycleLod();
                if (keys.nKey.wasPressedThisFrame) { _snow = _snow > 0.75f ? 0f : _snow + 0.5f; _materials.SetSnowLoad(_snow); }
                if (keys.cKey.wasPressedThisFrame) { _livery++; Show(_mode); }
                if (keys.tKey.wasPressedThisFrame) _turntable = !_turntable;
                if (keys.gKey.wasPressedThisFrame) _ground.SetActive(!_ground.activeSelf);
                if (keys.bKey.wasPressedThisFrame) StartCoroutine(Benchmark(null, false));
                if (keys.pKey.wasPressedThisFrame) ScreenCapture.CaptureScreenshot(Path.Combine(Application.persistentDataPath, $"LiftLab_{DateTime.Now:yyyyMMdd_HHmmss}.png"));
                if (keys.hKey.wasPressedThisFrame) Help = !Help;
                if (keys.escapeKey.wasPressedThisFrame) Application.Quit();
            }
            if (_turntable && Camera != null) Camera.SetAngles(Camera.Yaw + 12f * dt, Camera.Pitch);
            if (Time.unscaledTime >= _nextOverlay)
            {
                _nextOverlay = Time.unscaledTime + 0.25f;
                OverlayText = BuildOverlay();
            }
        }

        void CycleLod()
        {
            _forcedLod = _forcedLod >= 3 ? -1 : _forcedLod + 1;
            foreach (var go in _spawned)
            {
                var group = go.GetComponent<LODGroup>();
                if (group != null) group.ForceLOD(_forcedLod);
            }
        }

        string BuildOverlay()
        {
            int n = Mathf.Min(_recentCount, _recent.Length);
            var window = new float[n];
            Array.Copy(_recent, window, n);
            Array.Sort(window);
            float p50 = n > 0 ? window[n / 2] : 0, p95 = n > 0 ? window[Mathf.Min(n - 1, (int)(n * 0.95f))] : 0;
            _text.Clear();
            _text.Append("Lift Lab · ").Append(Models != null ? Models.ModelName : "no models").Append(" · ").Append(ModeNames[(int)_mode])
                 .Append(" · ").Append(_spawned.Count).Append(" objects · ");
            if (n < 30) _text.Append("measuring frame time…\n");
            else _text.Append(p50.ToString("F1")).Append(" ms (p95 ").Append(p95.ToString("F1")).Append(")\n");
            if (_focus != null && Camera != null)
            {
                var group = _focus.GetComponent<LODGroup>();
                var cam = Camera.GetComponent<UnityEngine.Camera>();
                if (group != null && cam != null)
                {
                    float distance = Vector3.Distance(cam.transform.position, _focus.transform.TransformPoint(group.localReferencePoint));
                    float size = group.size * _focus.transform.lossyScale.x;
                    float tanHalf = Mathf.Tan(cam.fieldOfView * 0.5f * Mathf.Deg2Rad);
                    float height = size / (2f * Mathf.Max(0.01f, distance) * tanHalf) * QualitySettings.lodBias;
                    var lods = group.GetLODs();
                    int active = lods.Length;
                    for (int i = 0; i < lods.Length; i++) if (height >= lods[i].screenRelativeTransitionHeight) { active = i; break; }
                    if (_forcedLod >= 0) active = Mathf.Min(_forcedLod, lods.Length - 1);
                    var prefab = Models.Prefabs.FirstOrDefault(p => p != null && _focus.name.StartsWith(p.name));
                    int[] tris = prefab != null && _lodTriangles.TryGetValue(prefab, out var t) ? t : Array.Empty<int>();
                    _text.Append(_focus.name.Replace("(Clone)", "")).Append(" · LOD ").Append(active < lods.Length ? active.ToString() : "culled")
                         .Append(_forcedLod >= 0 ? " (forced)" : " (auto)");
                    if (active < tris.Length) _text.Append(" · ").Append(tris[active].ToString("N0")).Append(" triangles");
                    _text.Append(" · ").Append(distance.ToString("F0")).Append(" m\nLOD switches at");
                    for (int i = 0; i < lods.Length; i++)
                        _text.Append(' ').Append((size * QualitySettings.lodBias / (2f * tanHalf * lods[i].screenRelativeTransitionHeight)).ToString("F0")).Append(" m");
                    _text.Append(" (lodBias ").Append(QualitySettings.lodBias.ToString("0.#")).Append(")\n");
                }
            }
            _text.Append("Snow ").Append((_snow * 100).ToString("F0")).Append("% · livery ").Append(_livery % Liveries.Length + 1).Append('/').Append(Liveries.Length);
            if (Help)
                _text.Append("\n1 drive · 2 return · 3 chair · 4 line-up · 5 stress · 6 towers · Tab next · L LOD · N snow · C colour · T turntable · G ground · B benchmark · P screenshot · H help · Esc quit")
                     .Append("\nWASD move · Q/E rotate · R/F tilt · wheel zoom · middle-drag rotate · right-drag move · Shift faster");
            return _text.ToString();
        }

        // -- measuring -----------------------------------------------------------------------------
        /// <summary>5 s warm-up, then 30 s along a fixed camera path (orbit, a pass along a line, a far view),
        /// recording every frame; the overlay is off so the measurement allocates nothing per frame.</summary>
        IEnumerator Benchmark(string output, bool quit)
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = -1;
            if (Overlay != null) Overlay.enabled = false;
            var centre = _mode == Mode.Stress ? new Vector3(0, 3, TerminalSpacing * 0.5f) : (_focus != null ? Bounds(_focus).center : Vector3.up * 3);
            float start = Time.unscaledTime;
            while (Time.unscaledTime - start < 5f) { CameraPath(centre, 0f); yield return null; }
            _stats.Reset();
            _benchmarking = true;
            start = Time.unscaledTime;
            while (Time.unscaledTime - start < 30f)
            {
                CameraPath(centre, (Time.unscaledTime - start) / 30f);
                yield return null;
            }
            _benchmarking = false;
            if (Overlay != null) Overlay.enabled = true;
            var summary = _stats.Summarise();
            string json = JsonUtility.ToJson(new BenchmarkResult
            {
                mode = ModeNames[(int)_mode],
                objects = _spawned.Count,
                resolution = $"{Screen.width}x{Screen.height}",
                quality = QualitySettings.names[QualitySettings.GetQualityLevel()],
                lodBias = QualitySettings.lodBias,
                gpu = SystemInfo.graphicsDeviceName,
                unity = Application.unityVersion,
                development = Debug.isDebugBuild,
                summary = summary,
            }, true);
            Debug.Log("[LiftLab] benchmark " + json);
            OverlayText = $"Benchmark ({ModeNames[(int)_mode]}): p50 {summary.p50Ms:F2} ms · p95 {summary.p95Ms:F2} ms · p99 {summary.p99Ms:F2} ms · " +
                          $"batches {summary.batches} · SetPass {summary.setPassCalls} · GC/frame {summary.gcBytesPerFrame} B";
            _nextOverlay = Time.unscaledTime + 10f;
            if (output != null) File.WriteAllText(output, json);
            if (quit) Application.Quit();
        }

        void CameraPath(Vector3 centre, float t)
        {
            if (Camera == null) return;
            if (t < 0.4f)
            {
                Camera.Frame(centre, 60f);
                Camera.SetAngles(200f + t / 0.4f * 360f, 22f);
            }
            else if (t < 0.7f)
            {
                float k = (t - 0.4f) / 0.3f;
                Camera.Frame(centre + new Vector3(8f, 0, (k - 0.5f) * TerminalSpacing * 0.9f), 14f);
                Camera.SetAngles(180f, 12f);
            }
            else
            {
                Camera.Frame(centre, 1500f);
                Camera.SetAngles(200f + (t - 0.7f) * 60f, 25f);
            }
        }

        IEnumerator CaptureAndQuit(string path)
        {
            for (int i = 0; i < 120; i++) yield return null;   // let LODs, shadows and the camera settle
            ScreenCapture.CaptureScreenshot(path);
            yield return null;
            yield return null;
            Debug.Log($"[LiftLab] Screenshot saved to {path}");
            Application.Quit();
        }

        void OnDestroy()
        {
            _materials?.Dispose();
            _stats?.Dispose();
        }

        [Serializable]
        struct BenchmarkResult
        {
            public string mode;
            public int objects;
            public string resolution;
            public string quality;
            public float lodBias;
            public string gpu;
            public string unity;
            public bool development;
            public FrameStats.Summary summary;
        }
    }
}
