using System;
using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.Simulation;

namespace MountainPlanner.App
{
    /// <summary>
    /// Task 4.29 PR A (SN-37): the design's mowing goes to the worker (<see cref="SimCommandKind.SetTrailMowing"/>): each
    /// built run's setting as the player sees it on that run (its shaped trail's, else the run's own; asked at the run's
    /// middle, as the grooming is). Sent after every new world and whenever the document or its sections change what it
    /// says; resent after a load, so the worker always has the design's.
    /// </summary>
    public sealed partial class MountainViewer
    {
        DesignDocument _mowDoc;
        TrailSections _mowSections;
        SimMowing _mowSent;

        /// <summary>A new world: nothing sent to it yet.</summary>
        void ResetMowing()
        {
            _mowDoc = null;
            _mowSections = null;
            _mowSent = null;
        }

        /// <summary>Per frame: the design's mowing, when it changed (the same diff as <see cref="SendGrooming"/>).</summary>
        void SendMowing()
        {
            var doc = _simSent.Document;
            if (_sim == null || doc == null) return;
            if (_mowSent != null && ReferenceEquals(doc, _mowDoc) && ReferenceEquals(_sections, _mowSections)) return;
            var m = MowingFor(doc, _sections, _netGeometry);
            if (_mowSent != null && m.SameAs(_mowSent)) { _mowDoc = doc; _mowSections = _sections; return; }
            if (!_sim.Send(SimCommandKind.SetTrailMowing, 0, m)) return;   // the queue was full: next frame
            _mowDoc = doc;
            _mowSections = _sections;
            _mowSent = m;
        }

        /// <summary>The worker's mowing for <paramref name="doc"/>: each built run's, that of the shaped trail on its middle, else its own.</summary>
        internal static SimMowing MowingFor(DesignDocument doc, TrailSections sections, ITrailGeometry geometry)
        {
            var runs = new List<KeyValuePair<ulong, TrailMowing>>();
            foreach (var t in doc.Trails)
            {
                if (t.Stage != WorkStage.Built) continue;
                runs.Add(new KeyValuePair<ulong, TrailMowing>(t.Id.Value, MowingOn(doc, sections, geometry, t)));
            }
            return SimMowing.From(runs);
        }

        static TrailMowing MowingOn(DesignDocument doc, TrailSections sections, ITrailGeometry geometry, Trail run)
        {
            if (sections != null)
            {
                int mid = geometry != null ? Math.Max(0, geometry.CentrelineLengthMm(run) / 2) : 0;
                var seg = sections.SegmentAt(run.Id, mid);
                var resolved = seg != null ? sections.TrailOf(seg) : sections.TrailOf(run.Id);
                if (resolved?.Line != null && doc.FindLine(resolved.Line.Id) is TrailLine line) return line.Mowing;
            }
            return run.Mowing;
        }
    }
}
