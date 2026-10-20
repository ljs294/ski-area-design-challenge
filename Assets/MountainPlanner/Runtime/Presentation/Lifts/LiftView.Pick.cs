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
        Vector3[] _ropeA = System.Array.Empty<Vector3>(), _ropeB = System.Array.Empty<Vector3>();
        Renderer[] _renderers = System.Array.Empty<Renderer>();
        bool _pickReady, _highlighted;

        /// <summary>The rope's capsules: centred this far below it, this wide, so the chairs under it count.</summary>
        const float ChairDrop = 1.4f, RopeRadius = 1.8f, RopeStep = 5f;

        public bool Highlighted => _highlighted;

        /// <summary>A point on the rope a fraction of the way round its loop, with a chair's drop (captures and tests).</summary>
        public Vector3 RopePoint(float fraction) =>
            _loop.Length == 0 ? Vector3.zero : _loop[Mathf.Clamp((int)(fraction * (_loop.Length - 1)), 0, _loop.Length - 1)] - new Vector3(0, 1, 0);

        void ForgetPick()
        {
            _pickReady = false;
            _highlighted = false;
            _boxes = System.Array.Empty<Bounds>();
            _renderers = System.Array.Empty<Renderer>();
        }

        void PreparePick()
        {
            _pickReady = true;
            if (Root == null) return;
            var boxes = new List<Bounds>();
            var renderers = new List<Renderer>();
            for (int i = 0; i < Root.transform.childCount; i++)
            {
                var child = Root.transform.GetChild(i);
                bool any = false;
                var box = default(Bounds);
                foreach (var r in child.GetComponentsInChildren<Renderer>())
                {
                    renderers.Add(r);
                    if (child.name == "Haul rope") continue;   // the rope picks as capsules, not its whole-line box
                    if (!any) { box = r.bounds; any = true; }
                    else box.Encapsulate(r.bounds);
                }
                if (any) boxes.Add(box);
            }
            _boxes = boxes.ToArray();
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
        public bool Pick(Ray ray, float maxDistance, float slackPerMetre, out float distance)
        {
            distance = float.MaxValue;
            if (_loop.Length == 0) return false;
            if (!_pickReady) PreparePick();
            var reach = _bounds;
            reach.Expand(Mathf.Max(20f, reach.size.y));
            if (!reach.IntersectRay(ray, out float enter) || enter > maxDistance) return false;
            bool hit = false;
            foreach (var box in _boxes)
            {
                var grown = box;
                float slack = slackPerMetre * Vector3.Distance(ray.origin, box.center);
                grown.Expand(2 * slack);
                if (grown.IntersectRay(ray, out float t) && t < maxDistance && t < distance) { distance = t; hit = true; }
            }
            var down = new Vector3(0, ChairDrop, 0);
            for (int i = 0; i < _ropeA.Length; i++)
            {
                float d = RaySegment(ray, _ropeA[i] - down, _ropeB[i] - down, out float t);
                if (t <= 0 || t >= maxDistance || t >= distance) continue;
                if (d <= RopeRadius + slackPerMetre * t) { distance = t; hit = true; }
            }
            return hit;
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
