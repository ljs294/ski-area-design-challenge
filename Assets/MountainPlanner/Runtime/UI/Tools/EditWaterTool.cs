using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Presentation;
using MountainPlanner.Presentation.Design;
using UnityEngine;

namespace MountainPlanner.UI.Tools
{
    /// <summary>Task 4.14: the pond or dam under a design point: inside a pond's shoreline, within 30 ft of a dam's line, or
    /// on a reservoir's water (the fold's wet points). Ponds first, then dams, in ID order.</summary>
    public static class WaterPick
    {
        public const int DamReachMm = 9144;

        public static ulong Under(DesignDocument doc, IEarthworkHost host, PointMm p)
        {
            foreach (var pond in doc.Ponds)
                if (Inside(pond.Ring, p)) return pond.Id.Value;
            foreach (var dam in doc.Dams)
            {
                double d2 = RingDistance.SegmentDistanceSquared(p.XMetres, p.YMetres, dam.BankA.XMetres, dam.BankA.YMetres, dam.BankB.XMetres, dam.BankB.YMetres);
                if (d2 * 1e6 <= (double)DamReachMm * DamReachMm) return dam.Id.Value;
            }
            if (host == null || !host.Ready) return 0;
            var site = host.Earthwork.Site;
            site.PointOf(p, out double x, out double y);
            site.Lattice.CellAt(x, y, out int c, out int r);
            foreach (var dam in doc.Dams)
                if (host.Earthwork.DetailOf(dam.Id.Value) is DamBuild b && b.Wet != null && b.Wet.Has(c, r)) return dam.Id.Value;
            return 0;
        }

        // Even-odd, in whole mm, allocating nothing (a tool's tick, CF-09).
        static bool Inside(System.Collections.Generic.IReadOnlyList<PointMm> ring, PointMm p)
        {
            bool inside = false;
            for (int i = 0, j = ring.Count - 1; i < ring.Count; j = i++)
            {
                var a = ring[i];
                var b = ring[j];
                if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < (double)(b.X - a.X) * (p.Y - a.Y) / ((double)b.Y - a.Y) + a.X) inside = !inside;
            }
            return inside;
        }

        public static string NameOf(DesignDocument doc, ulong id) =>
            doc.FindPond(new PondId(id))?.Name ?? doc.FindDam(new DamId(id))?.Name;

        /// <summary>Lights a pond's shoreline or a dam's line in <paramref name="ink"/> from preview slot <paramref name="slot"/>.</summary>
        public static void Draw(IToolPreview preview, DesignDocument doc, ulong id, int slot, PreviewInk ink, ref Vector2[] xz)
        {
            if (doc.FindPond(new PondId(id)) is Pond pond)
            {
                if (xz.Length < pond.Ring.Count) xz = new Vector2[pond.Ring.Count * 2];
                for (int i = 0; i < pond.Ring.Count; i++) xz[i] = Xz(pond.Ring[i]);
                preview.Line(slot, xz, pond.Ring.Count, ink, true);
                return;
            }
            if (doc.FindDam(new DamId(id)) is Dam dam)
            {
                xz[0] = Xz(dam.BankA);
                xz[1] = Xz(dam.BankB);
                preview.Line(slot, xz, 2, ink);
                return;
            }
            preview.Hide(slot);
        }

        static Vector2 Xz(PointMm p) => new Vector2((float)p.XMetres, (float)p.YMetres);
    }

    /// <summary>
    /// Task 4.14's Edit (the Snowmaking tab's Water group, M; MapLibre's ponds and dams were fixed once built): click a
    /// pond or dam, and its own tool opens on it in this window: a pond's shoreline points drag and its pool, dig and name
    /// change; a dam's pool moves (both banks re-snap), its spillway flips and its channel width changes. Regrade builds it.
    /// Esc goes back to picking.
    /// </summary>
    public sealed class EditWaterTool : IDesignTool, IKeepsWork
    {
        public const string ToolId = "edit-water";
        ToolContext _ctx;
        IDesignTool _inner;
        Vector2[] _xz = new Vector2[16];

        public string Id => ToolId;
        public string Name => "Edit pond or dam";
        public bool HasWork => _inner?.HasWork ?? false;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            Picking();
        }

        void Picking()
        {
            var w = _ctx.Window;
            w.SetName(null);
            w.SetSteps(new[] { "Pick" });
            w.SetStep(0);
            w.SetHint("Click a pond or a dam to change it.");
            w.SetMessage(null);
            w.SetKeys();
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.SetPlanShown(false);
            w.RemoveOption("spill");
            w.ShowReview(null);
            _ctx.Preview.HideFrom(0);
        }

        public void Tick(in ToolInput input)
        {
            if (_inner != null) { _inner.Tick(input); return; }
            var doc = _ctx.History.Document;
            ulong under = input.HasGround && !input.OverPanel ? WaterPick.Under(doc, _ctx.Earthwork, input.Point) : 0;
            WaterPick.Draw(_ctx.Preview, doc, under, 0, PreviewInk.Line, ref _xz);
            if (input.Click && under != 0) Choose(under);
        }

        /// <summary>Opens a pond or dam (a click on it, or its card).</summary>
        public void Choose(ulong id)
        {
            var doc = _ctx?.History.Document;
            if (doc == null) return;
            _ctx.Preview.HideFrom(0);
            if (doc.FindPond(new PondId(id)) is Pond p) _inner = new PondTool(p);
            else if (doc.FindDam(new DamId(id)) is Dam d) _inner = new DamTool(d);
            else return;
            _inner.Arm(_ctx);
        }

        public ToolBackOut Escape()
        {
            if (_inner == null) return ToolBackOut.Close;
            if (_inner.Escape() == ToolBackOut.Stepped) return ToolBackOut.Stepped;
            Back();
            return ToolBackOut.Stepped;
        }

        void Back()
        {
            _ctx.Earthwork?.ClearPending();
            _ctx.PendingTerrain = null;
            _ctx.Preview.Clear();
            _inner = null;   // not disarmed: that would end the shared context
            Picking();
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision) => _inner?.Commit(stage, baseRevision);
        public void Committed(CommandResult result) => _inner?.Committed(result);

        public void AskBeforeLeaving(Action then)
        {
            if (_inner is IKeepsWork k) k.AskBeforeLeaving(then);
            else then();
        }

        public void Disarm()
        {
            _inner?.Disarm();
            _inner = null;
            _ctx = null;
        }

        // For tests.
        public IDesignTool Inner => _inner;
    }

    /// <summary>
    /// Task 4.14's Remove (the Water group, X; PD-08): click a pond or dam to select it ("Click again to remove"), a second
    /// click or Enter takes it out with its berm or embankment and clearing, the ground back byte for byte (the fold without
    /// it), in one undoable command. Esc keeps it. MapLibre removed a dam without asking and left the berm in the ground.
    /// </summary>
    public sealed class RemoveWaterTool : IDesignTool
    {
        public const string ToolId = "remove-water";
        ToolContext _ctx;
        ulong _armed;
        string _armedName;
        DesignEarthwork.Result _rebuild;
        bool _working, _removeWhenReady;
        Vector2[] _xz = new Vector2[16];

        public string Id => ToolId;
        public string Name => "Remove pond or dam";
        public bool HasWork => false;

        public void Arm(ToolContext context)
        {
            _ctx = context;
            _ctx.Earthwork?.Start();
            _ctx.Window.SetName(null);
            Idle();
        }

        void Idle()
        {
            _armed = 0;
            _armedName = null;
            _rebuild = null;
            _working = _removeWhenReady = false;
            _ctx.Preview.HideFrom(0);
            var w = _ctx.Window;
            w.SetHint("Click a pond or a dam. Its berm or embankment and clearing go with it, the ground back as it was; Ctrl+Z puts it back.");
            w.SetMessage(null);
            w.SetKeys();
            w.SetNext(false, false);
            w.SetCommit(false, false);
            w.ShowReview(null);
        }

        public void Tick(in ToolInput input)
        {
            var doc = _ctx.History.Document;
            if (_armed != 0 && WaterPick.NameOf(doc, _armed) == null) Idle();
            ulong under = input.HasGround && !input.OverPanel ? WaterPick.Under(doc, _ctx.Earthwork, input.Point) : 0;
            WaterPick.Draw(_ctx.Preview, doc, _armed != 0 ? _armed : under, 0, _armed != 0 ? PreviewInk.Bad : PreviewInk.Line, ref _xz);
            if (_armed != 0 && input.Pressed(ToolKeys.Next)) { Remove(); return; }
            if (!input.Click || input.OverPanel) return;
            if (under == 0) { if (_armed != 0) Idle(); return; }
            if (under == _armed) { Remove(); return; }
            Choose(under);
        }

        /// <summary>Selects a pond or dam as a first click on it would (its card).</summary>
        public void Choose(ulong id)
        {
            var doc = _ctx?.History.Document;
            string name = doc != null ? WaterPick.NameOf(doc, id) : null;
            if (name == null) return;
            Idle();
            _armed = id;
            _armedName = name;
            var w = _ctx.Window;
            w.SetName(name);
            w.SetHint($"Click {name} again to remove it: the ground goes back as it was. Esc keeps it.");
            w.SetKeys((GameAction.ToolNext, "remove"));
            var host = _ctx.Earthwork;
            if (host == null || !host.Ready) return;   // no ground session (tests): the ground stays
            _working = true;
            var fold = host.Earthwork;
            var committed = _ctx.History.Terrain;
            long revision = _ctx.History.Revision;
            _ctx.Analyse(() => fold.Rebuild(new RemoveWater(revision, id).Apply(doc).Document, committed), r =>
            {
                _working = false;
                if (_armed != id) return;
                _rebuild = r;
                if (_removeWhenReady) Remove();
            });
        }

        void Remove()
        {
            if (_armed == 0) return;
            if (_working) { _removeWhenReady = true; _ctx.Window.SetMessage("Working out the ground without it…", true); return; }
            var result = _ctx.Submit?.Invoke(new RemoveWater(_ctx.History.Revision, _armed), _rebuild?.Edits);
            if (result == null || !result.Accepted) return;
            if (_rebuild != null && _ctx.History.Terrain == _rebuild.Edits) _ctx.Earthwork?.Earthwork?.Accept(_rebuild);
            _ctx.Notice?.Invoke(_armedName + " removed");
            _ctx.Window.SetName(null);
            Idle();
        }

        public ToolBackOut Escape()
        {
            if (_armed == 0) return ToolBackOut.Close;
            _ctx.Window.SetName(null);
            Idle();
            return ToolBackOut.Stepped;
        }

        public DesignCommand Commit(WorkStage stage, long baseRevision) => null;   // the second click removes
        public void Committed(CommandResult result) { }
        public void Disarm() { _ctx = null; }

        // For tests.
        public ulong Armed => _armed;
        public bool Working => _working;
    }
}
