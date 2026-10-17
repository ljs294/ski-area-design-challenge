using System;
using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>The preview's inks (P3-06): the mockup's meanings (mint OK, red can't, orange a plan, ice snapped).</summary>
    public enum PreviewInk : byte
    {
        /// <summary>The line being drawn, white.</summary>
        Line,
        /// <summary>Something that fits (a terminal that can sit), green.</summary>
        Ok,
        /// <summary>Something that can't (a red tower, a separate patch), red.</summary>
        Bad,
        /// <summary>A saved plan (CF-06), orange.</summary>
        Plan,
        /// <summary>A snapped end (TR-05), ice.</summary>
        Snap,
        /// <summary>A painted trail's edge, a muted trail colour.</summary>
        Paint,
        /// <summary>The brush ring, white and thin.</summary>
        Brush,
        /// <summary>A built lift on the placeholder overlay, the trail map's lift red.</summary>
        Lift,
    }

    /// <summary>
    /// The pending preview a construction tool draws (P3-06, CF-08): lines, rings and dots draped on the terrain, kept
    /// in numbered slots the tool reuses, so redrawing a moving ghost allocates nothing. Points are local x/z metres.
    /// The host clears everything when the tool disarms or commits; <see cref="Cleared"/> tells whatever else draws a
    /// pending edit (task 05/08's yellow contours) to clear too.
    /// </summary>
    public interface IToolPreview
    {
        /// <summary>A polyline through <paramref name="count"/> points of <paramref name="xz"/>, draped on the ground.</summary>
        void Line(int slot, Vector2[] xz, int count, PreviewInk ink, bool closed = false);

        /// <summary>A circle on the ground (the brush ring, a snap target).</summary>
        void Ring(int slot, Vector2 centre, float radiusMetres, PreviewInk ink);

        /// <summary>A dot: a terminal's bullwheel, a trail's head or tail.</summary>
        void Dot(int slot, Vector2 at, PreviewInk ink);

        /// <summary>Hides one slot.</summary>
        void Hide(int slot);

        /// <summary>Hides every slot from <paramref name="slot"/> on (a tool's trailing slots after a shorter redraw).</summary>
        void HideFrom(int slot);

        /// <summary>Hides everything and raises <see cref="Cleared"/>.</summary>
        void Clear();

        event Action Cleared;
    }
}
