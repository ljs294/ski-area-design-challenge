import { SNOW_SURFACE_POWDER } from '../snow';
import type { SnowGrid } from '../types/snow';
import type { SnowAddResult } from '../types/dualClock';

/** Matches the packed snow-grid ceiling used by the natural snow model. */
export const MAX_SNOW_DEPTH_M = Math.fround(4.095);

/**
 * Apply a development blanket without advancing the weather model. Existing
 * over-cap cells are preserved verbatim, rather than being reduced to the cap.
 */
export function addFreshSnow(grid: SnowGrid, meters: number): { grid: SnowGrid; result: SnowAddResult } {
  if (!Number.isFinite(meters) || meters <= 0) throw new Error('Snow addition must be a positive finite number of meters.');
  const next = { ...grid, depthM: grid.depthM.slice(), surface: grid.surface.slice() };
  let affectedCells = 0, clippedCells = 0;
  for (let index = 0; index < next.depthM.length; index++) {
    const before = next.depthM[index]!;
    const after = before >= MAX_SNOW_DEPTH_M ? before : Math.min(MAX_SNOW_DEPTH_M, before + meters);
    const deposited = after - before;
    if (deposited > 0) {
      next.depthM[index] = after;
      affectedCells++;
      if (before + meters > MAX_SNOW_DEPTH_M) clippedCells++;
    } else if (before + meters > MAX_SNOW_DEPTH_M) clippedCells++;
    // The operation describes fresh snowfall across the entire mountain.
    next.surface[index] = SNOW_SURFACE_POWDER;
  }
  return { grid: next, result: { requestedMeters: meters, affectedCells, clippedCells } };
}
