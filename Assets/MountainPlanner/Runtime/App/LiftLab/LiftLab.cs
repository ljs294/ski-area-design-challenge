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
    ///   1 drive terminal · 2 return terminal (its rope climbing out up the line) · 3 chair · 4 line-up
    ///   5 stress (20 lifts with towers, ~500 chairs)
    ///   6 towers (every tower head type on a full tower)
    ///   7 snow guns (the SLE ground gun and the 10, 20 and 30 ft stick guns side by side)
    ///   8 gun field (500 snow guns along 20 trails, each turned and aimed its own way)
    ///   9 Monta FG4 (its drive as the bottom station, the rope climbing out with Sessellift chairs; the return beside)
    ///   0 chairs (the Sessellift chair and the Chairworks chair with either grip, side by side on the rope)
    ///   X six-pack (the Sessellift D6: both terminals, its chair, and its six tower heads on full towers with ropes)
    ///   J detachable quad (the Sessellift D4: bottom with its first tower, top, its chair, and its five tower heads)
    ///   K Sessellift Stern (its quad, triple and double: return and drive facing up the line, a chair between each pair)
    ///   U T-bar (the Monta T-bar: drive, hut, its three heads on leaning towers, return, T bars and a J bar)
    ///   Y carpets (the Glidepath magic carpets on their snow approaches: the uncovered single with side rails, the
    ///     covered single with an exit bay, the covered double; stations, portals, control posts and the hut)
    ///   M fixed-grip terminals (the Sessellift quad, triple and double, the Monta 4, 3 and 2 and the Stern: every terminal)
    ///   V terminal head: every fixed-grip terminal shows its next head (drive at either end: bottom, top 4, 6, 8)
    ///   Tab next asset · L LOD auto/0/1/2/3 · N snow · C livery colour · I accent colour (the carpet glass's tint, C16)
    ///   T turntable · G ground on/off
    ///   B benchmark · P screenshot · H help · Esc quit (camera: ViewCamera's keys)
    /// Unattended: -mode drive|return|chair|lineup|stress|empty|towers|guns|gunfield|monta|chairs|sixpack|fixedgrip|d4|stern|tbar|carpet, -lod n,
    /// -snow 0..1, -livery n and -accent n (the colours C and I step to, from 0),
    /// -head bottom|top|top6|top8 (the head every fixed-grip terminal shows; else its own),
    /// -view yaw,pitch,distance, -focus id (with -view: frame the first placed asset whose id starts so, at its hood),
    /// -glass a (every glass's face-on opacity, 0..1, for review), -screenshot file.png (captures once settled, then
    /// quits), -benchmark file.json (runs, writes, quits).
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
        public ViewCamera Camera;
        public LiftLabOverlay Overlay;

        public const float TerminalSpacing = 175f;   // along the line, stress layout
        public const float LiftSpacing = 25f;        // side by side, stress layout
        public const float ChairSpacing = 13.8f;     // 2.3 m/s x 3600 s x 4 seats / 2400 pph (roadmap 10)

        static readonly Color[] Liveries =
        {
            new Color(0.72f, 0.12f, 0.09f), new Color(0.10f, 0.26f, 0.62f), new Color(0.11f, 0.42f, 0.22f), new Color(0.93f, 0.70f, 0.10f),
            new Color(0.12f, 0.12f, 0.13f), new Color(0.90f, 0.90f, 0.88f), new Color(0.90f, 0.42f, 0.08f), new Color(0.05f, 0.47f, 0.50f),
        };
        /// <summary>The accents I cycles (LT41): the default dark, a light blue and red; the carpet galleries' glass takes it (C16).</summary>
        static readonly Color[] Accents = { LiftLivery.DarkAccent, new Color(0.61f, 0.78f, 0.91f), LiftLivery.Red };
        static readonly string[] ModeNames = { "drive", "return", "chair", "lineup", "stress", "empty", "towers", "guns", "gunfield", "monta", "chairs", "sixpack", "fixedgrip", "d4", "stern", "tbar", "carpet" };
        /// <summary>The fixed-grip mode's rows, front to back: each model's terminals, left to right.</summary>
        static readonly string[][] FixedGrip =
        {
            new[] { "sessellift_fgq4_return", "sessellift_fgq4_drive" },
            new[] { "sessellift_fgt3_return", "sessellift_fgt3_drive" },
            new[] { "sessellift_fgd2_return", "sessellift_fgd2_drive" },
            new[] { "monta_fg4_return_bottom", "monta_fg4_drive", "monta_fg4_return" },
            new[] { "monta_fg3_return_bottom", "monta_fg3_drive", "monta_fg3_return" },
            new[] { "monta_fg2_return_bottom", "monta_fg2_drive", "monta_fg2_return" },
            new[] { "sessellift_stern4_return", "sessellift_stern4_drive" },
            new[] { "sessellift_stern3_return", "sessellift_stern3_drive" },
            new[] { "sessellift_stern2_return", "sessellift_stern2_drive" },
        };
        /// <summary>The Sessellift Stern mode's rows, front to back: quad, triple and double, each with its chair.</summary>
        static readonly (string model, string chair)[] Stern =
            { ("sessellift_stern4", "sessellift_fgq4_chair"), ("sessellift_stern3", "sessellift_fgt3_chair"), ("sessellift_stern2", "sessellift_fgd2_chair") };
        /// <summary>The heads V steps through: the terminal's own (null), then each end and top row in turn.</summary>
        static readonly string[] HeadKeys = { null, "bottom", "top", "top6", "top8" };
        /// <summary>The chairs mode's chairs, left to right across the view, and where each hangs along +X (m).</summary>
        static readonly (string id, float x)[] Chairs =
            { ("sessellift_fgq4_chair", -3.4f), ("chairworks_chair_fixed", 0f), ("chairworks_chair_detach", 3.4f) };
        /// <summary>The snow guns in the line-up's order, and where each stands along +Z (m).</summary>
        static readonly (string id, float z)[] SnowGuns =
            { ("sle_ground_gun", -2.6f), ("sle_stick_gun_10", 0f), ("sle_stick_gun_20", 2.6f), ("sle_stick_gun_30", 6.2f) };
        const int GunLines = 20;                     // gun field: trails side by side
        const int GunsPerLine = 25;                  // gun field: guns along each trail
        const float GunLineSpacing = 40f;            // between trails
        const float GunSpacing = 30f;                // between guns along a trail
        /// <summary>The tower head types (LP11), in the line-up's order.</summary>
        static readonly string[] TowerHeads = { "tower_s4", "tower_s6", "tower_b8", "tower_d8", "tower_c8" };
        const float TowerRope = 9.5f;                // rope height of the towers in the towers mode
        /// <summary>The Sessellift D6 six-pack's tower heads, in the six-pack mode's order.</summary>
        static readonly string[] SixPackHeads =
            { "sessellift_d6_tower_s4", "sessellift_d6_tower_s6", "sessellift_d6_tower_s10", "sessellift_d6_tower_b10", "sessellift_d6_tower_d10", "sessellift_d6_tower_c8" };
        /// <summary>The Sessellift D4 detachable quad's tower heads (the quad's kit at its gauge), in the D4 mode's order.</summary>
        static readonly string[] QuadHeads =
            { "sessellift_d4_tower_s4", "sessellift_d4_tower_s6", "sessellift_d4_tower_b8", "sessellift_d4_tower_d8", "sessellift_d4_tower_c8" };
        const float LineShown = 100f;                // how far up the line the return mode draws the rope
        /// <summary>The Monta T-bar line, in the T-bar mode: each tower head and where it stands up the line (m).</summary>
        static readonly (string id, float z)[] TbarTowers =
            { ("monta_tbar_tower_s6", 30f), ("monta_tbar_tower_s4", 60f), ("monta_tbar_tower_d4", 90f) };
        const float TbarReturnZ = 120f;              // the T-bar's top terminal, facing back down the line
        const float TbarRope = 5f;                   // rope height on the T-bar's line towers (D8, about 5 m)
        const float TbarLean = 8f;                   // the masts' lean toward +X (monta_tbar.json tower.lean)
        const float TbarSpacing = 12f;               // between carriers (monta_tbar.json common.carrierSpacing)
        const float TbarCordGap = 0.05f;             // a retracted bar's eye under the box's outlet (carrier.cordGap)
        /// <summary>The carpet mode's lines across the view: their ids, where each stands along +X (m), covered or with side
        /// rails, and the deck's half width (glidepath_carpet.json section.deckOut past the outer belt).</summary>
        static readonly (string prefix, float x, bool covered, float deck)[] Carpets =
            { ("glidepath_carpet_", -7f, false, 0.805f), ("glidepath_carpet_", 0f, true, 0.805f), ("glidepath_carpet2_", 7.5f, true, 1.53f) };
        const int CarpetModules = 8;                 // 2 m modules a line: 16 m (52 ft) of belt between the stations
        const int CarpetExitBay = 4;                 // the covered lines' bay with the exit doors
        // the earth the game grades at each station's plate, under the snow (glidepath_carpet.json top and bottom
        // "approach", owner C13): level with the plate top past its edge (the top's run-off falling first), easing down
        // to natural ground, wider than the deck each side
        const float CarpetPlateTop = 0.372f, CarpetBottomPlate = -0.6f, CarpetTopPlate = -1.0f;
        const float CarpetFlat = 4.9f, CarpetEase = 0.10f, CarpetMargin = 1.0f, CarpetRunOff = 2.0f, CarpetRunOffFall = 0.05f;

        enum Mode { Drive, Return, Chair, Lineup, Stress, Empty, Towers, Guns, GunField, Monta, Chairs, SixPack, FixedGrip, Quad, Stern, Tbar, Carpet }

        readonly List<GameObject> _spawned = new List<GameObject>();
        readonly List<Mesh> _meshes = new List<Mesh>();   // the carpet approaches, made per scene
        readonly Dictionary<GameObject, int[]> _lodTriangles = new Dictionary<GameObject, int[]>();
        LiftMaterials _materials;
        Mode _mode = Mode.Drive;
        GameObject _focus;
        int _forcedLod = -1;
        float _snow = 1f;
        int _livery;
        int _accent;                                 // index into Accents
        float _glassAlpha = -1f;                     // -glass: every glass's face-on opacity (review); below 0 keeps each own
        int _head;                                   // index into HeadKeys
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
            if (int.TryParse(Arg(args, "-livery"), out int livery)) _livery = Mathf.Max(0, livery);
            if (int.TryParse(Arg(args, "-accent"), out int accent)) _accent = Mathf.Max(0, accent);
            if (float.TryParse(Arg(args, "-glass"), NumberStyles.Float, CultureInfo.InvariantCulture, out float glass))
                _glassAlpha = Mathf.Clamp01(glass);
            string head = Arg(args, "-head");
            if (head != null && Array.IndexOf(HeadKeys, head) > 0) _head = Array.IndexOf(HeadKeys, head);
            Show(_mode);
            string view = Arg(args, "-view");
            if (view != null && Camera != null)
            {
                var v = view.Split(',').Select(t => float.Parse(t, CultureInfo.InvariantCulture)).ToArray();
                Camera.SetAngles(v[0], v[1]);
                Camera.Frame(Camera.Target, v[2]);
                string focus = Arg(args, "-focus");
                if (focus != null) FocusOn(focus, v[0], v[1], v[2]);
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
            foreach (var m in _meshes) Destroy(m);
            _meshes.Clear();
            _focus = null;
        }

        /// <summary>A lift asset in a livery colour and the accent I picks (the default dark, as before).</summary>
        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, Color livery) =>
            Spawn(prefab, position, rotation, new LiftLivery(livery, Accents[_accent % Accents.Length]));

        GameObject Spawn(GameObject prefab, Vector3 position, Quaternion rotation, LiftLivery livery)
        {
            var go = Instantiate(prefab, position, rotation);
            var rig = go.GetComponent<LiftRig>();
            if (rig != null && HeadKeys[_head] != null) rig.ShowHead(HeadKeys[_head]);   // a terminal without it keeps its own
            _materials.Apply(go, livery);
            _spawned.Add(go);
            if (!_lodTriangles.ContainsKey(prefab)) _lodTriangles[prefab] = LodTriangles(prefab);
            var group = go.GetComponent<LODGroup>();
            if (group != null) group.ForceLOD(_forcedLod);
            return go;
        }

        /// <summary>Triangles per LOD of the parts an instance shows (a terminal's other heads are inactive).</summary>
        static int[] ShownTriangles(GameObject go)
        {
            var group = go.GetComponent<LODGroup>();
            if (group == null) return Array.Empty<int>();
            return group.GetLODs().Select(l => l.renderers.Where(r => r != null && r.gameObject.activeInHierarchy).Sum(r =>
            {
                var mf = r.GetComponent<MeshFilter>();
                if (mf == null || mf.sharedMesh == null) return 0;
                int t = 0;
                for (int s = 0; s < mf.sharedMesh.subMeshCount; s++) t += (int)mf.sharedMesh.GetIndexCount(s) / 3;
                return t;
            })).ToArray();
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
        GameObject Asset(string id) => Models != null ? Models.Find(id) : null;

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
                    if (mode == Mode.Return) AddReturnLine(_focus, livery);
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
                case Mode.Guns:
                    foreach (var (id, z) in SnowGuns)
                    {
                        var prefab = Asset(id);
                        if (prefab == null) continue;
                        var gun = Spawn(prefab, new Vector3(0, 0, z), Quaternion.identity, livery);
                        if (id == "sle_stick_gun_20") _focus = gun;
                    }
                    if (Camera != null) { Camera.MinDistance = 1.5f; Camera.Frame(new Vector3(0, 3.2f, 3f), 20f); Camera.SetAngles(90f, 6f); }
                    break;
                case Mode.GunField:
                    BuildGunField();
                    break;
                case Mode.Monta:
                {
                    // bottom drive: the drive loads (chair_load) and its rope climbs out up the line with the
                    // Sessellift chairs it carries; the return, the top station, stands beside it for comparison
                    var drive = Asset("monta_fg4_drive");
                    var top = Asset("monta_fg4_return");
                    if (top != null) Spawn(top, new Vector3(-14f, 0, 0), Quaternion.identity, livery);
                    if (drive == null) break;
                    _focus = Spawn(drive, Vector3.zero, Quaternion.identity, livery);
                    AddReturnLine(_focus, livery);
                    FrameOn(_focus, 40f, 160f, 18f);   // from up the line: both stations, the chairs coming down
                    break;
                }
                case Mode.Chairs:
                    foreach (var (id, x) in Chairs)
                    {
                        var prefab = Asset(id);
                        if (prefab == null) continue;
                        var chair = Spawn(prefab, new Vector3(x, rope, 0), Quaternion.identity, livery);
                        AddRope(new Vector3(x, rope, -4f), new Vector3(x, rope, 4f), null, livery, true);
                        if (id == "chairworks_chair_fixed") _focus = chair;
                    }
                    if (_focus != null) FrameOn(_focus, 9f, 205f, 12f);
                    break;
                case Mode.SixPack:
                {
                    // the D6's return and drive facing up the line, a chair on the rope between them, and its tower
                    // heads on full kit towers further up, each with its ropes and a chair on the right
                    var chair = Asset("sessellift_d6_chair");
                    var ret = Asset("sessellift_d6_return");
                    var drive = Asset("sessellift_d6_drive");
                    if (ret != null) Spawn(ret, new Vector3(-12f, 0, 0), Quaternion.identity, livery);
                    if (drive != null) Spawn(drive, new Vector3(12f, 0, 0), Quaternion.identity, livery);
                    var rig = ret != null ? ret.GetComponent<LiftRig>() : null;
                    var bend = rig != null ? rig.Socket("rope_right_bend") : null;
                    if (chair != null) Spawn(chair, new Vector3(0, bend != null ? bend.localPosition.y : 3.6f, 0), Quaternion.identity, livery);
                    for (int i = 0; i < SixPackHeads.Length; i++)
                    {
                        var head = SpawnTower(SixPackHeads[i], new Vector3((i - (SixPackHeads.Length - 1) / 2f) * 11f, 0, 45f), Quaternion.identity, TowerRope, livery);
                        if (head == null) continue;
                        if (SixPackHeads[i] == "sessellift_d6_tower_s10") _focus = head;
                        var hr = head.GetComponent<LiftRig>();
                        foreach (string side in new[] { "left", "right" })
                        {
                            var p = hr.Socket("rope_" + side).position;
                            AddRope(p + Vector3.back * 16f, p + Vector3.forward * 16f, side == "right" ? chair : null, livery, true);
                        }
                    }
                    if (_focus != null) FrameOn(_focus, 38f, 205f, 14f);
                    break;
                }
                case Mode.Quad:
                {
                    // the D4's top and bottom facing up the line (the bottom's first tower ahead of it), a chair on the
                    // rope between them, and its tower heads on full kit towers further up, each with its ropes
                    var chair = Asset("sessellift_d4_chair");
                    var top = Asset("sessellift_d4_top");
                    var bottom = Asset("sessellift_d4_bottom");
                    if (top != null) Spawn(top, new Vector3(-12f, 0, 0), Quaternion.identity, livery);
                    if (bottom != null) Spawn(bottom, new Vector3(12f, 0, 0), Quaternion.identity, livery);
                    var rig = top != null ? top.GetComponent<LiftRig>() : null;
                    var bend = rig != null ? rig.Socket("rope_right_bend") : null;
                    if (chair != null) Spawn(chair, new Vector3(0, bend != null ? bend.localPosition.y : 3.6f, 0), Quaternion.identity, livery);
                    for (int i = 0; i < QuadHeads.Length; i++)
                    {
                        var head = SpawnTower(QuadHeads[i], new Vector3((i - (QuadHeads.Length - 1) / 2f) * 11f, 0, 45f), Quaternion.identity, TowerRope, livery);
                        if (head == null) continue;
                        if (QuadHeads[i] == "sessellift_d4_tower_b8") _focus = head;
                        var hr = head.GetComponent<LiftRig>();
                        foreach (string side in new[] { "left", "right" })
                        {
                            var p = hr.Socket("rope_" + side).position;
                            AddRope(p + Vector3.back * 16f, p + Vector3.forward * 16f, side == "right" ? chair : null, livery, true);
                        }
                    }
                    if (_focus != null) FrameOn(_focus, 38f, 205f, 14f);
                    break;
                }
                case Mode.Tbar:
                {
                    // the Monta T-bar on level ground: the drive (bottom) facing up the line, its operator hut beside it,
                    // the three tower heads on leaning towers, the return (top) facing back; T bars retracted every
                    // carrier spacing on both ropes, and a J bar beside the drive on a rope stub
                    var drive = Asset("monta_tbar_drive");
                    var top = Asset("monta_tbar_return");
                    var box = Asset("monta_tbar_springbox");
                    var hut = Asset("monta_tbar_hut");
                    if (drive == null || top == null) break;
                    var d = Spawn(drive, Vector3.zero, Quaternion.identity, livery);
                    var r = Spawn(top, new Vector3(0, 0, TbarReturnZ), Quaternion.Euler(0, 180f, 0), livery);
                    if (hut != null) Spawn(hut, new Vector3(4.6f, 0, 2f), Quaternion.Euler(0, -90f, 0), livery);
                    var dr = d.GetComponent<LiftRig>();
                    var rr = r.GetComponent<LiftRig>();
                    var up = new List<Vector3> { dr.Socket("rope_right_bw").position, dr.Socket("rope_right_out").position };
                    var down = new List<Vector3> { rr.Socket("rope_left_bw").position, rr.Socket("rope_left_out").position };
                    var towers = new List<GameObject>();
                    foreach (var (id, z) in TbarTowers)
                    {
                        var head = SpawnTbarTower(id, z, TbarRope, livery);
                        if (head == null) continue;
                        towers.Add(head);
                        if (id == "monta_tbar_tower_s4") _focus = head;
                    }
                    foreach (var head in towers) up.Add(head.GetComponent<LiftRig>().Socket("rope_right").position);
                    for (int i = towers.Count - 1; i >= 0; i--) down.Add(towers[i].GetComponent<LiftRig>().Socket("rope_left").position);
                    up.Add(rr.Socket("rope_right_out").position);
                    up.Add(rr.Socket("rope_right_bw").position);
                    down.Add(dr.Socket("rope_left_out").position);
                    down.Add(dr.Socket("rope_left_bw").position);
                    AddRope(up, null, livery);
                    AddRope(down, null, livery);
                    if (box != null)
                    {
                        SpawnCarriers(up, box, Asset("monta_tbar_bar_t"), livery);
                        SpawnCarriers(down, box, Asset("monta_tbar_bar_t"), livery);
                        var stub = new Vector3(-6f, dr.Socket("rope_right_bw").position.y, 4f);
                        AddRope(stub + Vector3.back * 3f, stub + Vector3.forward * 3f, null, livery, true);
                        SpawnCarrier(box, Asset("monta_tbar_bar_j"), stub, Quaternion.identity, livery);
                    }
                    if (_focus != null) FrameOn(_focus, 22f, 215f, 14f);
                    break;
                }
                case Mode.Carpet:
                {
                    // the three Glidepath carpets side by side on level ground, each on its snow approaches; the hut
                    // beside the uncovered single's bottom station (clear of its approach), its door toward the carpet
                    var bottomPads = new List<(float edge, float dir, float x, float half, float runOff)>();
                    var topPads = new List<(float edge, float dir, float x, float half, float runOff)>();
                    foreach (var (prefix, x, covered, deck) in Carpets)
                    {
                        var bay = BuildCarpet(prefix, x, covered, deck, livery, bottomPads, topPads);
                        if (covered && x == 0f) _focus = bay;
                    }
                    var hut = Asset("glidepath_carpet_hut");
                    if (hut != null) Spawn(hut, new Vector3(-13.6f, 0, -1.6f), Quaternion.Euler(0, -90f, 0), livery);
                    AddApproach(bottomPads);
                    AddApproach(topPads);
                    if (_focus != null) FrameOn(_focus, 26f, 215f, 18f);
                    break;
                }
                case Mode.Stern:
                    // each Stern model's return and drive facing up the line, its chair on the rope between them; the
                    // Stern is a bottom drive only, so V steps just the return through its top rows
                    for (int row = 0; row < Stern.Length; row++)
                    {
                        var ret = Asset(Stern[row].model + "_return");
                        var drive = Asset(Stern[row].model + "_drive");
                        var chair = Asset(Stern[row].chair);
                        float z = -row * 28f;
                        if (ret != null) Spawn(ret, new Vector3(-12f, 0, z), Quaternion.identity, livery);
                        var d = drive != null ? Spawn(drive, new Vector3(12f, 0, z), Quaternion.identity, livery) : null;
                        if (row == 0) _focus = d;
                        var bw = drive != null ? drive.GetComponent<LiftRig>().Socket("rope_right_bw") : null;
                        if (chair != null) Spawn(chair, new Vector3(0, bw != null ? bw.localPosition.y : 3.04f, z), Quaternion.identity, livery);
                    }
                    if (_focus != null) FrameOn(_focus, 34f, 215f, 18f);
                    break;
                case Mode.FixedGrip:
                    // every fixed-grip terminal facing up the line (+Z), a model to a row
                    for (int row = 0; row < FixedGrip.Length; row++)
                        for (int i = 0; i < FixedGrip[row].Length; i++)
                        {
                            var prefab = Asset(FixedGrip[row][i]);
                            if (prefab == null) continue;
                            var t = Spawn(prefab, new Vector3((i - 1) * 16f, 0, -row * 24f), Quaternion.identity, livery);
                            if (row == 0 && i == 1) _focus = t;
                        }
                    if (_focus != null) FrameOn(_focus, 70f, 215f, 24f);
                    break;
            }
            _materials.SetSnowLoad(_snow);
            if (_glassAlpha >= 0f) SetGlassAlpha(_glassAlpha);
        }

        /// <summary>Review only (-glass a): every placed glass copy, the hoods' and the carpet gallery's, at one face-on opacity.</summary>
        void SetGlassAlpha(float alpha)
        {
            var seen = new HashSet<Material>();
            int set = 0;
            foreach (var go in _spawned)
                foreach (var r in go.GetComponentsInChildren<Renderer>(true))
                    foreach (var m in r.sharedMaterials)
                        if (m != null && seen.Add(m) && m.name.StartsWith("LiftGlass", StringComparison.Ordinal))
                        {
                            m.SetFloat("_GlassAlpha", alpha);
                            set++;
                        }
            Debug.Log($"[LiftLab] -glass: {set} glass materials at {alpha:0.00} face-on");
        }

        /// <summary>Review only (-focus id): orbit the first placed asset whose name starts with id, aimed at its hood.</summary>
        void FocusOn(string id, float yaw, float pitch, float distance)
        {
            var go = _spawned.FirstOrDefault(s => s != null && s.name.StartsWith(id, StringComparison.Ordinal));
            if (go == null || Camera == null) { Debug.LogWarning($"[LiftLab] -focus: no '{id}' placed"); return; }
            var b = Bounds(go);
            var look = new Vector3(b.center.x, b.max.y - 2.5f, b.center.z);   // the hood, under the roof (the footings sink the centre)
            Camera.MinDistance = 1.5f;
            Camera.Frame(look, distance);
            Camera.SetAngles(yaw, pitch);
        }

        /// <summary>A prefab's socket in its own frame (sockets are under the root, which is at its origin).</summary>
        static Vector3 SocketIn(GameObject prefab, string name)
        {
            var rig = prefab.GetComponent<LiftRig>();
            var s = rig != null ? rig.Socket(name) : null;
            return s != null ? rig.transform.InverseTransformPoint(s.position) : Vector3.zero;
        }

        /// <summary>One Glidepath carpet up +Z from (x, 0, 0), as the game chains it: the bottom (tension) station facing up
        /// the line, CarpetModules belt modules each hung on the last one's front socket from the station's belt_out, the top
        /// (drive) station turned round on the last front socket by its belt_in. Covered: a gallery bay on every module (the
        /// exit bay at CarpetExitBay) and a portal at each end; else side rails on all but the end modules (riders step on
        /// and off clear of them). The control posts where the stations' post sockets say, on the approach. Adds each end's
        /// approach pad; returns the middle gallery bay (null if uncovered).</summary>
        GameObject BuildCarpet(string prefix, float x, bool covered, float deck, Color livery,
                               List<(float edge, float dir, float x, float half, float runOff)> bottomPads,
                               List<(float edge, float dir, float x, float half, float runOff)> topPads)
        {
            var bottom = Asset(prefix + "bottom");
            var top = Asset(prefix + "top");
            var module = Asset(prefix + "segment");
            if (bottom == null || top == null || module == null) return null;
            var start = new Vector3(x, 0, 0);
            Spawn(bottom, start, Quaternion.identity, livery);
            var p = start + SocketIn(bottom, "belt_out");
            var first = p;
            var back = SocketIn(module, "back");
            var step = SocketIn(module, "front") - back;
            GameObject middle = null;
            for (int k = 0; k < CarpetModules; k++)
            {
                var at = p - back;
                Spawn(module, at, Quaternion.identity, livery);
                var extra = covered ? Asset(prefix + (k == CarpetExitBay ? "gallery_exit" : "gallery"))
                    : k > 0 && k < CarpetModules - 1 ? Asset(prefix + "rail") : null;
                if (extra != null)
                {
                    var e = Spawn(extra, at, Quaternion.identity, livery);
                    if (covered && k == CarpetModules / 2 - 1) middle = e;
                }
                p += step;
            }
            var turned = Quaternion.Euler(0, 180f, 0);
            var topAt = p - turned * SocketIn(top, "belt_in");
            Spawn(top, topAt, turned, livery);
            var portal = covered ? Asset(prefix + "portal") : null;
            if (portal != null)
            {
                var g = SocketIn(portal, "gallery");
                Spawn(portal, first - g, Quaternion.identity, livery);
                Spawn(portal, p - turned * g, turned, livery);
            }
            var post = Asset("glidepath_carpet_post");
            if (post != null)
            {
                Spawn(post, start + SocketIn(bottom, "post"), Quaternion.identity, livery);
                Spawn(post, topAt + turned * SocketIn(top, "post"), turned, livery);
            }
            float half = deck + CarpetMargin;
            bottomPads.Add((start.z + CarpetBottomPlate, -1f, x, half, 0f));
            topPads.Add((topAt.z - CarpetTopPlate, 1f, x, half, CarpetRunOff));
            return middle;
        }

        /// <summary>The snow over the earth the game grades at one end of the carpets (owner C13): each station's pad level
        /// with its plate top past the plate's edge for CarpetFlat (the top's run-off falling first), easing down to the
        /// Lab's level ground at CarpetEase, CarpetMargin wider than the deck each side, banked 1:1 back along the station's
        /// sides; one heightfield for all the lines at that end (the highest pad wins), drawn where it stands above the
        /// ground, 2 mm proud of it.</summary>
        void AddApproach(List<(float edge, float dir, float x, float half, float runOff)> pads)
        {
            if (pads.Count == 0) return;
            const float step = 0.25f;
            float reach = 1f + CarpetPlateTop / CarpetEase;
            float u0 = float.MaxValue, u1 = float.MinValue, v0 = float.MaxValue, v1 = float.MinValue;
            foreach (var pad in pads)
            {
                float far = pad.edge + pad.dir * (pad.runOff + CarpetFlat);
                u0 = Mathf.Min(u0, Mathf.Min(pad.edge, far));
                u1 = Mathf.Max(u1, Mathf.Max(pad.edge, far));
                v0 = Mathf.Min(v0, pad.x - pad.half);
                v1 = Mathf.Max(v1, pad.x + pad.half);
            }
            u0 -= reach; u1 += reach; v0 -= reach; v1 += reach;
            int nu = Mathf.CeilToInt((u1 - u0) / step) + 1, nv = Mathf.CeilToInt((v1 - v0) / step) + 1;
            var height = new float[nu, nv];
            for (int i = 0; i < nu; i++)
                for (int k = 0; k < nv; k++)
                {
                    float u = u0 + i * step, v = v0 + k * step, best = float.MinValue;
                    foreach (var pad in pads)
                    {
                        float s = (u - pad.edge) * pad.dir;   // past the plate's edge, away from the line
                        float level = CarpetPlateTop - Mathf.Clamp(s, 0f, pad.runOff) * CarpetRunOffFall;
                        float side = Mathf.Max(0f, Mathf.Abs(v - pad.x) - pad.half);
                        float drop = s < 0f ? -s + side * CarpetEase   // beside the station: a 1:1 bank
                            : new Vector2(Mathf.Max(0f, s - pad.runOff - CarpetFlat), side).magnitude * CarpetEase;
                        best = Mathf.Max(best, level - drop);
                    }
                    height[i, k] = best;
                }
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            var index = new int[nu, nv];
            for (int i = 0; i < nu; i++) for (int k = 0; k < nv; k++) index[i, k] = -1;
            int Vertex(int i, int k)
            {
                if (index[i, k] >= 0) return index[i, k];
                var at = new Vector3(v0 + k * step, Mathf.Max(0.002f, height[i, k]), u0 + i * step);
                index[i, k] = verts.Count;
                verts.Add(at);
                uvs.Add(new Vector2(at.x, at.z) * 0.1f);
                return index[i, k];
            }
            for (int i = 0; i < nu - 1; i++)
                for (int k = 0; k < nv - 1; k++)
                {
                    if (height[i, k] <= 0f && height[i + 1, k] <= 0f && height[i + 1, k + 1] <= 0f && height[i, k + 1] <= 0f) continue;
                    int a = Vertex(i, k), b = Vertex(i + 1, k), c = Vertex(i + 1, k + 1), d = Vertex(i, k + 1);
                    tris.Add(a); tris.Add(b); tris.Add(c);   // clockwise seen from above: facing up
                    tris.Add(a); tris.Add(c); tris.Add(d);
                }
            if (tris.Count == 0) return;
            var mesh = new Mesh { name = "CarpetApproach", indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.SetVertices(verts);
            mesh.SetUVs(0, uvs);
            mesh.SetTriangles(tris, 0);
            mesh.RecalculateNormals();
            mesh.RecalculateBounds();
            _meshes.Add(mesh);
            var go = new GameObject("CarpetApproach");
            go.AddComponent<MeshFilter>().sharedMesh = mesh;
            var r = go.AddComponent<MeshRenderer>();
            r.sharedMaterial = GroundMaterial;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _spawned.Add(go);
        }

        /// <summary>A line tower of the given head type, its rope at ropeHeight above position: the base, as many 1 m mast
        /// sections as fit, and the head; the base rides up or down by the remainder (under half a metre), as a
        /// footing would be set. Returns the head.</summary>
        GameObject SpawnTower(string headId, Vector3 position, Quaternion rotation, float ropeHeight, Color livery)
        {
            var head = Asset(headId) ?? Prefab(headId);   // a full id (the D6's heads) or a Sessellift FGQ-4 part
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

        /// <summary>A Monta T-bar line tower whose head stands on the line centre at `z`, its ropes at ropeHeight: the
        /// base off to the return side (-X), as many 1 m mast sections as fit up the leaned axis (each tilted TbarLean
        /// toward +X, the head level on top), the base riding up or down by the remainder. Returns the head.</summary>
        GameObject SpawnTbarTower(string headId, float z, float ropeHeight, Color livery)
        {
            var head = Asset(headId);
            var mast = Asset("monta_tbar_mast");
            var foundation = Asset("monta_tbar_base");
            if (head == null || mast == null || foundation == null) return null;
            var lean = Quaternion.AngleAxis(-TbarLean, Vector3.forward);   // +Y tips toward +X
            var axis = lean * Vector3.up;
            var foot = foundation.GetComponent<LiftRig>().Socket("mast_foot").localPosition;
            float section = mast.GetComponent<LiftRig>().Socket("top").localPosition.y;
            float ropeOnHead = head.GetComponent<LiftRig>().Socket("rope_right").localPosition.y;
            int sections = Mathf.Max(1, Mathf.RoundToInt((ropeHeight - foot.y - ropeOnHead) / (axis.y * section)));
            var top = foot + axis * (section * sections);
            var at = new Vector3(-top.x, ropeHeight - top.y - ropeOnHead, z);   // the pier, so the head lands on X = 0
            Spawn(foundation, at, Quaternion.identity, livery);
            for (int i = 0; i < sections; i++)
                Spawn(mast, at + foot + axis * (section * i), lean, livery);
            return Spawn(head, at + top, Quaternion.identity, livery);
        }

        /// <summary>T-bar carriers every TbarSpacing along a rope, in travel order, retracted: each recoil box on the
        /// rope facing the way it travels, its bar hanging under it.</summary>
        void SpawnCarriers(IReadOnlyList<Vector3> points, GameObject box, GameObject bar, Color livery)
        {
            float next = TbarSpacing * 0.5f, walked = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                var seg = points[i] - points[i - 1];
                float len = seg.magnitude;
                var flat = Vector3.ProjectOnPlane(seg, Vector3.up);
                if (len < 1e-4f || flat.sqrMagnitude < 1e-8f) { walked += len; continue; }
                var rotation = Quaternion.LookRotation(flat.normalized, Vector3.up);
                for (; next <= walked + len; next += TbarSpacing)
                    SpawnCarrier(box, bar, points[i - 1] + seg * ((next - walked) / len), rotation, livery);
                walked += len;
            }
        }

        void SpawnCarrier(GameObject box, GameObject bar, Vector3 grip, Quaternion rotation, Color livery)
        {
            var b = Spawn(box, grip, rotation, livery);
            if (bar == null) return;
            var cord = b.GetComponent<LiftRig>().Socket("cord");
            if (cord != null) Spawn(bar, cord.position + Vector3.down * TbarCordGap, rotation, livery);
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
                // towers face the return (their platform side, +Z, points downhill) and carry the rope where the
                // straight rope between the terminals passes them (the ground here is flat, so the line is too)
                var rOut = rr.Socket("rope_right_out").position;
                var dOut = dr.Socket("rope_left_out").position;
                for (int k = 1; k <= 2; k++)
                {
                    float z = TerminalSpacing * k / 3f;
                    float rope = Mathf.Lerp(rOut.y, dOut.y, Mathf.InverseLerp(rOut.z, dOut.z, z));
                    SpawnTower(TowerHeads[(i * 2 + k) % TowerHeads.Length], new Vector3(x, 0, z), Quaternion.Euler(0, 180f, 0), rope, livery);
                }
                // the return's right rope meets the drive's left rope, and vice versa
                AddRope(rr.Socket("rope_right_out").position, dr.Socket("rope_left_out").position, chair, livery, true);
                AddRope(rr.Socket("rope_left_out").position, dr.Socket("rope_right_out").position, chair, livery, false);
            }
            if (Camera != null) { Camera.Frame(new Vector3(0, 3, 20f), 140f); Camera.SetAngles(205f, 22f); }
        }

        /// <summary>500 snow guns along 20 trails (40 m apart, a gun every 30 m): mostly stick guns of each lance length,
        /// every fifth a ground gun, each turned across its trail give or take 30 degrees and aimed on its own hinge (the
        /// lance a few degrees, the ground gun's elevation more). Seeded, so every run builds the same field.</summary>
        void BuildGunField()
        {
            var random = new System.Random(20260929);
            string[] mix = { "sle_stick_gun_20", "sle_stick_gun_30", "sle_stick_gun_10", "sle_stick_gun_20", "sle_ground_gun" };
            var livery = Liveries[_livery % Liveries.Length];
            for (int line = 0; line < GunLines; line++)
                for (int k = 0; k < GunsPerLine; k++)
                {
                    var prefab = Asset(mix[(line * 3 + k) % mix.Length]);
                    if (prefab == null) continue;
                    float x = (line - (GunLines - 1) / 2f) * GunLineSpacing;
                    float z = (k - (GunsPerLine - 1) / 2f) * GunSpacing;
                    float yaw = (line % 2 == 0 ? 90f : -90f) + (float)(random.NextDouble() * 60.0 - 30.0);
                    var gun = Spawn(prefab, new Vector3(x, 0, z), Quaternion.Euler(0, yaw, 0), livery);
                    var rig = gun.GetComponent<LiftRig>();
                    if (rig != null && rig.Pivots.Length > 0)   // the per-instance aim: a tilt on the gun's own hinge
                    {
                        float range = prefab.name == "sle_ground_gun" ? 10f : 3f;
                        rig.Pivots[0].localRotation = Quaternion.AngleAxis((float)(random.NextDouble() * 2.0 - 1.0) * range, rig.PivotAxes[0]);
                    }
                    if (line == GunLines / 2 && k == GunsPerLine / 2) _focus = gun;
                }
            if (Camera != null) { Camera.Frame(new Vector3(0, 3, 0), 140f); Camera.SetAngles(205f, 22f); }
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
            // chairs hang plumb and face the way they travel: uphill on the right rope, down on the left; outboard
            // is +X in both
            var rotation = Quaternion.LookRotation(Vector3.ProjectOnPlane(uphill ? dir : -dir, Vector3.up).normalized, Vector3.up);
            for (float t = ChairSpacing * 0.5f; t < length; t += ChairSpacing)
                Spawn(chair, from + dir * t, rotation, livery);
        }

        /// <summary>A rope along `points`, in travel order, with chairs every ChairSpacing along it, hanging plumb
        /// and facing the way they travel.</summary>
        void AddRope(IReadOnlyList<Vector3> points, GameObject chair, Color livery)
        {
            var go = new GameObject("Rope");
            var line = go.AddComponent<LineRenderer>();
            line.sharedMaterial = RopeMaterial;
            line.widthMultiplier = 0.045f;
            line.positionCount = points.Count;
            for (int i = 0; i < points.Count; i++) line.SetPosition(i, points[i]);
            line.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            _spawned.Add(go);
            if (chair == null) return;
            float next = ChairSpacing * 0.5f, walked = 0f;
            for (int i = 1; i < points.Count; i++)
            {
                var seg = points[i] - points[i - 1];
                float len = seg.magnitude;
                if (len < 1e-4f) continue;
                var flat = Vector3.ProjectOnPlane(seg, Vector3.up);
                var rotation = flat.sqrMagnitude > 1e-8f ? Quaternion.LookRotation(flat.normalized, Vector3.up) : Quaternion.identity;
                for (; next <= walked + len; next += ChairSpacing)
                    Spawn(chair, points[i - 1] + seg * ((next - walked) / len), rotation, livery);
                walked += len;
            }
        }

        /// <summary>The return mode's rope: down the left rope from up the line, round the back of the bullwheel
        /// and up the right rope, leaving the station climbing over the hold-down rows (owner), with chairs.</summary>
        void AddReturnLine(GameObject terminal, Color livery)
        {
            var rig = terminal.GetComponent<LiftRig>();
            if (rig == null || rig.Socket("rope_right_bw") == null || rig.Socket("rope_left_bw") == null) return;
            var up = RopeUpTheLine(rig, "right", LineShown);
            var down = RopeUpTheLine(rig, "left", LineShown);
            down.Reverse();
            var wheel = rig.Pivot("bullwheel");
            var centre = wheel != null ? wheel.position : (up[0] + down[down.Count - 1]) / 2f;
            var from = down[down.Count - 1] - centre;        // the left rope at the wheel
            var rope = new List<Vector3>(down);
            const int steps = 24;
            for (int k = 1; k < steps; k++)                  // round the back of the wheel, away from the line
                rope.Add(centre + Quaternion.AngleAxis(-180f * k / steps, Vector3.up) * from);
            rope.AddRange(up);
            AddRope(rope, Prefab("chair"), livery);
        }

        /// <summary>One side's rope from the bullwheel up the line, in travel order: level to the hold-down row
        /// where the terminal has one (rope_*_hold, the return), round the rope's circle under it (level at
        /// rope_*_hold, so its centre is straight above it) to where the rope leaves it climbing (rope_*_out), then
        /// on along that tangent for `length` metres; without one, level out past rope_*_out.</summary>
        static List<Vector3> RopeUpTheLine(LiftRig rig, string side, float length)
        {
            var along = rig.transform.forward;             // +Z: up the line
            var exit = rig.HeadSocket($"rope_{side}_out").position;   // the shown head's (drive at either end)
            var hold = rig.HeadSocket($"rope_{side}_hold");
            var points = new List<Vector3> { rig.Socket($"rope_{side}_bw").position };
            var dir = along;
            if (hold != null)
            {
                var h0 = hold.position;
                float d = Vector3.Dot(exit - h0, along), h = exit.y - h0.y;
                float r = (d * d + h * h) / (2f * h);
                float theta = Mathf.Asin(Mathf.Clamp(d / r, -1f, 1f));
                const int steps = 12;
                for (int k = 0; k <= steps; k++)
                {
                    float a = theta * k / steps;
                    points.Add(h0 + along * (r * Mathf.Sin(a)) + Vector3.up * (r * (1f - Mathf.Cos(a))));
                }
                dir = along * Mathf.Cos(theta) + Vector3.up * Mathf.Sin(theta);
            }
            else
                points.Add(exit);
            points.Add(points[points.Count - 1] + dir * length);
            return points;
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
            if (_benchmarking) { _stats.Record(dt, FrameStats.SampleGpuMs()); return; }
            var keys = Keyboard.current;
            if (keys != null)
            {
                if (keys.digit1Key.wasPressedThisFrame) Show(Mode.Drive);
                if (keys.digit2Key.wasPressedThisFrame) Show(Mode.Return);
                if (keys.digit3Key.wasPressedThisFrame) Show(Mode.Chair);
                if (keys.digit4Key.wasPressedThisFrame) Show(Mode.Lineup);
                if (keys.digit5Key.wasPressedThisFrame) Show(Mode.Stress);
                if (keys.digit6Key.wasPressedThisFrame) Show(Mode.Towers);
                if (keys.digit7Key.wasPressedThisFrame) Show(Mode.Guns);
                if (keys.digit8Key.wasPressedThisFrame) Show(Mode.GunField);
                if (keys.digit9Key.wasPressedThisFrame) Show(Mode.Monta);
                if (keys.digit0Key.wasPressedThisFrame) Show(Mode.Chairs);
                if (keys.xKey.wasPressedThisFrame) Show(Mode.SixPack);
                if (keys.mKey.wasPressedThisFrame) Show(Mode.FixedGrip);
                if (keys.jKey.wasPressedThisFrame) Show(Mode.Quad);
                if (keys.kKey.wasPressedThisFrame) Show(Mode.Stern);
                if (keys.uKey.wasPressedThisFrame) Show(Mode.Tbar);
                if (keys.yKey.wasPressedThisFrame) Show(Mode.Carpet);
                if (keys.vKey.wasPressedThisFrame) { _head = (_head + 1) % HeadKeys.Length; Show(_mode); }
                if (keys.tabKey.wasPressedThisFrame) Show(_mode < Mode.Chair ? _mode + 1 : Mode.Drive);
                if (keys.lKey.wasPressedThisFrame) CycleLod();
                if (keys.nKey.wasPressedThisFrame) { _snow = _snow > 0.75f ? 0f : _snow + 0.5f; _materials.SetSnowLoad(_snow); }
                if (keys.cKey.wasPressedThisFrame) { _livery++; Show(_mode); }
                if (keys.iKey.wasPressedThisFrame) { _accent++; Show(_mode); }
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
                    var focusRig = _focus.GetComponent<LiftRig>();
                    _text.Append(_focus.name.Replace("(Clone)", ""));
                    if (focusRig != null && !string.IsNullOrEmpty(focusRig.CatalogName))
                        _text.Append(" (").Append(focusRig.Maker).Append(' ').Append(focusRig.CatalogName).Append(')');
                    if (focusRig != null && focusRig.ShownHead >= 0)
                    {
                        var h = focusRig.Heads[focusRig.ShownHead];
                        _text.Append(" · ").Append(h.End).Append(" head");
                        if (h.Sheaves > 0) _text.Append(", ").Append(h.Sheaves).Append(" sheaves");
                        if (h.Default) _text.Append(" (its own)");
                        // a terminal with heads draws one at a time: count what this instance shows
                        tris = ShownTriangles(_focus);
                    }
                    _text.Append(" · LOD ").Append(active < lods.Length ? active.ToString() : "culled")
                         .Append(_forcedLod >= 0 ? " (forced)" : " (auto)");
                    if (active < tris.Length) _text.Append(" · ").Append(tris[active].ToString("N0")).Append(" triangles");
                    _text.Append(" · ").Append(distance.ToString("F0")).Append(" m\nLOD switches at");
                    for (int i = 0; i < lods.Length; i++)
                        _text.Append(' ').Append((size * QualitySettings.lodBias / (2f * tanHalf * lods[i].screenRelativeTransitionHeight)).ToString("F0")).Append(" m");
                    _text.Append(" (lodBias ").Append(QualitySettings.lodBias.ToString("0.#")).Append(")\n");
                }
            }
            _text.Append("Snow ").Append((_snow * 100).ToString("F0")).Append("% · livery ").Append(_livery % Liveries.Length + 1).Append('/').Append(Liveries.Length)
                 .Append(" · accent ").Append(_accent % Accents.Length + 1).Append('/').Append(Accents.Length);
            if (Help)
                _text.Append("\n1 drive · 2 return · 3 chair · 4 line-up · 5 stress · 6 towers · 7 snow guns · 8 gun field · 9 Monta · 0 chairs · X six-pack · J D4 quad · K Stern · U T-bar · Y carpets · M fixed-grip · V head (")
                     .Append(HeadKeys[_head] ?? "own").Append(") · Tab next · L LOD · N snow · C colour · I accent · T turntable · G ground · B benchmark · P screenshot · H help · Esc quit")
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
            var centre = _mode == Mode.Stress ? new Vector3(0, 3, TerminalSpacing * 0.5f)
                : _mode == Mode.GunField ? new Vector3(0, 3, 0)
                : (_focus != null ? Bounds(_focus).center : Vector3.up * 3);
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
