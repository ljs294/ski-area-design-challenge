namespace MountainPlanner.Domain.Design
{
    /// <summary>
    /// What a confirmed piece of earthwork moved (task 15, owner BR19): cut and fill against the ground before it, m³, and
    /// the 1 m cells it cleared of trees. Volumes only; the Phase 4 economy prices them.
    /// </summary>
    public readonly struct EarthworkBill
    {
        public readonly double CutCubicMetres, FillCubicMetres;
        public readonly int ClearedCells, Strokes;

        public EarthworkBill(double cutCubicMetres, double fillCubicMetres, int clearedCells, int strokes)
        {
            CutCubicMetres = cutCubicMetres;
            FillCubicMetres = fillCubicMetres;
            ClearedCells = clearedCells;
            Strokes = strokes;
        }
    }

    /// <summary>
    /// Where confirmed earthwork is paid for (BR19): the Terrain brushes' Confirm hands its bill here, once. Phase 4's
    /// economy implements it; in Phase 3 nothing does, and nothing is charged.
    /// </summary>
    public interface IEarthworkCharges
    {
        void Charge(in EarthworkBill bill);
    }
}
