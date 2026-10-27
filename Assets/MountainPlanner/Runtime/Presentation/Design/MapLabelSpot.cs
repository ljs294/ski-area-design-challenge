using UnityEngine;

namespace MountainPlanner.Presentation.Design
{
    /// <summary>
    /// A place a name on the map may sit (NT7): the world point it's centred on and, for a name set along a line (a run, a
    /// lift), two points of that line either side of it, which give the name its slant on screen. An upright name (a
    /// landmark, an Area) has From = To = At.
    /// </summary>
    public readonly struct MapLabelSpot
    {
        public readonly Vector3 At, From, To;

        public MapLabelSpot(Vector3 at, Vector3 from, Vector3 to)
        {
            At = at;
            From = from;
            To = to;
        }

        /// <summary>An upright name standing on <paramref name="at"/>.</summary>
        public static MapLabelSpot Upright(Vector3 at) => new MapLabelSpot(at, at, at);

        /// <summary>Whether the name is set along a line.</summary>
        public bool Along => From != To;
    }
}
