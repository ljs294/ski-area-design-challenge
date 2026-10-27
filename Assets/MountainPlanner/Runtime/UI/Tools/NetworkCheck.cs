using System.Collections.Generic;
using MountainPlanner.Domain.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// The review's Network row (task P3-10; NW-04, NW-12): the lift or trail being built, as if built, in the network
    /// of everything else built. Its warnings never block Break ground. One geometry per earthwork, so the trails already
    /// built keep their shapes between reviews.
    /// </summary>
    public static class NetworkCheck
    {
        static readonly object Gate = new object();
        static DesignEarthwork _for;
        static TrailNetworkGeometry _geometry;

        /// <summary>
        /// The warnings for <paramref name="id"/> (the lift or trail <paramref name="built"/> makes or changes, with stage
        /// Built) once it's applied to <paramref name="doc"/>; empty when it joins the network, null when it can't be told
        /// (no earthwork yet, or the command doesn't apply).
        /// </summary>
        /// <param name="ground">The natural ground, for paths' directions (PA-06), as the game's network uses.</param>
        public static List<string> Warnings(DesignDocument doc, DesignCommand built, ulong id, DesignEarthwork earthwork, MountainPlanner.Domain.Edits.IHeightSource ground)
        {
            if (doc == null || built == null || earthwork == null) return null;
            var r = built.Apply(doc);
            if (!r.Accepted) return null;
            if (id == 0 && r.Created.Count > 0) id = r.Created[0];
            TrailNetworkGeometry geometry;
            lock (Gate)
            {
                if (!ReferenceEquals(_for, earthwork))
                {
                    _for = earthwork;
                    _geometry = new TrailNetworkGeometry(earthwork.Site, ground, null, t => earthwork.DetailOf(t.Id.Value) as TrailBuild);
                }
                geometry = _geometry;
            }
            var report = NetworkReport.Build(r.Document, geometry);
            var list = new List<string>();
            var lift = report.Of(new LiftId(id));
            if (lift != null && lift.Warning != NetworkWarning.None) list.Add(NetworkReport.Text(lift.Warning));
            var trail = report.Of(new TrailId(id));
            if (trail != null && trail.Warning != NetworkWarning.None) list.Add(NetworkReport.Text(trail.Warning));
            return list;
        }

        /// <summary>Fills a review's Network row and adds its warnings as cautions (amber, never blocking).</summary>
        public static void Fill(ReviewModel review, List<string> warnings)
        {
            if (review == null || warnings == null) return;
            review.Fill(ReviewModel.NetworkSlot, warnings.Count == 0 ? "joins the network" : string.Join(" · ", warnings));
            foreach (var w in warnings) review.Warn(w, "NW-12");
        }
    }
}
