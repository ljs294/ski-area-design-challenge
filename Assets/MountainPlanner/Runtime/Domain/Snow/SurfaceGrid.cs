namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// The 8 m surface grid over the ring (task 10's snow-depth field, 4.05's weather over the map, 4.07's conditions map):
    /// one definition, so every field lines up cell for cell. Cells run west to east, then south to north, from the ring's
    /// south-west corner.
    /// </summary>
    public static class SurfaceGrid
    {
        /// <summary>The cell's side, metres.</summary>
        public const int CellMetres = 8;
    }
}
