using System.Collections.Generic;
using UnityEngine;

namespace MountainPlanner.Presentation.Lifts
{
    // Task 09 (LT23): hovering any part of a built lift (a terminal, a tower, the rope, a chair) lights the whole lift.
    public sealed partial class LiftView
    {
        // What the pointer can hit, made once after a build (the first pick): each terminal and tower piece as its renderers'
        // box, and the rope with its chairs as capsules every ~5 m along the loop (the chairs hang below the rope).
        Bounds[] _boxes = System.Array.Empty<Bounds>();
        int[] _boxParts = System.Array.Empty<int>(), _rendererParts = System.Array.Empty<int>();
        // Finding E5: a terminal's box in its own frame (along the line), not the world's axes, which on a diagonal line
        // covered the station tower beside it; towers and the rest keep world boxes (identity here).
        Matrix4x4[] _boxToLocal = System.Array.Empty<Matrix4x4>();
        // Which part each spawned piece belongs to (F1): a tower's index, or one of the Part constants.
        readonly Dictionary<GameObject, int> _partOf = new Dictionary<GameObject, int>();
        int _spawning = PartRope;

        /// <summary>The part <see cref="Pick(Ray, float, float, out float, out int)"/> hit: a tower's index from 0, or these.</summary>
        public const int PartBottom = -1, PartTop = -2, PartRope = -3;
        Vector3[] _ropeA = System.Array.Empty<Vector3>(), _ropeB = System.Array.Empty<Vector3>();
        Renderer[] _renderers = System.Array.Empty<Renderer>();
        bool _pickReady, _highlighted;

        /// <summary>The rope's capsules: centred this far below it, this wide, so the chairs under it count.</summary>
        const float ChairDrop = 1.4f, RopeRadius = 1.8f, RopeStep = 5f;
        /// <summary>How far behind the rope's hit a tower still takes the pick (E5), metres.</summary>
        const float TowerReach = 30f;

        public bool Highlighted => _highlighted;

        /// <summary>A point on the rope a fraction of the way round its loop, with a chair's drop (captures and tests).</summary>
        public Vector3 RopePoint(float fraction) =>
            _loop.Length == 0 ? Vector3.zero : _loop[Mathf.Clamp((int)(fraction * (_loop.Length - 1)), 0, _loop.Length - 1)] - new Vector3(0, 1, 0);

        // The boxes and renderers the pointer and the highlight use, made again at the next pick (after a build or a re-height).
        // Which part each piece belongs to (_partOf) stays: only Clear forgets that, with the pieces. A lit lift stays lit.
        void ForgetPick()
        {
            bool lit = _highlighted;
            if (lit) ApplyHighlight(false);
            _pickReady = false;
            _boxes = System.Array.Empty<Bounds>();
            _boxParts = System.Array.Empty<int>();
            _boxToLocal = System.Array.Empty<Matrix4x4>();
            _renderers = System.Array.Empty<Renderer>();
            if (lit) PreparePick();   // relights the pieces as they are now
        }

        void PreparePick()
        {
            _pickReady = true;
            if (Root == null) return;
            var boxes = new List<Bounds>();
            var parts = new List<int>();
            var toLocal = new List<Matrix4x4>();
            var rendererParts = new List<int>();
            var renderers = new List<Renderer>();
            for (int i = 0; i < Root.transform.childCount; i++)
            {
                var child = Root.transform.GetChild(i);
                bool any = false;
                var box = default(Bounds);
                int partOf = _partOf.TryGetValue(child.gameObject, out int p) ? p : PartRope;
                bool terminal = partOf == PartBottom || partOf == PartTop;
                var local = terminal ? child.worldToLocalMatrix : Matrix4x4.identity;
                foreach (var r in child.GetComponentsInChildren<Renderer>())
                {
                    renderers.Add(r);
                    rendererParts.Add(partOf);
                    if (child.name == "Haul rope") continue;   // the rope picks as capsules, not its whole-line box
                    var rb = terminal ? LocalBounds(r, local) : r.bounds;
                    if (!any) { box = rb; any = true; }
                    else box.Encapsulate(rb);
                }
                if (any)
                {
                    boxes.Add(box);
                    parts.Add(partOf);
                    toLocal.Add(local);
                }
            }
            _boxes = boxes.ToArray();
            _boxParts = parts.ToArray();
            _boxToLocal = toLocal.ToArray();
            _rendererParts = rendererParts.ToArray();
            _renderers = renderers.ToArray();
            var a = new List<Vector3>();
            var b = new List<Vector3>();
            for (int i = 1; i < _loop.Length; i++)
            {
                if (a.Count > 0 && Vector3.Distance(a[a.Count - 1], _loop[i]) < RopeStep && i < _loop.Length - 1) continue;
                var from = a.Count > 0 ? b[b.Count - 1] : _loop[0];
                a.Add(from);
                b.Add(_loop[i]);
            }
            _ropeA = a.ToArray();
            _ropeB = b.ToArray();
            if (_highlighted) ApplyHighlight(true);
        }

        /// <summary>
        /// Whether <paramref name="ray"/> hits this lift before <paramref name="maxDistance"/> (the ground behind it), within
        /// <paramref name="slackPerMetre"/> × distance of a part (a few pixels' worth); <paramref name="distance"/> is how far
        /// along the ray. Allocates nothing once prepared.
        /// </summary>
        public bool Pick(Ray ray, float maxDistance, float slackPerMetre, out float distance) => Pick(ray, maxDistance, slackPerMetre, out distance, out _);

        /// <summary>As <see cref="Pick(Ray, float, float, out float)"/>, saying which part was hit (F1): a tower's index,
        /// <see cref="PartBottom"/>, <see cref="PartTop"/> or <see cref="PartRope"/> (the rope or a chair). Finding E5: a
        /// tower the ray meets wins over the rope (thin, it hides nothing: the span in front of a head used to take the click)
        /// unless a terminal stands in front of it; <paramref name="distance"/> is the nearest hit of any part.</summary>
        public bool Pick(Ray ray, float maxDistance, float slackPerMetre, out float distance, out int part)
        {
            distance = float.MaxValue;
            part = PartRope;
            if (_loop.Length == 0) return false;
            if (!_pickReady) PreparePick();
            var reach = _bounds;
            reach.Expand(Mathf.Max(20f, reach.size.y));
            if (!reach.IntersectRay(ray, out float enter) || enter > maxDistance) return false;
            float towerT = float.MaxValue, terminalT = float.MaxValue, ropeT = float.MaxValue;
            int tower = -1, terminal = PartRope;
            for (int i = 0; i < _boxes.Length; i++)
            {
                var grown = _boxes[i];
                bool oriented = _boxParts[i] == PartBottom || _boxParts[i] == PartTop;
                var r = oriented ? new Ray(_boxToLocal[i].MultiplyPoint3x4(ray.origin), _boxToLocal[i].MultiplyVector(ray.direction)) : ray;
                float slack = slackPerMetre * Vector3.Distance(r.origin, grown.center);
                grown.Expand(2 * slack);
                if (!grown.IntersectRay(r, out float t) || t >= maxDistance) continue;
                if (_boxParts[i] >= 0) { if (t < towerT) { towerT = t; tower = _boxParts[i]; } }
                else if (oriented) { if (t < terminalT) { terminalT = t; terminal = _boxParts[i]; } }
                else if (t < ropeT) ropeT = t;   // anything else spawned (footings): as the rope
            }
            var down = new Vector3(0, ChairDrop, 0);
            for (int i = 0; i < _ropeA.Length; i++)
            {
                float d = RaySegment(ray, _ropeA[i] - down, _ropeB[i] - down, out float t);
                if (t <= 0 || t >= maxDistance || t >= ropeT) continue;
                if (d <= RopeRadius + slackPerMetre * t) ropeT = t;
            }
            distance = Mathf.Min(towerT, Mathf.Min(terminalT, ropeT));
            if (distance == float.MaxValue) return false;
            // The rope in front of a tower gives way to it when the tower is just behind (the span before its head); a tower
            // far down the line behind a point on the rope near the eye doesn't take that click.
            bool towerFirst = tower >= 0 && towerT <= terminalT && towerT <= ropeT + Mathf.Max(TowerReach, 0.1f * ropeT);
            if (towerFirst) part = tower;
            else if (terminalT <= ropeT && terminalT < float.MaxValue) part = terminal;
            else part = PartRope;
            return true;
        }

        /// <summary>A renderer's bounds in another frame (<paramref name="toLocal"/>): its mesh's eight corners carried over.</summary>
        static Bounds LocalBounds(Renderer r, Matrix4x4 toLocal)
        {
            var mf = r.GetComponent<MeshFilter>();
            if (mf == null || mf.sharedMesh == null)
            {
                var c = toLocal.MultiplyPoint3x4(r.bounds.center);
                return new Bounds(c, r.bounds.size);
            }
            var b = mf.sharedMesh.bounds;
            var m = toLocal * r.localToWorldMatrix;
            var box = new Bounds(m.MultiplyPoint3x4(b.min), Vector3.zero);
            for (int k = 0; k < 8; k++)
                box.Encapsulate(m.MultiplyPoint3x4(new Vector3((k & 1) != 0 ? b.max.x : b.min.x, (k & 2) != 0 ? b.max.y : b.min.y, (k & 4) != 0 ? b.max.z : b.min.z)));
            return box;
        }

        /// <summary>The closest distance between a ray and a segment, and how far along the ray it falls.</summary>
        static float RaySegment(Ray ray, Vector3 a, Vector3 b, out float along)
        {
            var u = ray.direction;
            var v = b - a;
            var w = ray.origin - a;
            float uu = 1, uv = Vector3.Dot(u, v), vv = Vector3.Dot(v, v), uw = Vector3.Dot(u, w), vw = Vector3.Dot(v, w);
            float den = uu * vv - uv * uv;
            float s = den > 1e-9f ? Mathf.Clamp01((uu * vw - uv * uw) / den) : 0;
            along = Mathf.Max(0, uv * s - uw);
            return Vector3.Distance(ray.origin + u * along, a + v * s);
        }

        /// <summary>Lights (or unlights) the whole lift: every terminal, tower, the rope and the chairs (LT23).</summary>
        public void SetHighlight(bool on)
        {
            if (on == _highlighted) return;
            _highlighted = on;
            if (!_pickReady) PreparePick();
            ApplyHighlight(on);
        }

        /// <summary>Lights one part alone (F1: the selected tower or terminal, white as UI-15's lit lift); int.MinValue
        /// for none. Not with <see cref="SetHighlight"/> on the same view.</summary>
        public void HighlightPart(int part)
        {
            if (!_pickReady) PreparePick();
            for (int i = 0; i < _renderers.Length; i++)
            {
                var r = _renderers[i];
                if (r == null) continue;
                bool on = part != int.MinValue && _rendererParts[i] == part;
                r.renderingLayerMask = on ? r.renderingLayerMask | LiftOutline.Layer : r.renderingLayerMask & ~LiftOutline.Layer;
            }
        }

        void ApplyHighlight(bool on)
        {
            foreach (var r in _renderers)
            {
                if (r == null) continue;
                r.renderingLayerMask = on ? r.renderingLayerMask | LiftOutline.Layer : r.renderingLayerMask & ~LiftOutline.Layer;
            }
        }
    }
}
