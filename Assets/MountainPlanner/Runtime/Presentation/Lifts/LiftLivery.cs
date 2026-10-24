using MountainPlanner.Domain.Design;
using UnityEngine;

namespace MountainPlanner.Presentation.Lifts
{
    /// <summary>
    /// A lift's two colours (LT41, owner 2026-10-23): the primary (the hood and the parts the models mark as livery) and the
    /// accent (the parts they mark as accent). Each type has its defaults; choosing them per lift comes later.
    /// </summary>
    public readonly struct LiftLivery
    {
        public readonly Color Primary, Accent;
        public LiftLivery(Color primary, Color accent) { Primary = primary; Accent = accent; }

        public static readonly Color Red = new Color(0.72f, 0.12f, 0.09f), DarkAccent = new Color(0.13f, 0.14f, 0.15f);
        public static readonly Color Black = new Color(0.07f, 0.075f, 0.08f), White = new Color(0.92f, 0.93f, 0.94f);

        /// <summary>The type's defaults: the fixed-grip quads red with a dark accent, the D6 black with white.</summary>
        public static LiftLivery For(string catalogId) =>
            catalogId == LiftCatalogIds.SesselliftD6 ? new LiftLivery(Black, White) : new LiftLivery(Red, DarkAccent);
    }
}
