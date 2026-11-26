namespace MountainPlanner.Domain.Snow
{
    /// <summary>
    /// Task 4.29 PR B: which trail each 8 m cell draws as, for the trails' grass on the ground. From a
    /// <see cref="TrailSnowLayout"/>: per cell of its grid (rows south to north, then west to east), 1 + the layout trail that
    /// owns most of the cell's fine cells (ties to the lower index), or 0 where no trail owns any. Engine-free and integer
    /// only; built off the main thread (it allocates the grid).
    /// </summary>
    public static class TrailCellOwners
    {
        /// <summary>The grid's owners (<see cref="TrailSnowLayout.GridWidth"/> × <see cref="TrailSnowLayout.GridHeight"/>).</summary>
        public static ushort[] Build(TrailSnowLayout layout)
        {
            if (layout == null) return null;
            var map = new ushort[layout.GridWidth * layout.GridHeight];
            int bc = layout.BlockCells;
            var owners = layout.Owner;
            // A block holds a few trails at most: their counts in two small arrays, reused.
            var ids = new short[8];
            var counts = new int[8];
            for (int b = 0; b < layout.Blocks; b++)
            {
                int seen = 0;
                for (int k = b * bc, end = k + bc; k < end; k++)
                {
                    short o = owners[k];
                    if (o < 0) continue;
                    int s = 0;
                    while (s < seen && ids[s] != o) s++;
                    if (s == seen)
                    {
                        if (seen == ids.Length) { System.Array.Resize(ref ids, seen * 2); System.Array.Resize(ref counts, seen * 2); }
                        ids[s] = o; counts[s] = 0; seen++;
                    }
                    counts[s]++;
                }
                if (seen == 0) continue;
                int best = 0;
                for (int s = 1; s < seen; s++)
                    if (counts[s] > counts[best] || counts[s] == counts[best] && ids[s] < ids[best]) best = s;
                map[layout.BlockCell[b]] = (ushort)(ids[best] + 1);
            }
            return map;
        }
    }
}
