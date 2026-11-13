using MountainPlanner.Simulation;
using Unity.Collections;

namespace MountainPlanner.Presentation.Guests
{
    /// <summary>
    /// Task 4.13 PR C2, <c>-dotstress N</c>: headroom for the dots past the worker's crowd cap. Each new column from the
    /// worker is padded to N records with copies of its skiers on runs, each a new serial (<see cref="CopyMark"/> and its place in the column) and no line slot (so the screen's
    /// easing, kept by slot, never mixes a copy with a real skier), in another lane, and moved along its own run by a keyed share of the run's time, so the copies spread down the runs
    /// instead of stacking on the real skiers. Lines and riders aren't copied: copies in a line would only stand on the
    /// real line's places. Render only, a measurement: the worker, its hashes and saves never see a copy.
    /// </summary>
    public static class DotStress
    {
        /// <summary>A copy's serial has this bit set (the worker's serials never reach it).</summary>
        public const uint CopyMark = 0x8000_0000u;
        /// <summary>A copy's slot: past any column, so the place job's easing by slot leaves it alone.</summary>
        public const ushort NoSlot = 0xFFFF;

        /// <summary>Whether <paramref name="r"/> is a skier on a run, the only kind copied.</summary>
        public static bool IsSource(in SkierRender r) => r.State == (byte)GuestState.Skiing && !r.OnALift && r.ExitTick != r.EntryTick;

        /// <summary>
        /// Pads <paramref name="records"/> from <paramref name="count"/> up to <paramref name="target"/> (at most its
        /// length) and returns the new count; unchanged when nobody is on a run. <paramref name="now"/> is the column's
        /// tick from the start of the day (the records' clock), so every copy is somewhere on its run now.
        /// </summary>
        public static int Pad(NativeArray<SkierRender> records, int count, int target, uint now)
        {
            if (target > records.Length) target = records.Length;
            if (target <= count) return count;
            int sources = 0;
            for (int i = 0; i < count; i++) if (IsSource(records[i])) sources++;
            if (sources == 0) return count;
            int n = count;
            // Copy k of a skier is placed by its serial and k, never by the order, so it stays at the same point of its
            // skier's run from one column to the next however the others change. Right after a crowd spawns few are on a
            // run yet, so a skier may have thousands of copies.
            for (uint k = 1; n < target; k++)
            {
                for (int i = 0; i < count && n < target; i++)
                {
                    var r = records[i];
                    if (!IsSource(r)) continue;
                    uint span = r.ExitTick - r.EntryTick;
                    uint shift = (uint)((ulong)(Mix(r.Serial ^ (k * 0x9E3779B9u)) & 0xFFFF) * span >> 16);
                    if (shift > r.EntryTick) shift = r.EntryTick;   // never before the day began
                    uint entry = r.EntryTick - shift, exit = r.ExitTick - shift;
                    if (now > exit) { entry += span; exit += span; }   // past its end: start again a run behind
                    records[n] = new SkierRender(CopyMark | (uint)n, r.Segment, 0, NoSlot, r.Node, entry, exit,
                                                 (byte)((r.Lane + k) & 7), r.State, r.Band, r.Satisfaction);
                    n++;
                }
            }
            return n;
        }

        static uint Mix(uint x)
        {
            x ^= x >> 16; x *= 0x7FEB352Du;
            x ^= x >> 15; x *= 0x846CA68Bu;
            x ^= x >> 16;
            return x;
        }
    }
}
