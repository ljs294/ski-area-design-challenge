using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;
using UnityEngine;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.25 PR A (GO-01, GO-05, SN-22 v4): the design's grooming goes to the worker (<see cref="SimCommandKind.SetGrooming"/>,
    /// A = 0): the fleet and its crew pins, and each built run's setting and priority as the player sees it on that run
    /// (its shaped trail's, else the run's own; asked at the run's middle, as the switch is). Sent after every new world and
    /// whenever the document or its sections change what it says; resent after a load, so the worker always has the
    /// design's.
    ///
    /// The dev tool <c>-groomers N[:model]</c> (model 400, 400w, 800 or 800w; 800w when left out) adds N free groomers to
    /// what is sent, numbered from 9001: not in the document, so no history entry and not saved (the save keeps the
    /// design's fleet; pass the flag again). A new resort has none (owner 2026-11-20), so the flag is how a test or demo
    /// grooms without buying.
    /// </summary>
    public sealed partial class MountainViewer
    {
        DesignDocument _groomDoc;
        TrailSections _groomSections;
        SimGrooming _groomSent;

        /// <summary>A new world: nothing sent to it yet.</summary>
        void ResetGrooming()
        {
            _groomDoc = null;
            _groomSections = null;
            _groomSent = null;
        }

        int _groomSaidNight = int.MinValue, _groomSaidPlan = int.MinValue;

        /// <summary>The log's line for each night's plan and each morning (from the snapshot): what the review reads.</summary>
        void SayGrooming(SimSnapshot s)
        {
            var g = s?.Grooming;
            if (g == null || !g.Live) return;
            if (g.NightDay != _groomSaidPlan)
            {
                _groomSaidPlan = g.NightDay;
                int till = 0, push = 0;
                for (int t = 0; t < g.Job.Length; t++) { if (g.Job[t] == GroomJob.Till) till++; else if (g.Job[t] == GroomJob.Push) push++; }
                Debug.Log($"[Grooming] night of day {g.NightDay}: {till} till, {push} push; want {g.WantMinutes} min, crews swing {g.CrewsSwing} " +
                          $"night {g.CrewsNight}, crewed {g.CrewMinutes} min, short {g.ShortMinutes}{(g.SnowingTonight ? ", snowing" : "")}");
            }
            if (g.LastNightDay != _groomSaidNight)
            {
                _groomSaidNight = g.LastNightDay;
                int done = 0, other = 0;
                var kinds = new System.Text.StringBuilder();
                foreach (GroomRowState k in Enum.GetValues(typeof(GroomRowState)))
                {
                    int c = 0;
                    for (int t = 0; t < g.LastState.Length; t++) if (g.LastState[t] == k) c++;
                    if (k == GroomRowState.Done) done = c; else if (k != GroomRowState.None) other += c;
                    if (c > 0 && k != GroomRowState.None) kinds.Append($" {k} {c}");
                }
                Debug.Log($"[Grooming] morning after day {g.LastNightDay}: {done} groomed, {other} other, {g.LastNotReached} not reached ({kinds.ToString().Trim()}); " +
                          $"worked {g.LastWorkMinutes} min, ≈ ${g.LastCostUsd}");
            }
        }

        /// <summary>Per frame, after the trail layout: the design's grooming when it changed.</summary>
        void SendGrooming()
        {
            SayGrooming(_simNow);
            var doc = _simSent.Document;
            if (_sim == null || doc == null) return;
            if (_groomSent != null && ReferenceEquals(doc, _groomDoc) && ReferenceEquals(_sections, _groomSections)) return;
            var g = GroomingFor(doc, _sections, _netGeometry);
            if (_groomSent != null && g.SameAs(_groomSent)) { _groomDoc = doc; _groomSections = _sections; return; }
            if (!_sim.Send(SimCommandKind.SetGrooming, 0, g)) return;   // the queue was full: next frame
            if (_groomSent == null || g.Numbers.Length != _groomSent.Numbers.Length)
                Debug.Log($"[Grooming] {g.Numbers.Length} groomers ({DevGroomers.Count} from -groomers), {g.TrailIds.Length} runs to the worker");
            _groomDoc = doc;
            _groomSections = _sections;
            _groomSent = g;
        }

        /// <summary>
        /// The worker's grooming for <paramref name="doc"/>: its fleet (with the dev tool's), and each built run's grooming:
        /// that of the shaped trail on the run's middle, else the run's own.
        /// </summary>
        internal static SimGrooming GroomingFor(DesignDocument doc, TrailSections sections, ITrailGeometry geometry)
        {
            var runs = new List<KeyValuePair<ulong, TrailGrooming>>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built) continue;
                runs.Add(new KeyValuePair<ulong, TrailGrooming>(t.Id.Value, GroomingOn(doc, sections, geometry, t)));
            }
            var fleet = doc.Grooming ?? GroomFleet.Empty;
            var dev = DevGroomers;
            if (dev.Count > 0)
            {
                foreach (var g in dev) if (fleet.Groomers.Count < GroomFleet.MaxGroomers) fleet = fleet.WithAdded(g);
            }
            return SimGrooming.From(fleet, runs, id => doc.FindTrail(new TrailId(id))?.Settings.Kind ?? TrailKind.Run);
        }

        static TrailGrooming GroomingOn(DesignDocument doc, TrailSections sections, ITrailGeometry geometry, Trail run)
        {
            if (sections != null)
            {
                int mid = geometry != null ? Math.Max(0, geometry.CentrelineLengthMm(run) / 2) : 0;
                var seg = sections.SegmentAt(run.Id, mid);
                var resolved = seg != null ? sections.TrailOf(seg) : sections.TrailOf(run.Id);
                if (resolved?.Line != null && doc.FindLine(resolved.Line.Id) is TrailLine line) return line.Grooming;
            }
            return run.Grooming;
        }

        static List<Groomer> _devGroomers;

        /// <summary>
        /// Task 4.25 PR B: the F1 panel's dev row: adds free groomers as <c>-groomers</c> does (numbered on from 9001, not in
        /// the design, not saved), and shows the fleet the worker has.
        /// </summary>
        void DrawGroomerRows(ref float y, float row, Action<string> caption, Func<int, string, bool> button, Func<float, int, string, float> note)
        {
            var g = _simNow?.Grooming;
            caption("Groomers (dev)");
            if (button(0, "Add 1 groomer")) AddDevGroomers(1, GroomerModel.S800W);
            if (button(1, "Add 3 groomers")) AddDevGroomers(3, GroomerModel.S800W);
            if (button(2, "Add 3 × 400")) AddDevGroomers(3, GroomerModel.S400);
            y += row;
            if (g != null)
                y += note(y, 1, $"{g.GroomerCount} groomers ({DevGroomers.Count} dev) · crews swing {g.CrewsSwing}, night {g.CrewsNight}, day {g.CrewsDay} · " +
                                $"tonight want {g.WantMinutes / 60.0:F1} h, crewed {g.CrewMinutes / 60.0:F1} h, short {g.ShortMinutes / 60.0:F1} h");
        }

        /// <summary>Adds <paramref name="n"/> free dev groomers and resends the fleet.</summary>
        internal void AddDevGroomers(int n, GroomerModel model)
        {
            var dev = DevGroomers;
            int fleet = Design?.Document?.Grooming?.Groomers.Count ?? 0;
            for (int i = 0; i < n && dev.Count + fleet < GroomFleet.MaxGroomers; i++) dev.Add(new Groomer(9001 + dev.Count, model, GroomerDrive.Diesel));
            _groomSent = null;   // resend
            Debug.Log($"[Grooming] dev tool: now {dev.Count} free groomers");
        }

        /// <summary>
        /// Task 4.25 PR B (GO-07): tonight's order for one trail (Till, Push, Skip, or None to clear), straight to the worker,
        /// which keeps it in the sidecar; not a design edit, so not in the undo history (it lasts one night).
        /// </summary>
        internal bool SendGroomOrder(ulong trailId, GroomOrder order) =>
            _sim != null && _sim.Send(SimCommand.Of(SimCommandKind.SetGrooming, 1, new SimGroomOrder(trailId, order)));

        /// <summary>Task 4.25 PR B: a grooming edit (setting, priority, buy, sell, tiller, crews) as one undoable step; null or why not.</summary>
        internal string SubmitGrooming(DesignCommand command)
        {
            var history = Design;
            if (history == null) return "There's no resort open.";
            var r = history.Submit(command);
            if (r.Accepted) return null;
            return r.Reason == "Nothing changes." ? null : r.Reason;
        }

        /// <summary>The dev tool's groomers (<c>-groomers N[:model]</c>), numbered from 9001; none without the flag.</summary>
        internal static List<Groomer> DevGroomers => _devGroomers ??= ParseDevGroomers(Environment.GetCommandLineArgs());

        internal static List<Groomer> ParseDevGroomers(string[] args)
        {
            var list = new List<Groomer>();
            int at = Array.IndexOf(args, "-groomers");
            if (at < 0 || at + 1 >= args.Length) return list;
            string value = args[at + 1];
            int colon = value.IndexOf(':');
            string countText = colon < 0 ? value : value.Substring(0, colon);
            string modelText = colon < 0 ? "800w" : value.Substring(colon + 1).Trim().ToLowerInvariant();
            if (!int.TryParse(countText, out int n) || n < 1)
            {
                Debug.LogWarning($"[Grooming] -groomers {value}: give a count, e.g. -groomers 3 or -groomers 3:400w");
                return list;
            }
            GroomerModel model;
            switch (modelText)
            {
                case "400": model = GroomerModel.S400; break;
                case "400w": model = GroomerModel.S400W; break;
                case "800": model = GroomerModel.S800; break;
                case "800w": model = GroomerModel.S800W; break;
                default:
                    Debug.LogWarning($"[Grooming] -groomers {value}: the model is 400, 400w, 800 or 800w; using 800w");
                    model = GroomerModel.S800W;
                    break;
            }
            n = Math.Min(n, GroomFleet.MaxGroomers);
            for (int i = 0; i < n; i++) list.Add(new Groomer(9001 + i, model, GroomerDrive.Diesel));
            Debug.Log($"[Grooming] dev tool: {n} free Skogkatt {modelText} groomers (not in the design, not saved)");
            return list;
        }
    }
}
