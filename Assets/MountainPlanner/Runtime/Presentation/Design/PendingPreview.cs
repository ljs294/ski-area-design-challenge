using System;
using MountainPlanner.World;
using UnityEngine;
using UnityEngine.Rendering;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// <see cref="IToolPreview"/> drawn with pooled line renderers draped on the terrain (P3-06). Each slot keeps its
    /// renderer and point buffer, so moving a ghost rewrites points and allocates nothing once the buffers have grown.
    /// Lines keep about the same width on screen all along: <see cref="Follow"/> sizes each stretch by its own distance
    /// from the camera (findings 2026-10-20, #4: sized by a long run's middle, a line was metres wide under the camera).
    /// A placeholder look: tasks 08 and 09 draw the real trails and lifts.
    /// </summary>
    public sealed class PendingPreview : IToolPreview, IDisposable
    {
        const float Lift = 1.5f;          // metres above the ground, so a line isn't swallowed by the terrain between samples
        const float Spacing = 6f;         // a draped line's sample spacing, metres
        const int MaxPoints = 1024;
        const float PixelsWide = 2.5f, DotPixels = 10f;
        /// <summary>The lines' shader (Resources): pulls each line toward the eye so far terrain doesn't swallow it.</summary>
        public const string ShaderResource = "MountainPlannerDesign/DesignLine";
        const int RingPoints = 48, DotPoints = 12, WidthKeys = 16;
        static readonly AnimationCurve One = AnimationCurve.Constant(0, 1, 1);
        static readonly int FadeNearId = Shader.PropertyToID("_FadeNear"), FadeFarId = Shader.PropertyToID("_FadeFar");

        static readonly Color[] Inks =
        {
            new Color(0.957f, 0.945f, 0.918f),   // Line: warm white #f4f1ea
            new Color(0.561f, 0.812f, 0.541f),   // Ok: --pos #8fcf8a
            new Color(1f, 0.478f, 0.400f),       // Bad: --neg #ff7a66
            new Color(0.941f, 0.549f, 0.235f),   // Plan: orange #f08c3c
            new Color(0.624f, 0.847f, 1f),       // Snap: ice #9fd8ff
            new Color(0.784f, 0.722f, 0.627f),   // Paint: a muted trail tone #c8b8a0
            new Color(0.957f, 0.945f, 0.918f),   // Brush: white
            new Color(0.878f, 0.278f, 0.227f),   // Lift: #e0473a
            new Color(0.247f, 0.682f, 0.416f),   // RatingGreen: the mockup's #3fae6a
            new Color(0.302f, 0.541f, 0.839f),   // RatingBlue: #4d8ad6
            new Color(0.067f, 0.067f, 0.067f),   // RatingBlack
            new Color(1f, 1f, 1f),               // Casing: white
        };

        sealed class Slot
        {
            public LineRenderer Renderer;
            public Vector3[] Points = new Vector3[16];
            public int Count;
            public Vector3 Centre;
            public bool Dot;
            public Vector2 DotAt;
            public float DotGround;
            public float LastWidth = -1;
            // The width along the line (metres), a key at each of WidthKeys points spread along it; kept, so Follow allocates nothing.
            public readonly Keyframe[] Keys = new Keyframe[WidthKeys];
            public readonly AnimationCurve Curve = new AnimationCurve();
            /// <summary>How many times the usual width (task 08's casing under a trail in focus).</summary>
            public float WidthScale = 1;
        }

        readonly Transform _root;
        readonly ITerrainSurface _surface;
        readonly Material[] _materials;
        Slot[] _slots = new Slot[32];
        int _used;

        public event Action Cleared;

        public PendingPreview(Transform parent, string name, ITerrainSurface surface, Material lineMaterial)
        {
            _root = new GameObject(name).transform;
            _root.SetParent(parent, false);
            _surface = surface;
            _materials = new Material[Inks.Length];
            var shader = Resources.Load<Shader>(ShaderResource);
            for (int i = 0; i < Inks.Length; i++)
            {
                // The line shader when it's there; the viewer's unlit material otherwise (edit-mode tests).
                var m = shader != null ? new Material(shader) : lineMaterial != null ? new Material(lineMaterial) : new Material(Shader.Find("Universal Render Pipeline/Unlit"));
                m.name = name + " " + (PreviewInk)i;
                m.SetColor("_BaseColor", Inks[i]);
                _materials[i] = m;
            }
        }

        public Transform Root => _root;

        Slot Get(int slot, PreviewInk ink)
        {
            if (slot >= _slots.Length) Array.Resize(ref _slots, Mathf.NextPowerOfTwo(slot + 1));
            var s = _slots[slot];
            if (s == null)
            {
                var go = new GameObject("slot " + slot);
                go.transform.SetParent(_root, false);
                var r = go.AddComponent<LineRenderer>();
                r.useWorldSpace = false;
                r.numCapVertices = 2;
                r.numCornerVertices = 2;
                r.shadowCastingMode = ShadowCastingMode.Off;
                r.receiveShadows = false;
                r.alignment = LineAlignment.View;
                s = _slots[slot] = new Slot { Renderer = r };
            }
            s.Renderer.sharedMaterial = _materials[(int)ink];
            s.Renderer.enabled = true;
            _used = Mathf.Max(_used, slot + 1);
            return s;
        }

        float Ground(Vector2 xz)
        {
            float h = _surface != null ? _surface.HeightAt(xz.x, xz.y) : 0;
            return float.IsNaN(h) ? 0 : h;
        }

        void Ensure(Slot s, int n)
        {
            // Grown keeping the points already written: a long line fills its buffer segment by segment (task 08 found
            // long trails losing their first points to (0, 0, 0) when it was replaced instead).
            if (s.Points.Length < n) Array.Resize(ref s.Points, Mathf.NextPowerOfTwo(n));
        }

        void Push(Slot s)
        {
            var r = s.Renderer;
            r.positionCount = s.Count;
            for (int i = 0; i < s.Count; i++) r.SetPosition(i, s.Points[i]);   // the buffer is longer than the line
            var c = Vector3.zero;
            for (int i = 0; i < s.Count; i++) c += s.Points[i];
            s.Centre = s.Count > 0 ? c / s.Count : Vector3.zero;
            s.LastWidth = -1;
        }

        /// <summary>
        /// A slot's line drawn <paramref name="scale"/> times as wide, and behind the others when <paramref name="behind"/>
        /// (task 08: the white casing under a trail in focus, the owner's "wider white casing").
        /// </summary>
        public void SetWidth(int slot, float scale, bool behind)
        {
            if (slot >= _slots.Length || _slots[slot] == null) return;
            var s = _slots[slot];
            s.WidthScale = scale;
            s.LastWidth = -1;
            s.Renderer.sortingOrder = behind ? -1 : 0;
            // Opaque lines at the same depth fight: one behind sits a little lower, so the line over it wins.
            s.Renderer.transform.localPosition = behind ? new Vector3(0, -0.6f, 0) : Vector3.zero;
        }

        public void Line(int slot, Vector2[] xz, int count, PreviewInk ink, bool closed = false)
        {
            if (count < 2) { Hide(slot); return; }
            var s = Get(slot, ink);
            s.Dot = false;
            int n = 0;
            int segments = closed ? count : count - 1;
            for (int k = 0; k < segments && n < MaxPoints; k++)
            {
                var a = xz[k];
                var b = xz[(k + 1) % count];
                int steps = Mathf.Clamp(Mathf.CeilToInt(Vector2.Distance(a, b) / Spacing), 1, MaxPoints);
                Ensure(s, n + steps + 1);
                for (int i = 0; i < steps && n < MaxPoints; i++)
                {
                    var p = Vector2.Lerp(a, b, i / (float)steps);
                    s.Points[n++] = new Vector3(p.x, Ground(p) + Lift, p.y);
                }
            }
            var last = closed ? xz[0] : xz[count - 1];
            Ensure(s, n + 1);
            s.Points[n++] = new Vector3(last.x, Ground(last) + Lift, last.y);
            s.Count = n;
            s.Renderer.loop = false;
            Push(s);
        }

        public void Ring(int slot, Vector2 centre, float radiusMetres, PreviewInk ink)
        {
            var s = Get(slot, ink);
            s.Dot = false;
            Ensure(s, RingPoints);
            for (int i = 0; i < RingPoints; i++)
            {
                float a = i * Mathf.PI * 2 / RingPoints;
                var p = centre + radiusMetres * new Vector2(Mathf.Cos(a), Mathf.Sin(a));
                s.Points[i] = new Vector3(p.x, Ground(p) + Lift, p.y);
            }
            s.Count = RingPoints;
            s.Renderer.loop = true;
            Push(s);
        }

        public void Dot(int slot, Vector2 at, PreviewInk ink)
        {
            var s = Get(slot, ink);
            s.Dot = true;
            s.DotAt = at;
            s.DotGround = Ground(at) + Lift;
            Ensure(s, DotPoints);
            s.Count = DotPoints;
            s.Renderer.loop = true;
            s.Centre = new Vector3(at.x, s.DotGround, at.y);
            s.LastWidth = -1;
            s.Renderer.widthCurve = One;   // a dot is sized by its multiplier
        }

        public void Hide(int slot)
        {
            if (slot < _slots.Length && _slots[slot] != null) _slots[slot].Renderer.enabled = false;
        }

        public void HideFrom(int slot)
        {
            for (int i = Mathf.Max(0, slot); i < _used; i++) Hide(i);
        }

        public void Clear()
        {
            HideFrom(0);
            Cleared?.Invoke();
        }

        /// <summary>
        /// Lines fade out (dithered) where they're nearer the camera than <paramref name="far"/> metres, gone by
        /// <paramref name="near"/>; 0, 0 never fades (the default). The Trails map layer's run lines (owner, TL26).
        /// </summary>
        public void SetFade(float near, float far)
        {
            foreach (var m in _materials)
            {
                m.SetFloat(FadeNearId, near);
                m.SetFloat(FadeFarId, far);
            }
        }

        /// <summary>
        /// Once a frame: keeps every visible line about <see cref="PixelsWide"/> pixels wide all along, each stretch by its
        /// own distance from the camera, and every dot a few pixels across. Rewrites only what changed by more than a
        /// tenth; no allocation.
        /// </summary>
        public void Follow(Camera camera)
        {
            if (camera == null) return;
            float perPixel = 2 * Mathf.Tan(camera.fieldOfView * 0.5f * Mathf.Deg2Rad) / Mathf.Max(1, camera.pixelHeight);
            var eye = camera.transform.position;
            var local = _root.InverseTransformPoint(eye);
            for (int i = 0; i < _used; i++)
            {
                var s = _slots[i];
                if (s == null || !s.Renderer.enabled) continue;
                if (!s.Dot)
                {
                    Widen(s, local, perPixel * PixelsWide * s.WidthScale);
                    continue;
                }
                float metresPerPixel = Vector3.Distance(local, s.Centre) * perPixel;
                float width = metresPerPixel * PixelsWide * s.WidthScale;
                if (s.LastWidth > 0 && Mathf.Abs(width - s.LastWidth) < s.LastWidth * 0.1f) continue;
                s.LastWidth = width;
                s.Renderer.widthMultiplier = width;
                // A dot is a small ring as wide as its own radius: it reads as a filled disc.
                float r = metresPerPixel * DotPixels * 0.5f;
                s.Renderer.widthMultiplier = r;
                for (int k = 0; k < DotPoints; k++)
                {
                    float a = k * Mathf.PI * 2 / DotPoints;
                    s.Points[k] = new Vector3(s.DotAt.x + r * 0.5f * Mathf.Cos(a), s.DotGround + 0.2f, s.DotAt.y + r * 0.5f * Mathf.Sin(a));
                }
                s.Renderer.positionCount = DotPoints;
                for (int k = 0; k < DotPoints; k++) s.Renderer.SetPosition(k, s.Points[k]);
            }
        }

        // A line's width curve: each key the width that's PixelsWide at that point's distance, joined straight. Rewritten
        // when any key would move by more than a tenth of the width last drawn.
        static void Widen(Slot s, Vector3 eye, float metresPerDistance)
        {
            if (s.Count < 2) return;
            bool changed = s.LastWidth < 0;
            for (int k = 0; k < WidthKeys && !changed; k++)
            {
                float was = s.Keys[k].value;
                changed = Mathf.Abs(Width(s, k, eye, metresPerDistance) - was) > was * 0.1f;
            }
            if (!changed) return;
            for (int k = 0; k < WidthKeys; k++) s.Keys[k] = new Keyframe(k / (float)(WidthKeys - 1), Width(s, k, eye, metresPerDistance));
            for (int k = 0; k < WidthKeys; k++)
            {
                int a = Mathf.Max(0, k - 1), b = Mathf.Min(WidthKeys - 1, k + 1);
                float slope = (s.Keys[b].value - s.Keys[a].value) / (s.Keys[b].time - s.Keys[a].time);
                s.Keys[k].inTangent = s.Keys[k].outTangent = slope;
            }
            s.LastWidth = 1;
            s.Curve.keys = s.Keys;
            s.Renderer.widthCurve = s.Curve;
            s.Renderer.widthMultiplier = 1;
        }

        static float Width(Slot s, int key, Vector3 eye, float metresPerDistance)
        {
            var p = s.Points[Mathf.RoundToInt(key / (float)(WidthKeys - 1) * (s.Count - 1))];
            return Mathf.Max(0.05f, Vector3.Distance(eye, p) * metresPerDistance);
        }

        /// <summary>For tests: whether a slot is showing, and how many points it has.</summary>
        public bool Showing(int slot) => slot < _slots.Length && _slots[slot] != null && _slots[slot].Renderer.enabled;
        public int Visible
        {
            get
            {
                int n = 0;
                for (int i = 0; i < _used; i++) if (Showing(i)) n++;
                return n;
            }
        }

        public void Dispose()
        {
            if (_root != null) Gone(_root.gameObject);
            foreach (var m in _materials) if (m != null) Gone(m);
        }

        static void Gone(UnityEngine.Object o)
        {
            if (Application.isPlaying) UnityEngine.Object.Destroy(o);
            else UnityEngine.Object.DestroyImmediate(o);   // edit-mode tests
        }
    }
}
