using System.Collections.Generic;
using MountainPlanner.Domain.Design;
using MountainPlanner.World.Lifts;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// A lift type with each end measured on its terminal model (TW-16 v3): where the rope leaves its row, its footprint
    /// (LF-12 v2's pad hugs it) and where its origin lies. The build and the view both use it, so the view solves the rope
    /// the build checked.
    /// </summary>
    public static class LiftTypes
    {
        // The measurements by type and drive, for one model set (each end's footprint walks its meshes).
        static LiftModelSet _cachedFor;
        static readonly Dictionary<(string, LiftDrive), LiftType> Cache = new Dictionary<(string, LiftDrive), LiftType>();

        /// <summary>The type (fresh from <see cref="LiftType.Of(string, LiftDrive?)"/>) with its ends measured on
        /// <paramref name="models"/>, each top row on its own head (LD9).</summary>
        public static LiftType Measured(LiftType type, LiftModelSet models)
        {
            if (models == null) return type;
            lock (Cache)
            {
                if (!ReferenceEquals(models, _cachedFor)) { Cache.Clear(); _cachedFor = models; }
                var key = (type.CatalogId, type.Drive);
                if (Cache.TryGetValue(key, out var known)) return known;
                var measured = Measure(type, models);
                Cache[key] = measured;
                return measured;
            }
        }

        static LiftType Measure(LiftType type, LiftModelSet models)
        {
            // A carpet's stations are measured into LiftRope.CarpetOf already, and its bed is laid from them (CarpetBed): the
            // view must lay the same bed the earthwork graded, so its ends are never re-measured.
            if (type.Belt) return type;
            if (type.TopTrains == null) return type.With(Measure(type.Bottom, models), Measure(type.Top, models), type.ChairHalfWidthMetres);
            // LD9: every top row measured on its head; the first is the top until a rope picks another.
            var trains = new TerminalEnd[type.TopTrains.Count];
            for (int i = 0; i < trains.Length; i++) trains[i] = Measure(type.TopTrains[i], models);
            return type.With(Measure(type.Bottom, models), trains[0], type.ChairHalfWidthMetres, trains);
        }

        static TerminalEnd Measure(TerminalEnd end, LiftModelSet models)
        {
            if (LiftView.RopeExit(models, end.Model, end.Head, out float along, out float rise)) end = end.WithExit(along, rise);
            if (LiftView.TerminalExtent(models, end.Model, end.Head, out float behind, out float ahead, out float halfWidth))
                end = end.WithFootprint(behind, ahead, halfWidth);
            return end.WithOrigin(LiftView.OriginAlong(models, end.Model));
        }
    }
}
