using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Snowmaking;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.15: the snowmaking network on the map (<see cref="SnowNetworkView"/>), shown with the Snowmaking map layer or the
    /// Pipe pressure info layer. It draws the design's network at once and colours it from the game's last solve (the
    /// "every gun" check's while the Check tool has it on), greyed while a newer edit is still being solved. Rebuilt only when
    /// the network, the solve, the layers or the ground move (at most a few times a second), never per frame.
    /// </summary>
    public sealed partial class MountainViewer
    {
        SnowNetworkView _snowView;
        SnowNetwork _snowViewNet;
        SnowSolution _snowViewSol;
        bool _snowViewPressure;
        object _snowViewGround;
        const int ProfileStepMm = 10_000;

        void TickSnowView(Camera cam)
        {
            if (_resort == null || _layers == null) return;
            var net = Design?.Document?.Snow;
            bool any = net != null && !net.IsEmpty;
            bool pressure = _layers.Info == MapLayers.InfoView.Pressure;
            bool shown = any && (_layers.SnowmakingOn || pressure);
            if (!shown)
            {
                if (_snowView != null) _snowView.Visible = false;
                return;
            }
            _snowView ??= new SnowNetworkView(_resort.Root.transform);
            _snowView.Visible = true;
            var s = _simNow;
            var sol = s?.SnowCheck ?? s?.Snowmaking;
            // A solve of another network (an edit still on its way) colours nothing: the shape shows greyed.
            if (sol != null && (_snowNetBuilt == null || !ReferenceEquals(sol.Graph, _snowNetBuilt.Graph))) sol = null;
            bool stale = _snowCompile != null || !_simSnowNetSent || (s?.SnowSolvePending ?? false) || (sol == null && pressure);
            _snowView.Stale = stale && pressure;
            _snowView.Pressure = pressure;
            var ground = _snowService?.Ground;
            if (!ReferenceEquals(net, _snowViewNet) || !ReferenceEquals(sol, _snowViewSol) || pressure != _snowViewPressure || !ReferenceEquals(ground, _snowViewGround))
            {
                // Keep the last colours until the new solve lands, so an edit greys the map instead of blanking it.
                if (sol != null || !ReferenceEquals(net, _snowViewNet)) BuildSnowView(net, sol ?? (ReferenceEquals(net, _snowViewNet) ? _snowViewSol : null), ground);
                _snowViewNet = net;
                if (sol != null) _snowViewSol = sol;
                _snowViewPressure = pressure;
                _snowViewGround = ground;
            }
            _snowView.Follow(cam);
        }

        readonly List<double> _profileAt = new List<double>();

        void BuildSnowView(SnowNetwork net, SnowSolution sol, IGroundHeights ground)
        {
            var pipes = new SnowNetworkView.Pipe[net.Pipes.Count];
            for (int i = 0; i < pipes.Length; i++)
            {
                var p = net.Pipes[i];
                _profileAt.Clear();
                double len = SnowGeometry.Length(p.Route), run = 0;
                // The route's own points and one every 10 m between them, so the colours follow the ground.
                for (int k = 0; k < p.Route.Count; k++)
                {
                    if (k > 0)
                    {
                        double seg = Math.Sqrt(p.Route[k - 1].DistanceSquared(p.Route[k]));
                        int steps = (int)Math.Ceiling(seg / ProfileStepMm);
                        for (int j = 1; j < steps; j++) _profileAt.Add(run + seg * j / steps);
                        run += seg;
                    }
                    _profileAt.Add(Math.Min(run, len));
                }
                var pts = new Vector3[_profileAt.Count];
                for (int k = 0; k < pts.Length; k++) pts[k] = SnowOnGround(SnowGeometry.At(p.Route, _profileAt[k]));
                var psi = new float[pts.Length];
                if (sol != null && ground != null) SnowProfile.PressureAlong(sol, p, ground, _profileAt, psi);
                else for (int k = 0; k < psi.Length; k++) psi[k] = float.NaN;
                pipes[i] = new SnowNetworkView.Pipe { Id = p.Id.Value, Points = pts, Psi = psi, DiameterIn = p.DiameterIn };
            }
            var ends = new Dictionary<SnowId, int>();
            foreach (var p in net.Pipes)
            {
                ends[p.From] = ends.TryGetValue(p.From, out int a) ? a + 1 : 1;
                ends[p.To] = ends.TryGetValue(p.To, out int b) ? b + 1 : 1;
            }
            var marks = new List<SnowNetworkView.Mark>(net.Nodes.Count + net.Guns.Count + net.Houses.Count);
            foreach (var h in net.Houses)
            {
                float yaw = h.BearingCentideg / 100f * Mathf.Deg2Rad;
                marks.Add(new SnowNetworkView.Mark { Id = h.Id.Value, Kind = SnowNetworkView.MarkKind.House, At = SnowOnGround(h.Centre), Dir = new Vector2(Mathf.Sin(yaw), Mathf.Cos(yaw)) });
            }
            foreach (var n in net.Nodes)
            {
                SnowNetworkView.MarkKind kind;
                var dir = Vector2.zero;
                switch (n.Kind)
                {
                    case SnowNodeKind.Hydrant: kind = SnowNetworkView.MarkKind.Hydrant; break;
                    case SnowNodeKind.Intake: kind = SnowNetworkView.MarkKind.Intake; break;
                    case SnowNodeKind.Outlet: kind = SnowNetworkView.MarkKind.Outlet; break;
                    case SnowNodeKind.Pump:
                        kind = SnowNetworkView.MarkKind.Pump;
                        dir = PumpDirection(net, n);
                        break;
                    case SnowNodeKind.House: continue;   // drawn as its house
                    default:
                        // A junction where only two pipes meet end to end is just a bend in the line: no ring.
                        if (!n.IsTap && ends.TryGetValue(n.Id, out int meet) && meet == 2) continue;
                        kind = SnowNetworkView.MarkKind.Junction;
                        break;
                }
                marks.Add(new SnowNetworkView.Mark { Id = n.Id.Value, Kind = kind, At = SnowOnGround(n.At), Dir = dir });
            }
            foreach (var gun in net.Guns)
            {
                var hydrant = gun.Hydrant != SnowId.None ? net.FindNode(gun.Hydrant) : null;
                var look = SnowNetworkView.GunLook.Idle;
                int gi = sol != null ? sol.GunIndex(gun.Id) : -1;
                if (gi >= 0)
                {
                    var st = sol.GunStates[gi];
                    look = st == GunState.Running || st == GunState.RunningWarm ? SnowNetworkView.GunLook.Running
                         : st == GunState.ShortOfPressure || st == GunState.ShortOfAir || st == GunState.NoWater && gun.Override == GunOverride.On ? SnowNetworkView.GunLook.Short
                         : SnowNetworkView.GunLook.Idle;
                }
                marks.Add(new SnowNetworkView.Mark
                {
                    Id = gun.Id.Value, Kind = SnowNetworkView.MarkKind.Gun, At = SnowOnGround(gun.At),
                    Hooked = hydrant != null, Hydrant = hydrant != null ? SnowOnGround(hydrant.At) : default, Look = look,
                });
            }
            _snowView.Set(pipes, marks.ToArray());
        }

        /// <summary>The way an inline pump pushes, on the ground: along its pipe's route, or against it.</summary>
        static Vector2 PumpDirection(SnowNetwork net, SnowNode pump)
        {
            var host = net.FindPipe(pump.Host);
            if (host == null || host.Route.Count < 2) return Vector2.up;
            var on = SnowGeometry.Project(host.Route, pump.At);
            var a = host.Route[on.Segment];
            var b = host.Route[Math.Min(on.Segment + 1, host.Route.Count - 1)];
            var d = new Vector2(b.X - a.X, b.Y - a.Y).normalized;
            return pump.Forward ? d : -d;
        }

        Vector3 SnowOnGround(PointMm p)
        {
            var v = OnGround(new Vector2((float)p.XMetres, (float)p.YMetres));   // the top of the snow
            if (float.IsNaN(v.y)) v.y = 0;
            v.y += 0.3f;
            return v;
        }

        void DisposeSnowView()
        {
            _snowView?.Dispose();
            _snowView = null;
            _snowViewNet = null;
            _snowViewSol = null;
        }
    }
}
