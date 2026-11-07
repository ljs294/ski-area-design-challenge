using System;
using MountainPlanner.Domain.Design;
using MountainPlanner.Domain.Edits;

namespace MountainPlanner.Domain.Costs
{
    /// <summary>
    /// The natural tree cover (task 4.01): how much of a 1 m cell was forest before any design, 0 to 1, at a point in the
    /// edit lattice's frame. The game answers it from the area's canopy layer; a test answers it with a plain grid.
    /// </summary>
    public interface ITreeCover
    {
        double TreeShare(double x, double y);
    }

    /// <summary>
    /// What a piece of work does to the site, priced by <see cref="SiteCost"/> (task 4.01): cut and fill, m³, and the forest
    /// it newly clears, m² (each newly cleared cell weighted by its natural tree share, so bare ground costs nothing).
    /// </summary>
    public readonly struct SiteWorks
    {
        public readonly double CutCubicMetres, FillCubicMetres, ForestSquareMetres;

        public SiteWorks(double cutCubicMetres, double fillCubicMetres, double forestSquareMetres)
        {
            CutCubicMetres = Math.Max(0, cutCubicMetres);
            FillCubicMetres = Math.Max(0, fillCubicMetres);
            ForestSquareMetres = Math.Max(0, forestSquareMetres);
        }

        /// <summary>
        /// The forest <paramref name="after"/> clears that <paramref name="before"/> hadn't (each cell once, however many of
        /// the work's clearings cover it; ground a trail or another lift cleared costs nothing), m².
        /// </summary>
        public static double NewForest(TerrainEdits before, TerrainEdits after, EditLattice lattice, ITreeCover trees)
        {
            if (trees == null || after == null) return 0;
            before ??= TerrainEdits.Empty;
            double sum = 0;
            foreach (var key in TerrainEdits.Changed(before, after))
            {
                var was = before.Get(key);
                var now = after.Get(key);
                for (int j = 0; j < EditTileKey.Side; j++)
                    for (int i = 0; i < EditTileKey.Side; i++)
                        if (now.IsCleared(i, j) && !was.IsCleared(i, j))
                            sum += Share(trees, lattice, key.FirstColumn + i, key.FirstRow + j);
            }
            return sum;
        }

        /// <summary>
        /// The forest a trail's <paramref name="writes"/> clear, m², less the cells <paramref name="alreadyCleared"/> says
        /// earlier work cleared (each cell once).
        /// </summary>
        public static double NewForest(EarthworkWrites writes, Func<int, int, bool> alreadyCleared, EditLattice lattice, ITreeCover trees)
        {
            if (trees == null || writes == null) return 0;
            double sum = 0;
            writes.ForEachCleared((c, r) =>
            {
                if (alreadyCleared == null || !alreadyCleared(c, r)) sum += Share(trees, lattice, c, r);
            });
            return sum;
        }

        // A cell's centre, as LiftClearing reads it.
        static double Share(ITreeCover trees, EditLattice lattice, int c, int r)
        {
            double s = trees.TreeShare(lattice.X(c) + 0.5, lattice.Y(r) - 0.5);
            return s <= 0 ? 0 : s >= 1 ? 1 : s;
        }
    }

    /// <summary>
    /// Prices site works (task 4.01, owner 2026-11-06): <b>Grading</b>, the cut and fill at <see cref="SoilPrices"/>'
    /// rates, and <b>Clearing</b>, the forest cleared by the acre. Clearing isn't soil work, so it's its own line. Lifts take
    /// both lines on their price (<see cref="With(LiftCostBreakdown, in SiteWorks)"/>); trails are these lines alone.
    /// </summary>
    public static class SiteCost
    {
        public const string Grading = "Grading", Clearing = "Clearing";

        public static long GradingUsd(in SiteWorks w) =>
            LiftCost.Dollars((w.CutCubicMetres * SoilPrices.CutUsdPerCubicYard + w.FillCubicMetres * SoilPrices.FillUsdPerCubicYard)
                             / SoilPrices.CubicMetresPerCubicYard);

        public static long ClearingUsd(in SiteWorks w) =>
            LiftCost.Dollars(w.ForestSquareMetres / SoilPrices.SquareMetresPerAcre * SoilPrices.ClearingUsdPerAcre);

        /// <summary>A lift's price with its Grading and Clearing lines.</summary>
        public static LiftCostBreakdown With(LiftCostBreakdown lift, in SiteWorks w) =>
            lift?.With(Grading, GradingUsd(w)).With(Clearing, ClearingUsd(w));

        /// <summary>A trail's cost: Clearing always, Grading when it's graded (owner, 2026-11-06).</summary>
        public static CostBreakdown Trail(in SiteWorks w, bool graded)
        {
            var cost = new CostBreakdown(new[] { new CostLine(Clearing, ClearingUsd(w)) });
            return graded ? cost.With(Grading, GradingUsd(w)) : cost;
        }

        /// <summary>
        /// A lift's site works from its build over <paramref name="ground"/> (the ground without it): both pads' cut and
        /// fill, the line trench's cut, a carpet bed's cut and fill, and the forest it newly clears.
        /// </summary>
        public static SiteWorks OfLift(LiftBuild.Result built, EditedHeights ground, ITreeCover trees)
        {
            if (built == null) return default;
            double cut = 0, fill = 0;
            if (built.Bed != null) { cut += built.Bed.CutCubicMetres; fill += built.Bed.FillCubicMetres; }
            else
            {
                if (built.BottomPad != null) { cut += built.BottomPad.CutCubicMetres; fill += built.BottomPad.FillCubicMetres; }
                if (built.TopPad != null) { cut += built.TopPad.CutCubicMetres; fill += built.TopPad.FillCubicMetres; }
                if (built.Envelope != null) cut += built.Envelope.CutCubicMetres;
            }
            double forest = ground == null ? 0 : SiteWorks.NewForest(ground.Edits, built.Edits, ground.Lattice, trees);
            return new SiteWorks(cut, fill, forest);
        }
    }
}
