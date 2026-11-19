using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.UI;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.15's cards on the map (the approved mockup): with no tool armed and the Snowmaking layer (or Pipe pressure) on,
    /// the part under the pointer lights (a gun, then a node or pump house, then a water line within 8 pixels) and a click
    /// opens its card in the water card's panel (<see cref="SnowCards"/>). The open card follows the design and each new
    /// solve, so its figures stay live; it picks before water, after lifts and trails.
    /// </summary>
    public sealed partial class MountainViewer
    {
        const float SnowPickPx = 8f;
        SnowId _snowHover, _snowPress;
        Vector2 _snowPressAt;
        bool _snowPressed;
        double _snowCardAtMm;
        SnowSolution _snowCardSol;
        bool _snowCardPending;
        long _snowCardRevision = -1;
        readonly List<SnowId> _snowHoverLit = new List<SnowId>(2);
        SnowId _snowLitHover, _snowLitCard;

        void UpdateSnowHover()
        {
            if (Hud == null || _resort == null) return;
            WireWaterCard();
            var cam = Camera != null ? Camera.GetComponent<UnityEngine.Camera>() : null;
            var mouse = UnityEngine.InputSystem.Mouse.current;
            var net = Design?.Document?.Snow;
            bool armed = _tools != null && _tools.Armed;
            bool picking = cam != null && mouse != null && !_scriptedRun && net != null && !net.IsEmpty && _snowView != null && _snowView.Visible
                           && !_networkOn && !PhotoOpen && !armed && _hoveredId == 0 && _hoveredTrailRow == 0;
            Vector2 pos = mouse != null ? mouse.position.ReadValue() : default;
            var hit = SnowId.None;
            double atMm = 0;
            if (picking && !PointerOverUi(pos)) hit = SnowUnder(cam, pos, net, out atMm);
            _snowHover = hit;

            // The card follows the design and each solve.
            string card = Hud.WaterCardKey;
            bool snowCard = SnowCards.TryParse(card, out var cardId);
            if (snowCard)
            {
                var s = _simNow;
                var sol = s?.SnowCheck ?? s?.Snowmaking;
                bool pending = _snowCompile != null || !_simSnowNetSent || (s?.SnowSolvePending ?? false);
                if (!ReferenceEquals(sol, _snowCardSol) || pending != _snowCardPending || (Design?.Revision ?? -1) != _snowCardRevision) ShowWaterCardFor(card);
            }
            // The light: the hovered part and the open card's, only while no tool owns it (Check's Show me does).
            if (!armed)
            {
                var c = snowCard ? cardId : SnowId.None;
                if (hit != _snowLitHover || c != _snowLitCard)
                {
                    _snowLitHover = hit;
                    _snowLitCard = c;
                    _snowHoverLit.Clear();
                    if (!hit.IsNone) _snowHoverLit.Add(hit);
                    if (!c.IsNone && c != hit) _snowHoverLit.Add(c);
                    _snowView?.Light(_snowHoverLit);
                }
            }
            else { _snowLitHover = SnowId.None; _snowLitCard = SnowId.None; }

            if (!picking) { _snowPressed = false; return; }
            if (mouse.leftButton.wasPressedThisFrame && !PointerOverUi(pos) && !SkierTookPress)
            {
                _snowPressed = !hit.IsNone;
                _snowPressAt = pos;
                _snowPress = hit;
            }
            if (!_snowPressed || !mouse.leftButton.wasReleasedThisFrame) return;
            _snowPressed = false;
            if ((pos - _snowPressAt).sqrMagnitude <= 36 && hit == _snowPress)
            {
                _snowCardAtMm = atMm;
                ShowWaterCardFor(SnowCards.Key(hit));
            }
        }

        /// <summary>The snowmaking part under a screen point: a gun or a node first, then a water line (and how far along it).</summary>
        SnowId SnowUnder(UnityEngine.Camera cam, Vector2 screen, SnowNetwork net, out double atMm)
        {
            atMm = 0;
            ulong mark = _snowView.MarkAt(cam, screen, SnowPickPx, out _);
            if (mark != 0) return new SnowId(mark);
            ulong pipe = _snowView.PipeAt(cam, screen, SnowPickPx, out _);
            if (pipe == 0) return SnowId.None;
            var id = new SnowId(pipe);
            var p = net.FindPipe(id);
            var ray = cam.ScreenPointToRay(screen);
            float along = Camera.GroundAlong(ray, 30000);
            if (p != null && !float.IsNaN(along))
            {
                var g = ray.GetPoint(along);
                atMm = SnowGeometry.Project(p.Route, PointMm.FromMetres(g.x, g.z)).ChainageMm;
            }
            return id;
        }

        /// <summary>The water card's model for a snowmaking part (the "snow:" keys).</summary>
        WaterCardModel? SnowCardFor(DesignDocument doc, ulong id)
        {
            var s = _simNow;
            var sol = s?.SnowCheck ?? s?.Snowmaking;
            bool pending = _snowCompile != null || !_simSnowNetSent || (s?.SnowSolvePending ?? false);
            _snowCardSol = sol;
            _snowCardPending = pending;
            _snowCardRevision = Design?.Revision ?? -1;
            // A solve of another network (an edit on its way) shows no figures until the new one lands.
            if (sol != null && (_snowNetBuilt == null || !ReferenceEquals(sol.Graph, _snowNetBuilt.Graph))) { sol = null; pending = true; }
            return SnowCards.For(doc.Snow, sol, new SnowId(id), _snowCardAtMm, SnowSourceName, pending);
        }

        string SnowSourceName(ulong source)
        {
            var doc = Design?.Document;
            if (doc == null) return null;
            string n = WaterPick.NameOf(doc, source);
            if (!string.IsNullOrEmpty(n)) return n;
            n = doc.FindLake(source).Name;
            return !string.IsNullOrEmpty(n) ? n : OsmLakeName(source) is string o && o.Length > 0 ? o : "Lake";
        }

        string RenameSnow(ulong id, string name)
        {
            var history = Design;
            var cmd = SnowCards.Rename(history?.Document?.Snow, history?.Revision ?? 0, new SnowId(id), name);
            if (cmd == null) return "That can't be renamed";
            var r = history.Submit(cmd);
            return r.Accepted ? null : r.Reason;
        }

        void SnowCardAction(string key, ulong id, string action)
        {
            var history = Design;
            if (history == null) return;
            var cmd = SnowCards.Action(history.Document.Snow, history.Revision, new SnowId(id), action, out string done);
            if (cmd == null) return;
            var r = history.Submit(cmd);
            if (!r.Accepted) Hud.Toast(r.Reason);
            else if (done != null) Hud.Toast(done);
            ShowWaterCardFor(key);
        }
    }
}
