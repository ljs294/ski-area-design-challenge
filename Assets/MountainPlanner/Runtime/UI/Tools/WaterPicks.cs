using MountainPlanner.Domain.Design;

namespace MountainPlanner.UI.Tools
{
    /// <summary>
    /// Task 4.14 (PD-05, PD-06, WS-03): a stream a dam line crosses. <see cref="Upstream"/> is a point on the stream on its
    /// upstream side: the host's median test (the ground 65, 165 and 330 ft along the stream each way; on a tie, the
    /// whole side's mean), so the tool never reads the ground itself.
    /// </summary>
    public readonly struct StreamCrossing
    {
        /// <summary>The OSM way (0 for the rasterised streams body of an area without water.json).</summary>
        public readonly ulong Way;
        public readonly string Name;
        public readonly double WidthMetres;
        public readonly bool WidthFromOsm;
        public readonly PointMm At, Upstream;

        public StreamCrossing(ulong way, string name, double widthMetres, bool widthFromOsm, PointMm at, PointMm upstream)
        {
            Way = way;
            Name = name ?? "";
            WidthMetres = widthMetres;
            WidthFromOsm = widthFromOsm;
            At = at;
            Upstream = upstream;
        }

        public string Label => string.IsNullOrEmpty(Name) ? "an unnamed stream" : Name;
    }

    /// <summary>Task 4.14 (WS-01, LK-02 v1): a lake under the pointer, for the Water source tool.</summary>
    public readonly struct LakeUnder
    {
        /// <summary>The lake's id in the area's WaterBodyTable.</summary>
        public readonly ulong Id;
        public readonly string Name;
        /// <summary>LK-02 v1's estimated mean depth, tenths of a foot (always clamped, never zero).</summary>
        public readonly int EstimatedDepthTenthsFt;
        public readonly double AreaSquareMetres;

        public LakeUnder(ulong id, string name, int estimatedDepthTenthsFt, double areaSquareMetres)
        {
            Id = id;
            Name = name ?? "";
            EstimatedDepthTenthsFt = estimatedDepthTenthsFt;
            AreaSquareMetres = areaSquareMetres;
        }

        public string Label => string.IsNullOrEmpty(Name) ? "Lake" : Name;
    }

    /// <summary>Task 4.15 (WS-05, WS-11): a snowmaking source's water near a point: its id, name and the nearest water.</summary>
    public readonly struct SourceNear
    {
        public readonly ulong Id;
        public readonly string Name;
        /// <summary>The source's water nearest the point (the point itself when it is in the water).</summary>
        public readonly PointMm Water;
        public readonly double DistanceMetres;
        /// <summary>A pond or dam (an outlet may fill it); a lake may only be drawn from.</summary>
        public readonly bool Fillable;

        public SourceNear(ulong id, string name, PointMm water, double distanceMetres, bool fillable)
        {
            Id = id;
            Name = name ?? "";
            Water = water;
            DistanceMetres = distanceMetres;
            Fillable = fillable;
        }
    }
}
