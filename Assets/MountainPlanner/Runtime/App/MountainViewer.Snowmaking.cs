using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Snowmaking;
using MountainPlanner.Domain.Water;
using MountainPlanner.Simulation;
using MountainPlanner.UI.Tools;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.15's snowmaking network in the viewer (HY-01, HY-08):
    /// - **To the game.** The design's network compiled on the edited ground (buried, sampled), off the main thread whenever
    ///   the network, the ground or the sources move, and sent as <see cref="SimCommandKind.SetSnowNetwork"/>. A network that
    ///   fills a pond holds the clock until it lands, so a reopened save moves the same water from the first hour (SI-22).
    /// - **To the tools.** A <see cref="SnowService"/> on the same ground, water and weather, so a draft solves as the game
    ///   would; the game's last solve; the source nearest a point (WS-05, WS-11).
    /// </summary>
    public sealed partial class MountainViewer
    {
        SnowService _snowService;
        object _snowServiceGround;
        ulong _snowServiceSources;
        long _snowServiceHour = long.MinValue;
        SnowNetwork _snowNetFor;
        SnowService _snowNetService;
        Task<SimSnowNetwork> _snowCompile;
        SnowNetwork _snowCompiling;
        SimSnowNetwork _snowNetBuilt;
        bool _simSnowNetSent = true, _simSnowNetCleared = true;
        bool _snowCheckOn;

        /// <summary>The compiled network the map draws, or null.</summary>
        internal SimSnowNetwork SnowNetBuilt => _snowNetBuilt;

        void HookSnow(ToolContext c)
        {
            c.Snow = () => SnowServiceNow();
            c.LiveSnow = () => _simNow?.Snowmaking;
            c.LiveSnowCheck = () => _simNow?.SnowCheck;
            c.SnowPending = () => _snowCompile != null || (_simNow?.SnowSolvePending ?? false) || !_simSnowNetSent;
            c.SetSnowCheck = SetSnowCheck;
            c.SourceNear = SnowSourceNear;
            c.ShowSnow = ShowSnowPieces;
        }

        bool DesignHasSnow => Design?.Document?.Snow is SnowNetwork n && !n.IsEmpty;

        /// <summary>A network with an outlet moves water each face hour (WS-11): the clock waits for it.</summary>
        bool SnowNetMissing
        {
            get
            {
                if (_simSnowNetSent || _resort?.WaterBodies == null) return false;
                var net = Design?.Document?.Snow;
                if (net == null) return false;
                foreach (var n in net.Nodes) if (n.Kind == SnowNodeKind.Outlet) return true;
                return false;
            }
        }

        /// <summary>The tools' solver: the edited ground once the edit session is up, the sources' full pools, this hour's air.</summary>
        SnowService SnowServiceNow()
        {
            var host = Ground;
            host.Start();
            if (!host.Ready || host.Heights == null || host.Earthwork == null) return null;
            var heights = host.Heights;
            ulong sources = _waterSources?.Hash ?? 0;
            if (_snowService == null || !ReferenceEquals(_snowServiceGround, heights) || _snowServiceSources != sources)
            {
                var src = _waterSources;
                Func<ulong, int?> level = id =>
                {
                    int i = src != null ? src.IndexOf(id) : -1;
                    return i >= 0 ? src[i].FullPoolMm : (int?)null;
                };
                _snowService = new SnowService(new SnowGround(heights, host.Earthwork.Site), level);
                _snowServiceGround = heights;
                _snowServiceSources = sources;
                _snowServiceHour = long.MinValue;
            }
            var s = _simNow;
            long hour = s != null && s.WeatherKnown ? s.WeatherHour.HourOfYear + 10_000L * s.WeatherHour.LabelYear : -1;
            if (hour != _snowServiceHour)
            {
                _snowServiceHour = hour;
                _snowService = hour < 0 ? _snowService.WithWeather(null, 0, null)
                                        : _snowService.WithWeather(s.WeatherHour, _simWeather?.Rows.ZrefFeet ?? 0, _resort.WeatherTerrain);
            }
            return _snowService;
        }

        /// <summary>Once a frame from the clock: compiles a moved network off the main thread and sends it (or its clearing).</summary>
        void UpdateSnowNetwork()
        {
            if (_sim == null) return;
            var net = Design?.Document?.Snow;
            if (net == null || net.IsEmpty)
            {
                _snowNetFor = null;
                _snowCompile = null;
                _snowNetBuilt = null;
                if (!_simSnowNetCleared) _simSnowNetCleared = _sim.Send(SimCommandKind.SetSnowNetwork, 0, null);
                _simSnowNetSent = true;
                return;
            }
            if (_snowCompile != null)
            {
                if (!_snowCompile.IsCompleted) return;
                if (_snowCompile.IsFaulted) Debug.LogException(_snowCompile.Exception);
                else if (ReferenceEquals(_snowCompiling, net)) { _snowNetBuilt = _snowCompile.Result; _simSnowNetSent = false; }
                _snowCompile = null;
            }
            if (_snowNetBuilt != null && !_simSnowNetSent && _simSourcesSent)
            {
                _simSnowNetSent = _sim.Send(SimCommandKind.SetSnowNetwork, 0, _snowNetBuilt);
                if (_simSnowNetSent) _simSnowNetCleared = false;
            }
            var service = SnowServiceNow();
            if (service == null) return;
            // A new network, ground or water: compile again (the weather alone doesn't move the graph).
            if (_snowCompile == null && (!ReferenceEquals(net, _snowNetFor) || !ReferenceEquals(_snowNetService?.Ground, service.Ground) || SourcesMoved(service)))
            {
                _snowNetFor = net;
                _snowNetService = service;
                _snowCompiling = net;
                _snowCompile = Task.Run(() => new SimSnowNetwork(service.Compile(net)));
            }
        }

        ulong _snowCompiledSources;
        bool SourcesMoved(SnowService service)
        {
            ulong now = _waterSources?.Hash ?? 0;
            if (now == _snowCompiledSources) return false;
            _snowCompiledSources = now;
            return true;
        }

        /// <summary>After the worker restarts (a load, a new area) everything goes again.</summary>
        void ResetSnowNetworkSend()
        {
            _simSnowNetSent = _snowNetBuilt == null;
            _simSnowNetCleared = true;
            _snowNetFor = null;
            _snowCheckOn = false;   // a new worker starts with the check off; the Check tool turns it on again
        }

        void SetSnowCheck(bool on)
        {
            if (on == _snowCheckOn) return;
            _snowCheckOn = on;
            _sim?.Send(SimCommandKind.CheckNetwork, on ? 1 : 0);
        }

        /// <summary>WS-05, WS-11: the source (a used lake, a pond or a dam) whose water is nearest <paramref name="p"/> within
        /// <paramref name="reachMm"/>, by the body table's 8 m cells.</summary>
        SourceNear? SnowSourceNear(PointMm p, int reachMm)
        {
            var t = _resort?.WaterBodies;
            var src = _waterSources;
            if (t == null || src == null || src.Count == 0) return null;
            const int Cell = WaterBodyTable.CellMetres;
            int n = (int)Math.Ceiling(reachMm / 1000.0 / Cell) + 1;
            long ci = FloorDiv(p.X - (long)t.OriginXMm, Cell * 1000L), cj = FloorDiv(p.Y - (long)t.OriginYMm, Cell * 1000L);
            double best = double.PositiveInfinity;
            int bestBody = -1;
            PointMm bestAt = p;
            for (long j = cj - n; j <= cj + n; j++)
                for (long i = ci - n; i <= ci + n; i++)
                {
                    if (i < 0 || j < 0 || i >= t.Width || j >= t.Depth) continue;
                    int body = t.BodyAt((int)(j * t.Width + i));
                    if (body <= WaterBodyTable.Streams || src.IndexOf(t[body].Id) < 0) continue;
                    // The cell's point nearest p (0 inside it).
                    double x0 = t.OriginXMm + i * Cell * 1000.0, y0 = t.OriginYMm + j * Cell * 1000.0;
                    double qx = Math.Max(x0, Math.Min(x0 + Cell * 1000.0, p.X)), qy = Math.Max(y0, Math.Min(y0 + Cell * 1000.0, p.Y));
                    double d = Math.Sqrt((qx - p.X) * (qx - p.X) + (qy - p.Y) * (qy - p.Y));
                    if (d < best || d == best && body < bestBody)
                    {
                        best = d; bestBody = body;
                        bestAt = new PointMm((int)Math.Round(qx), (int)Math.Round(qy));
                    }
                }
            if (bestBody < 0 || best > reachMm) return null;
            var b = t[bestBody];
            var doc = Design?.Document;
            bool fillable = b.Kind != WaterBodyKind.Lake;
            string name = fillable ? (doc != null ? WaterPick.NameOf(doc, b.Id) : null)
                                   : (doc?.FindLake(b.Id).Name is string l && l.Length > 0 ? l : OsmLakeName(b.Id));
            if (string.IsNullOrEmpty(name)) name = fillable ? "Pond" : "Lake";
            return new SourceNear(b.Id, name, bestAt, best / 1000.0, fillable);
        }

        readonly List<SnowId> _snowLit = new List<SnowId>();
        void ShowSnowPieces(IReadOnlyList<SnowId> ids, PointMm? flyTo)
        {
            _snowLit.Clear();
            if (ids != null) _snowLit.AddRange(ids);
            _snowView?.Light(_snowLit);
            if (flyTo.HasValue && Camera != null && _resort != null)
            {
                float x = (float)flyTo.Value.XMetres, z = (float)flyTo.Value.YMetres;
                float y = _resort.Surface.HeightAt(x, z);
                Camera.Frame(new Vector3(x, float.IsNaN(y) ? 0 : y, z), 450f);   // "Show me": about 1,500 ft out
            }
        }
    }
}
