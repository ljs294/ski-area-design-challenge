using System;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// The ground under the design's built lifts, rebuilt from what each one stores (task 09, LT24): every built lift's
    /// earthwork (<see cref="LiftBuild.Earthwork"/>, search-free) laid in the design's order over the natural ground, so the
    /// same lifts always give the same bytes as their first builds. Remove lift uses it for the ground without the removed
    /// lift (its pads, trench and clearing gone, the others' kept exactly). Lifts are the only earthwork on main today;
    /// P3-08's replay (TL3) takes this over when trails grade too, calling the same per-lift function.
    /// </summary>
    public static class LiftReplay
    {
        /// <summary>
        /// The edits for every built lift with levels except <paramref name="skip"/> (0 for none), over
        /// <paramref name="natural"/> with no edits. <paramref name="toGround"/> turns a design point into
        /// <paramref name="natural"/>'s frame.
        /// </summary>
        /// <param name="replace">Edit lift's draft, standing in for the lift with its id (LT2's Apply), or null.</param>
        public static TerrainEdits Rebuild(IHeightSource natural, EditLattice lattice, DesignDocument doc, Func<PointMm, (double X, double Y)> toGround,
                                           LiftId skip = default, Lift replace = null)
        {
            if (natural == null) throw new ArgumentNullException(nameof(natural));
            if (doc == null) throw new ArgumentNullException(nameof(doc));
            if (toGround == null) throw new ArgumentNullException(nameof(toGround));
            var current = new EditedHeights(natural, lattice, TerrainEdits.Empty);
            foreach (var listed in doc.Lifts)
            {
                var lift = replace != null && listed.Id.Value == replace.Id.Value ? replace : listed;
                if (lift.Id.Value == skip.Value || lift.Stage != WorkStage.Built || lift.Levels is not LiftLevels levels || lift.Towers.Count == 0) continue;
                var b = toGround(lift.Bottom);
                var t = toGround(lift.Top);
                var works = LiftBuild.Earthwork(LiftType.Of(lift.CatalogId), current, b.X, b.Y, t.X, t.Y, lift.Towers, levels, lift.Stance);
                current = new EditedHeights(natural, lattice, works.Edits);
            }
            return current.Edits;
        }
    }
}
