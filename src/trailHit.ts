import { makeFrame, pointSegmentDistance, toMeters } from './network';
import type { SavedTrail } from './types/trails';

/** Pick one semantic run from overlapping hit polygons. Stable one-metre ties
 * cycle after the selected run so every coincident run remains reachable. */
export function resolveTrailHitId(
  trails: readonly SavedTrail[],
  candidateIds: readonly string[],
  click: [number, number],
  selectedId: string | null,
): string | null {
  const ids = [...new Set(candidateIds)].sort();
  if (!ids.length) return null;
  const frame = makeFrame([click]);
  const target = toMeters(frame, click);
  const distances = ids.map((id) => {
    const trail = trails.find((entry) => entry.id === id);
    let distanceM = Infinity;
    for (const part of trail?.parts ?? []) for (let index = 1; index < part.centerline.length; index++) {
      distanceM = Math.min(distanceM, pointSegmentDistance(target,
        toMeters(frame, part.centerline[index - 1]), toMeters(frame, part.centerline[index])).d);
    }
    return { id, distanceM };
  }).filter((entry) => Number.isFinite(entry.distanceM))
    .sort((left, right) => left.distanceM - right.distanceM || left.id.localeCompare(right.id));
  const best = distances[0];
  if (!best) return null;
  const tied = distances.filter((entry) => entry.distanceM <= best.distanceM + 1)
    .map((entry) => entry.id).sort();
  const selectedIndex = selectedId ? tied.indexOf(selectedId) : -1;
  return selectedIndex >= 0 ? tied[(selectedIndex + 1) % tied.length] : tied[0];
}
