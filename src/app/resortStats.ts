import type { SavedLift, SavedTrail } from '../types';

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
    for (const e of t.spineElevM) {
      if (typeof e === 'number' && Number.isFinite(e)) elevs.push(e);
    }
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
}

/** Run count + total run length across all painted runs. */
export function resortTrailTotals(trails: SavedTrail[]): ResortTrailTotals {
  let totalLengthM = 0;
  for (const t of trails) totalLengthM += t.lengthM;
  return { count: trails.length, totalLengthM };
}
