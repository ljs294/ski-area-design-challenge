import type { SavedLift } from '../types';

export interface ResortElevations {
  /** Highest lift top terminal, meters. null until any elevation resolves. */
  summitM: number | null;
  /** Lowest lift bottom terminal, meters. null until any elevation resolves. */
  baseM: number | null;
  /** summit − base, meters. null until both resolve. */
  verticalM: number | null;
}

/**
 * Resort-scale summit/base/vertical, derived from the lift network. Summit is
 * the highest terminal of any lift, base the lowest; both draw from every
 * resolved endpoint elevation, so a single lift already yields a vertical drop.
 */
export function resortElevations(lifts: SavedLift[]): ResortElevations {
  const elevs: number[] = [];
  for (const l of lifts) {
    for (const e of l.endpointElevM) {
      if (typeof e === 'number' && Number.isFinite(e)) elevs.push(e);
    }
  }
  if (elevs.length === 0) return { summitM: null, baseM: null, verticalM: null };
  const summitM = Math.max(...elevs);
  const baseM = Math.min(...elevs);
  return { summitM, baseM, verticalM: summitM - baseM };
}
