import type { SavedDam, SavedLift, SavedPond, SavedTrail } from '../types';

export interface ResortElevations {
  /** Highest resolved elevation across lifts + runs, meters. null until any resolves. */
  summitM: number | null;
  /** Lowest resolved elevation across lifts + runs, meters. null until any resolves. */
  baseM: number | null;
  /** summit − base, meters. null until both resolve. */
  verticalM: number | null;
}

/**
 * Resort-scale summit/base/vertical, derived from the lift network and painted
 * runs together. Summit is the highest resolved elevation of any lift terminal
 * or run station, base the lowest — so a resort with runs but no lifts (or vice
 * versa) still yields a vertical drop.
 */
export function resortElevations(lifts: SavedLift[], trails: SavedTrail[] = []): ResortElevations {
  const elevs: number[] = [];
  for (const l of lifts) {
    for (const e of l.endpointElevM) {
      if (typeof e === 'number' && Number.isFinite(e)) elevs.push(e);
    }
  }
  for (const t of trails) {
    for (const part of t.parts) for (const e of part.centerlineElevM)
      if (typeof e === 'number' && Number.isFinite(e)) elevs.push(e);
  }
  if (elevs.length === 0) return { summitM: null, baseM: null, verticalM: null };
  const summitM = Math.max(...elevs);
  const baseM = Math.min(...elevs);
  return { summitM, baseM, verticalM: summitM - baseM };
}

export interface ResortTrailTotals {
  count: number;
  /** Sum of every run's slope length, meters. */
  totalLengthM: number;
  /**
   * Sum of every run's painted footprint, square meters — the resort's skiable
   * acreage. Each run contributes the same `areaM2` its own panel reports, so
   * two runs painted across each other count their shared ground twice; that
   * matches how the trails list adds up and keeps the total explainable
   * run-by-run rather than silently differing from the parts.
   */
  totalAreaM2: number;
}

/** Run count, total run length and total skiable area across all painted runs. */
export function resortTrailTotals(trails: SavedTrail[]): ResortTrailTotals {
  let totalLengthM = 0;
  let totalAreaM2 = 0;
  for (const t of trails) {
    totalLengthM += t.lengthM;
    totalAreaM2 += t.areaM2;
  }
  return { count: trails.length, totalLengthM, totalAreaM2 };
}

/** Full-pool storage available to the resort snowmaking system. */
export function snowmakingWaterCapacityM3(dams: SavedDam[], ponds: SavedPond[]): number {
  return dams.reduce((sum, dam) => sum + dam.capacityM3, 0) +
    ponds.reduce((sum, pond) => sum + (pond.isSnowmaking !== false ? pond.capacityM3 : 0), 0);
}
