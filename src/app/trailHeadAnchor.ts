import { liftStats } from '../lifts';
import { makeFrame, pointSegmentDistance, toMeters, type MetersFrame, type XY } from '../network';
import type { AnchorRef } from '../types/anchors';
import type { SavedLift, SavedTrail } from '../types';

export type TrailHeadAnchor = Extract<AnchorRef, { kind: 'lift' | 'trail' }>;
export type TrailTailAnchor = TrailHeadAnchor;

type LiftTarget = {
  kind: 'lift'; sequence: number; liftId: string; end: 'base' | 'top';
  point: [number, number]; meters: XY;
};
type TrailTarget = {
  kind: 'trail'; sequence: number; trailId: string;
  a: [number, number]; b: [number, number]; am: XY; bm: XY;
};
type AnchorTarget = LiftTarget | TrailTarget;

const DEFAULT_CELL_M = 60;
const cellKey = (x: number, y: number) => `${x}:${y}`;

/**
 * Immutable, resort-local lookup for trail endpoint snapping. Building the
 * index does the work that used to be repeated for every pointer event:
 * orienting lifts, projecting every centerline segment, and scanning the
 * complete trail network.
 */
export class TrailAnchorIndex {
  private readonly frame: MetersFrame;
  private readonly cells = new Map<string, AnchorTarget[]>();
  private readonly cellSizeM: number;

  constructor(
    lifts: readonly SavedLift[],
    trails: readonly SavedTrail[],
    cellSizeM = DEFAULT_CELL_M,
  ) {
    const samples = [
      ...lifts.flatMap((lift) => lift.points),
      ...trails.flatMap((trail) => trail.parts.flatMap((part) => part.centerline)),
    ];
    this.frame = makeFrame(samples);
    this.cellSizeM = Math.max(1, cellSizeM);
    let sequence = 0;

    for (const lift of lifts) {
      const flip = liftStats(lift.points, lift.endpointElevM).topIndex === 0;
      const terminals: Array<{ end: 'base' | 'top'; point: [number, number] }> = [
        { end: 'base', point: flip ? lift.points[1] : lift.points[0] },
        { end: 'top', point: flip ? lift.points[0] : lift.points[1] },
      ];
      for (const terminal of terminals) {
        const target: LiftTarget = { kind: 'lift', sequence: sequence++, liftId: lift.id,
          ...terminal, meters: toMeters(this.frame, terminal.point) };
        this.add(target, target.meters.x, target.meters.y, target.meters.x, target.meters.y);
      }
    }

    for (const trail of trails) for (const part of trail.parts) {
      for (let i = 0; i < part.centerline.length - 1; i++) {
        const a = part.centerline[i], b = part.centerline[i + 1];
        const am = toMeters(this.frame, a), bm = toMeters(this.frame, b);
        const target: TrailTarget = { kind: 'trail', sequence: sequence++, trailId: trail.id,
          a, b, am, bm };
        this.add(target, Math.min(am.x, bm.x), Math.min(am.y, bm.y),
          Math.max(am.x, bm.x), Math.max(am.y, bm.y));
      }
    }
  }

  nearestHead(click: [number, number], maxDistanceM: number): TrailHeadAnchor | null {
    return this.nearest(click, maxDistanceM, 'head');
  }

  nearestTail(click: [number, number], maxDistanceM: number): TrailTailAnchor | null {
    return this.nearest(click, maxDistanceM, 'tail');
  }

  /** Number of geometries considered by a query, exposed for deterministic
   * scale tests rather than timing-sensitive benchmarks. */
  candidateCount(click: [number, number], maxDistanceM: number): number {
    return this.candidates(click, maxDistanceM).length;
  }

  private add(target: AnchorTarget, minX: number, minY: number, maxX: number, maxY: number): void {
    const x0 = Math.floor(minX / this.cellSizeM), x1 = Math.floor(maxX / this.cellSizeM);
    const y0 = Math.floor(minY / this.cellSizeM), y1 = Math.floor(maxY / this.cellSizeM);
    for (let x = x0; x <= x1; x++) for (let y = y0; y <= y1; y++) {
      const key = cellKey(x, y), cell = this.cells.get(key);
      if (cell) cell.push(target);
      else this.cells.set(key, [target]);
    }
  }

  private candidates(click: [number, number], maxDistanceM: number): AnchorTarget[] {
    const point = toMeters(this.frame, click);
    const radius = Math.ceil(Math.max(0, maxDistanceM) / this.cellSizeM) + 1;
    const cx = Math.floor(point.x / this.cellSizeM), cy = Math.floor(point.y / this.cellSizeM);
    const found = new Set<AnchorTarget>();
    for (let x = cx - radius; x <= cx + radius; x++) {
      for (let y = cy - radius; y <= cy + radius; y++) {
        for (const target of this.cells.get(cellKey(x, y)) ?? []) found.add(target);
      }
    }
    return [...found].sort((a, b) => a.sequence - b.sequence);
  }

  private nearest(
    click: [number, number],
    maxDistanceM: number,
    endpoint: 'head' | 'tail',
  ): TrailHeadAnchor | null {
    // Retain the old click-local distance frame for exact compatibility. The
    // resort-local frame above is used only to find a small candidate set.
    const exactFrame = makeFrame([click]);
    const pm = toMeters(exactFrame, click);
    let best: { anchor: TrailHeadAnchor; distanceM: number; priority: number } | null = null;
    const consider = (anchor: TrailHeadAnchor, distanceM: number, priority: number) => {
      if (distanceM > maxDistanceM) return;
      if (!best || distanceM < best.distanceM - 1e-9 ||
          (Math.abs(distanceM - best.distanceM) <= 1e-9 && priority < best.priority)) {
        best = { anchor, distanceM, priority };
      }
    };

    for (const target of this.candidates(click, maxDistanceM)) {
      if (target.kind === 'lift') {
        if (target.end !== (endpoint === 'head' ? 'top' : 'base')) continue;
        const meters = toMeters(exactFrame, target.point);
        consider({ kind: 'lift', liftId: target.liftId, end: target.end, point: target.point },
          Math.hypot(meters.x - pm.x, meters.y - pm.y), 0);
        continue;
      }
      const a = toMeters(exactFrame, target.a), b = toMeters(exactFrame, target.b);
      const { d, u } = pointSegmentDistance(pm, a, b);
      const point: [number, number] = [
        target.a[0] + (target.b[0] - target.a[0]) * u,
        target.a[1] + (target.b[1] - target.a[1]) * u,
      ];
      consider({ kind: 'trail', trailId: target.trailId, point }, d, 1);
    }
    return (best as { anchor: TrailHeadAnchor } | null)?.anchor ?? null;
  }
}

/** Compatibility helpers for occasional one-shot consumers. Pointer-driven
 * trail construction owns and reuses a TrailAnchorIndex instead. */
export function nearestTrailHeadAnchor(
  click: [number, number],
  lifts: SavedLift[],
  trails: SavedTrail[],
  maxDistanceM: number,
): TrailHeadAnchor | null {
  return new TrailAnchorIndex(lifts, trails).nearestHead(click, maxDistanceM);
}

export function nearestTrailTailAnchor(
  click: [number, number], lifts: SavedLift[], trails: SavedTrail[], maxDistanceM: number,
): TrailTailAnchor | null {
  return new TrailAnchorIndex(lifts, trails).nearestTail(click, maxDistanceM);
}
