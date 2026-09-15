import { SNOW_SURFACE_POWDER } from '../snow';
import type { SnowGrid } from '../types/snow';
import type { SnowAddArea, SnowAddResult } from '../types/dualClock';

/** Matches the packed snow-grid ceiling used by the natural snow model. */
export const MAX_SNOW_DEPTH_M = Math.fround(4.095);

/**
 * Apply a development blanket without advancing the weather model. Existing
 * over-cap cells are preserved verbatim, rather than being reduced to the cap.
 */
export function addFreshSnow(grid: SnowGrid, meters: number, area?: SnowAddArea): { grid: SnowGrid; result: SnowAddResult } {
  if (!Number.isFinite(meters) || meters <= 0) throw new Error('Snow addition must be a positive finite number of meters.');
  if (area && (!Number.isFinite(area.center[0]) || !Number.isFinite(area.center[1])
    || !Number.isFinite(area.radiusM) || area.radiusM <= 0)) {
    throw new Error('Localized snow requires a finite center and positive radius.');
  }
  const next = { ...grid, depthM: grid.depthM.slice(), surface: grid.surface.slice() };
  let affectedCells = 0, clippedCells = 0;
  for (let index = 0; index < next.depthM.length; index++) {
    if (area) {
      const x = index % grid.width, y = Math.floor(index / grid.width);
      const lng = grid.bounds.west + (grid.bounds.east - grid.bounds.west) * x / Math.max(1, grid.width - 1);
      const lat = grid.bounds.north - (grid.bounds.north - grid.bounds.south) * y / Math.max(1, grid.height - 1);
      const eastM = (lng - area.center[0]) * 111_320 * Math.cos((lat + area.center[1]) * Math.PI / 360);
      const northM = (lat - area.center[1]) * 111_320;
      if (eastM * eastM + northM * northM > area.radiusM * area.radiusM) continue;
    }
    const before = next.depthM[index]!;
    const after = before >= MAX_SNOW_DEPTH_M ? before : Math.min(MAX_SNOW_DEPTH_M, before + meters);
    const deposited = after - before;
    if (deposited > 0) {
      next.depthM[index] = after;
      affectedCells++;
      if (before + meters > MAX_SNOW_DEPTH_M) clippedCells++;
    } else if (before + meters > MAX_SNOW_DEPTH_M) clippedCells++;
    next.surface[index] = SNOW_SURFACE_POWDER;
  }
  return { grid: next, result: { requestedMeters: meters, affectedCells, clippedCells } };
}
