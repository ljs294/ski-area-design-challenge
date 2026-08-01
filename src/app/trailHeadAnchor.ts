import { liftStats } from '../lifts';
import { makeFrame, pointSegmentDistance, toMeters } from '../network';
import type { AnchorRef } from '../skiNodes';
import type { SavedLift, SavedTrail } from '../types';

export type TrailHeadAnchor = Extract<AnchorRef, { kind: 'lift' | 'trail' }>;
export type TrailTailAnchor = TrailHeadAnchor;

/**
 * Snap a prospective trailhead onto the nearest supported graph target. Lift
 * terminals win exact-distance ties so a terminal sitting on a run remains an
 * explicit lift connection.
 */
export function nearestTrailHeadAnchor(
  click: [number, number],
  lifts: SavedLift[],
  trails: SavedTrail[],
  maxDistanceM: number,
): TrailHeadAnchor | null {
  return nearestTrailEndpointAnchor(click, lifts, trails, maxDistanceM, 'head');
}

export function nearestTrailTailAnchor(
  click: [number, number], lifts: SavedLift[], trails: SavedTrail[], maxDistanceM: number,
): TrailTailAnchor | null {
  return nearestTrailEndpointAnchor(click, lifts, trails, maxDistanceM, 'tail');
}

function nearestTrailEndpointAnchor(
  click: [number, number], lifts: SavedLift[], trails: SavedTrail[], maxDistanceM: number,
  endpoint: 'head' | 'tail',
): TrailHeadAnchor | null {
  const frame = makeFrame([click]);
  const pm = toMeters(frame, click);
  let best: { anchor: TrailHeadAnchor; distanceM: number; priority: number } | null = null;
  const consider = (anchor: TrailHeadAnchor, distanceM: number, priority: number) => {
    if (distanceM > maxDistanceM) return;
    if (!best || distanceM < best.distanceM - 1e-9 ||
        (Math.abs(distanceM - best.distanceM) <= 1e-9 && priority < best.priority)) {
      best = { anchor, distanceM, priority };
    }
  };

  for (const lift of lifts) {
    const flip = liftStats(lift.points, lift.endpointElevM).topIndex === 0;
    const terminals: { end: 'base' | 'top'; point: [number, number] }[] = [
      { end: 'base', point: flip ? lift.points[1] : lift.points[0] },
      { end: 'top', point: flip ? lift.points[0] : lift.points[1] },
    ];
    for (const terminal of terminals) {
      if (terminal.end !== (endpoint === 'head' ? 'top' : 'base')) continue;
      const m = toMeters(frame, terminal.point);
      consider({ kind: 'lift', liftId: lift.id, ...terminal },
        Math.hypot(m.x - pm.x, m.y - pm.y), 0);
    }
  }

  for (const trail of trails) {
    for (const part of trail.parts) {
      for (let i = 0; i < part.centerline.length - 1; i++) {
        const a = toMeters(frame, part.centerline[i]);
        const b = toMeters(frame, part.centerline[i + 1]);
        const { d, u } = pointSegmentDistance(pm, a, b);
        const point: [number, number] = [
          part.centerline[i][0] + (part.centerline[i + 1][0] - part.centerline[i][0]) * u,
          part.centerline[i][1] + (part.centerline[i + 1][1] - part.centerline[i][1]) * u,
        ];
        consider({ kind: 'trail', trailId: trail.id, point }, d, 1);
      }
    }
  }

  return (best as { anchor: TrailHeadAnchor } | null)?.anchor ?? null;
}
