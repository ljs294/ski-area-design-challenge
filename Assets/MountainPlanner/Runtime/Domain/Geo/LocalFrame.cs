namespace MountainPlanner.Domain.Geo
{
    /// <summary>
    /// The resort's local frame (0.3 §4.1): the origin is the site centre, x = Albers X − X₀ (east),
    /// z = Albers Y − Y₀ (north), and y is elevation; one unit is one metre. Kept in doubles here;
    /// the World assembly narrows to Unity floats, which stay precise to well under 1 mm across an
    /// 11 km ring because the origin is at the centre.
    /// </summary>
    public readonly struct LocalFrame
    {
        public readonly AlbersPoint Origin;

        public LocalFrame(AlbersPoint origin)
        {
            Origin = origin;
        }

        /// <summary>Albers position to local (x east, z north) metres.</summary>
        public (double x, double z) ToLocal(AlbersPoint p) => (p.X - Origin.X, p.Y - Origin.Y);

        /// <summary>Local (x east, z north) metres to an Albers position.</summary>
        public AlbersPoint ToAlbers(double x, double z) => new AlbersPoint(Origin.X + x, Origin.Y + z);
    }
}
