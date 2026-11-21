using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using MountainPlanner.Presentation.Guests;
using MountainPlanner.Simulation;
using UnityEngine;
using UnityEngine.UIElements;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.25 (GO-13, owner 2026-11-20): the groomers on the map as amber dots, from the snapshot's <see cref="GroomView"/>
    /// (each groomer's piece now, or the pieces either side) and the face clock drawn between snapshots, so they glide.
    ///
    /// - **Working**, a dot sweeps its trail in full-length passes side by side (GO-16 v2), from the end the sim started at,
    ///   down one and up the next from the left edge looking downhill, as many as fit the trail's width at
    ///   <see cref="PassMetres"/> a pass; its share of the task says how far through the passes it is, so it keeps pace with
    ///   the sim's clock and leaves when the sim's piece ends.
    /// - **Between pieces** it drives from where it stopped to where it starts next (the depot before a shift's first and
    ///   after its last) along the trails and paths (MountainViewer.GroomerRoutes), over the sim's drive minutes (else the first <see cref="DriveMinutes"/> of the gap), then waits.
    /// - **Parked** in a row at the depot: on the run nearest the foot of the lowest built lift.
    /// - **At night** a working or driving one carries its headlights' glow. Hovering one names it and its job.
    ///
    /// The trails' sampled lines are made once per design document and trail (on first need), never per frame.
    /// </summary>
    public sealed partial class MountainViewer
    {
        /// <summary>A tiller's pass across the snow, metres (the dots' sweeps; the sim's effort is its own).</summary>
        const float PassMetres = 5.5f;
        /// <summary>Face minutes a groomer takes to drive between two places (the rest of a gap it waits).</summary>
        const int DriveMinutes = 20;
        /// <summary>The line's sample spacing, metres.</summary>
        const float GroomSampleMetres = 4f;

        sealed class GroomPath
        {
            public Vector2[] At;      // the centreline's samples (local metres, x and z)
            public Vector2[] Across;  // each sample's unit normal
            public float[] Half;      // how far a pass may sit off the centreline there, metres
            public int Passes;
        }

        GroomerLayer _groomerDots;
        bool _groomerDotsTried;
        readonly Dictionary<ulong, GroomPath> _groomPaths = new Dictionary<ulong, GroomPath>();
        object _groomPathsFor, _groomPathsGeometry;
        readonly Vector3[] _groomerShown = new Vector3[GroomerLayer.Cap];
        readonly bool[] _groomerPlaced = new bool[GroomerLayer.Cap];
        readonly int[] _groomerSlot = new int[GroomerLayer.Cap];
        int _groomerHover = -1;
        Label _groomerTip;
        string _groomerTipText, _groomerTipWanted;
        long _groomerTipKey = -1;
        Vector2 _depotAt, _depotRow = Vector2.right;
        object _depotFor;
        ulong _depotLift;
        bool _depotKnown;

        GroomerLayer EnsureGroomerDots()
        {
            if (_groomerDotsTried) return _groomerDots;
            _groomerDotsTried = true;
            if (!SystemInfo.supportsComputeShaders) return null;
            var shader = Resources.Load<Shader>(GroomerLayer.ShaderResource);
            if (shader == null) { Debug.LogWarning("[Groomers] the dot shader is missing: " + GroomerLayer.ShaderResource); return null; }
            _groomerDots = new GroomerLayer(shader);
            return _groomerDots;
        }

        /// <summary>Once a frame from LateUpdate, after the camera moved: every groomer placed and drawn.</summary>
        void TickGroomers()
        {
            var s = _simNow;
            var g = s?.Grooming;
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            if (_resort == null || TitleMode || cam == null || g == null || g.GroomerCount == 0 || _netGeometry == null || Design?.Document == null)
            {
                if (_groomerDots != null) _groomerDots.Begin();
                HideGroomerTip();
                return;
            }
            var layer = EnsureGroomerDots();
            if (layer == null) return;
            var doc = Design.Document;
            if (!ReferenceEquals(doc, _groomPathsFor) || !ReferenceEquals(_netGeometry, _groomPathsGeometry))
            {
                _groomPaths.Clear();
                _groomPathsFor = doc; _groomPathsGeometry = _netGeometry;
            }
            FindDepot(doc, g.DepotLift);
            double now = (s.FaceMs - _sunClock.BehindMs) / 60_000.0;
            float night = Lighting != null ? SceneLighting.NightWeight((float)Lighting.CurrentLight.SunElevation) : 0;
            float follow = 1 - Mathf.Exp(-Mathf.Min(0.25f, Time.unscaledDeltaTime) / 0.3f);
            layer.Begin();
            layer.UiScale = MountainPlanner.UI.UiPreferences.Scale;
            layer.Visible = !_riding && !PhotoOpen;
            layer.ExposureEv = Lighting != null ? Lighting.Applied.Exposure : 0;
            int n = Math.Min(g.GroomerCount, GroomerLayer.Cap), parked = 0;
            for (int i = 0; i < n; i++)
            {
                bool busy = GroomerTarget(s, g, i, now, ref parked, out var target);
                if (!_groomerPlaced[i] || (target - _groomerShown[i]).sqrMagnitude > 400f * 400f) _groomerShown[i] = target;   // a far jump (a skip): no glide
                else _groomerShown[i] = Vector3.Lerp(_groomerShown[i], target, follow);
                _groomerPlaced[i] = true;
                _groomerSlot[i] = layer.Count;
                layer.Add(_groomerShown[i], i == _groomerHover, busy ? night : 0);
            }
            layer.Draw(cam);
            UpdateGroomerHover(g, n);
        }

        /// <summary>Where groomer <paramref name="i"/> stands now (on the snow); true while it works or drives.</summary>
        bool GroomerTarget(SimSnapshot s, GroomView g, int i, double now, ref int parked, out Vector3 at)
        {
            int ta = g.AtTrailA[i], tb = g.AtTrailB[i];
            if (ta < 0 && tb < 0 && g.AtMinA[i] == 0 && g.AtMinB[i] == 0) { at = Depot(parked++); return false; }
            if (g.AtJob[i] != GroomJob.None && ta >= 0 && ta == tb)
            {
                double span = Math.Max(1e-3, g.AtMinB[i] - g.AtMinA[i]);
                double u = Math.Max(0, Math.Min(1, (now - g.AtMinA[i]) / span));
                double share = (g.AtPpmA[i] + (g.AtPpmB[i] - g.AtPpmA[i]) * u) / 1_000_000.0;
                if (OnPasses(s, ta, share, (g.AtTail[i] & 1) != 0, out var p)) { at = Stand(p); return true; }
                at = Depot(parked++); return false;
            }
            // between pieces: drive from A to B in the gap's first minutes, then wait at B
            Vector2 a = ta >= 0 && OnPasses(s, ta, g.AtPpmA[i] / 1_000_000.0, (g.AtTail[i] & 1) != 0, out var pa) ? pa : DepotPoint(i);
            Vector2 b = tb >= 0 && OnPasses(s, tb, g.AtPpmB[i] / 1_000_000.0, (g.AtTail[i] & 2) != 0, out var pb) ? pb : DepotPoint(i);
            double drive = Math.Max(1, Math.Min(g.AtDrive[i] > 0 ? g.AtDrive[i] : DriveMinutes, g.AtMinB[i] - g.AtMinA[i]));
            double t = Math.Max(0, Math.Min(1, (now - g.AtMinA[i]) / drive));
            bool moving = t > 0 && t < 1;
            if (ta < 0 && tb < 0) { at = Depot(parked++); return false; }
            if (ta < 0 && t <= 0) { at = Depot(parked++); return false; }   // still at the depot before the shift
            if (tb < 0 && t >= 1) { at = Depot(parked++); return false; }   // back at the depot after it
            // round 3: over the trails and paths, as the sim drives (a straight line where there's no route)
            long key = unchecked(((long)g.AtMinA[i] * 1_000_003L + g.AtMinB[i]) * 31L + ta * 7919L + tb * 104729L + g.AtPpmA[i] * 13L + g.AtPpmB[i] * 17L + g.AtTail[i]);
            at = Stand(DriveAlong(i, key, a, b, (float)t));
            return moving;
        }

        /// <summary>The point <paramref name="share"/> of the way through trail <paramref name="k"/>'s passes (the snapshot's order),
        /// the first pass at the left edge looking downhill (the sim's order) and from the tail when <paramref name="fromTail"/>.</summary>
        bool OnPasses(SimSnapshot s, int k, double share, bool fromTail, out Vector2 at)
        {
            at = default;
            if (k < 0 || k >= s.TrailCount) return false;
            var path = PathOf(s.TrailIds[k]);
            if (path == null || path.At.Length == 0) return false;
            int passes = path.Passes;
            double f = Math.Max(0, Math.Min(0.999999, share)) * passes;
            int pass = (int)f;
            double u = f - pass;
            if ((pass & 1) == 1) u = 1 - u;   // back up the next pass
            if (fromTail) u = 1 - u;
            double x = u * (path.At.Length - 1);
            int j = Math.Min(path.At.Length - 2, (int)x);
            if (j < 0) { at = path.At[0]; return true; }
            float w = (float)(x - j);
            var c = Vector2.Lerp(path.At[j], path.At[j + 1], w);
            var nrm = Vector2.Lerp(path.Across[j], path.Across[j + 1], w);
            float half = Mathf.Lerp(path.Half[j], path.Half[j + 1], w);
            float side = passes <= 1 ? 0 : 1f - (2f * pass + 1f) / passes;   // the normal points left: the left edge first
            at = c + nrm * (side * half);
            return true;
        }

        GroomPath PathOf(ulong id)
        {
            if (_groomPaths.TryGetValue(id, out var path)) return path;
            path = BuildGroomPath(id);
            _groomPaths[id] = path;   // null too: asked once per design
            return path;
        }

        GroomPath BuildGroomPath(ulong id)
        {
            var trail = Design.Document.FindTrail(new TrailId(id));
            if (trail == null) return null;
            int length = _netGeometry.CentrelineLengthMm(trail);
            if (length <= 0) return null;
            int n = Math.Max(2, (int)Math.Ceiling(length / 1000f / GroomSampleMetres) + 1);
            var path = new GroomPath { At = new Vector2[n], Across = new Vector2[n], Half = new float[n] };
            for (int i = 0; i < n; i++) path.At[i] = DesignFrame.ToLocal(_netGeometry.PointAlong(trail, (int)((long)length * i / (n - 1))));
            var footprint = (_edits?.Earthwork?.DetailOf(id) as TrailBuild)?.Footprint;
            double halfSum = 0;
            for (int i = 0; i < n; i++)
            {
                var d = path.At[Math.Min(n - 1, i + 1)] - path.At[Math.Max(0, i - 1)];
                path.Across[i] = d.sqrMagnitude > 1e-6f ? new Vector2(-d.y, d.x).normalized : Vector2.right;
                float half = 4f;
                if (footprint != null)
                {
                    footprint.Lattice.CellAt(footprint.OriginX + path.At[i].x, footprint.OriginY + path.At[i].y, out int c, out int r);
                    half = (float)footprint.Clearance(c, r);
                }
                path.Half[i] = Mathf.Max(0, half - PassMetres * 0.5f);   // a pass's middle keeps half a tiller off the edge
                halfSum += half;
            }
            double width = 2 * halfSum / n;
            path.Passes = Math.Max(1, Math.Min(12, (int)Math.Round(width / PassMetres)));
            return path;
        }

        /// <summary>The depot: on the run nearest the foot of the sim's depot lift (GO-15), else of the lowest built lift (once
        /// per document and depot).</summary>
        void FindDepot(DesignDocument doc, ulong depotLift)
        {
            if (ReferenceEquals(doc, _depotFor) && depotLift == _depotLift) return;
            _depotFor = doc;
            _depotLift = depotLift;
            _depotKnown = false;
            float best = float.MaxValue;
            foreach (var lift in doc.Lifts)
            {
                if (lift.Stage != WorkStage.Built) continue;
                if (depotLift != 0 && lift.Id.Value != depotLift) continue;
                var bottom = DesignFrame.ToLocal(lift.Bottom);
                float h = _resort.Surface != null ? _resort.Surface.HeightAt(bottom.x, bottom.y) : 0;
                if (float.IsNaN(h) || h >= best) continue;
                best = h;
                _depotAt = bottom;
                _depotKnown = true;
            }
            // on the snow: the built run's line nearest that foot (trails come down to their lifts), the row across it
            var foot = _depotKnown ? _depotAt : _resort.Core.center;
            float near = float.MaxValue;
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built || t.Settings.Kind != TrailKind.Run) continue;
                var path = PathOf(t.Id.Value);
                if (path == null) continue;
                for (int i = 0; i < path.At.Length; i++)
                {
                    float d = (path.At[i] - foot).sqrMagnitude;
                    if (d >= near) continue;
                    near = d;
                    _depotAt = path.At[i];
                    _depotRow = path.Across[i];
                    _depotKnown = true;
                }
            }
            if (near < float.MaxValue)
            {
                _depotAt -= _depotRow * 7f;   // the row of three centred on the line
                return;
            }
            if (_depotKnown) return;
            var core = _resort.Core;
            _depotAt = core.center;
            _depotRow = Vector2.right;
            _depotKnown = true;
        }

        Vector2 DepotPoint(int slot) => _depotAt + _depotRow * (slot * 7f);

        Vector3 Depot(int slot) => Stand(DepotPoint(slot));

        /// <summary>The snow's top at a local point: the ground plus the snow shell as drawn.</summary>
        Vector3 Stand(Vector2 p)
        {
            float h = _resort.Surface != null ? _resort.Surface.HeightAt(p.x, p.y) : 0;
            if (float.IsNaN(h)) h = 0;
            var states = _resort.States;
            if (states != null && states.Lift != null)
            {
                var field = states.Snow;
                float cell = field.CellMetres;
                int i = Mathf.FloorToInt((p.x - _resort.Ring.xMin) / cell), j = Mathf.FloorToInt((p.y - _resort.Ring.yMin) / cell);
                if (i >= 0 && j >= 0 && i < field.Width && j < field.Height && j * field.Width + i < states.Lift.Length)
                    h += states.Lift[j * field.Width + i] * states.LiftScale;
            }
            return new Vector3(p.x, h, p.y);
        }

        // ---------- hover ----------

        void UpdateGroomerHover(GroomView g, int n)
        {
            _groomerHover = -1;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var layer = _groomerDots;
            bool can = layer != null && layer.Visible && mouse != null && !_scriptedRun && !PhotoOpen && !_riding && (_tools == null || !_tools.Armed);
            int found = -1;
            if (can)
            {
                var pos = mouse.position.ReadValue();
                if (!PointerOverUi(pos)) found = layer.Pick(pos);
            }
            if (layer != null && _groomerForceHover >= 0 && _groomerForceHover < n) found = _groomerSlot[_groomerForceHover];   // a capture's
            int groomer = -1;
            for (int i = 0; i < n; i++) if (_groomerSlot[i] == found) { groomer = i; break; }
            if (found < 0 || groomer < 0) { HideGroomerTip(); return; }
            _groomerHover = groomer;
            // the text is made only when what it says changes (no string a frame while hovering)
            long key = ((long)groomer << 40) | ((long)(byte)g.AtJob[groomer] << 32) | ((long)(ushort)g.AtTrailA[groomer] << 16) | (ushort)g.AtTrailB[groomer];
            if (key != _groomerTipKey || _groomerTipText == null) { _groomerTipKey = key; _groomerTipWanted = GroomerText(g, groomer); }
            ShowGroomerTip(_groomerTipWanted, layer.ScreenOf(found));
        }

        string GroomerText(GroomView g, int i)
        {
            string model = g.Model[i] switch
            {
                GroomerModel.S400 => "Skogkatt 400",
                GroomerModel.S400W => "Skogkatt 400 W",
                GroomerModel.S800 => "Skogkatt 800",
                GroomerModel.S800W => "Skogkatt 800 W",
                _ => "Groomer",
            };
            string name = $"Groomer {g.Number[i]} · {model}";
            if (g.AtJob[i] != GroomJob.None && g.AtTrailA[i] >= 0)
                return $"{name}\n{(g.AtJob[i] == GroomJob.Push ? "Pushing" : "Tilling")} {TrailNameAt(g.AtTrailA[i])}";
            if (g.AtTrailA[i] < 0 && g.AtTrailB[i] < 0) return $"{name}\nParked";
            return $"{name}\nTo {TrailNameAt(g.AtTrailB[i])}";
        }

        /// <summary>Snapshot trail <paramref name="k"/>'s name (−1: the depot).</summary>
        string TrailNameAt(int k)
        {
            var s = _simNow;
            if (k < 0 || s == null || k >= s.TrailCount) return "the depot";
            return Design?.Document?.FindTrail(new TrailId(s.TrailIds[k]))?.Name ?? "a trail";
        }

        void ShowGroomerTip(string text, Vector3 screen)
        {
            var layer = Hud?.ContourLabelLayer;
            if (layer == null || layer.panel == null) return;
            if (_groomerTip == null)
            {
                _groomerTip = new Label { name = "groomer-tip", pickingMode = PickingMode.Ignore };
                _groomerTip.AddToClassList("landmark-label");
                _groomerTip.style.width = StyleKeyword.Auto;
                _groomerTip.style.height = StyleKeyword.Auto;
                _groomerTip.style.whiteSpace = WhiteSpace.NoWrap;
                _groomerTip.style.unityTextAlign = TextAnchor.LowerLeft;
                _groomerTip.style.fontSize = 14;
                layer.parent.Add(_groomerTip);
            }
            if (_groomerTipText != text) { _groomerTip.text = text; _groomerTipText = text; }
            var local = layer.parent.WorldToLocal(RuntimePanelUtils.ScreenToPanel(layer.panel, new Vector2(screen.x, Screen.height - screen.y)));
            _groomerTip.style.left = local.x + 12;
            _groomerTip.style.top = local.y - 44;
            _groomerTip.style.display = DisplayStyle.Flex;
        }

        void HideGroomerTip()
        {
            if (_groomerTip != null && _groomerTip.style.display != DisplayStyle.None) _groomerTip.style.display = DisplayStyle.None;
        }
    }
}
