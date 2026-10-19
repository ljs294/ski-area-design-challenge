using MountainPlanner.Domain.Design;
using MountainPlanner.World.Lifts;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// A lift type with each end measured on its terminal model (TW-16 v3): where the rope leaves its row and where its
    /// origin lies. The build and the view both use it, so the view solves the rope the build checked.
    /// </summary>
    public static class LiftTypes
    {
        public static LiftType Measured(LiftType type, LiftModelSet models)
        {
            if (models == null) return type;
            return type.With(Measure(type.Bottom, models), Measure(type.Top, models), type.ChairHalfWidthMetres);
        }

        static TerminalEnd Measure(TerminalEnd end, LiftModelSet models)
        {
            if (LiftView.RopeExit(models, end.Model, out float along, out float rise)) end = end.WithExit(along, rise);
            return end.WithOrigin(LiftView.OriginAlong(models, end.Model));
        }
    }
}
