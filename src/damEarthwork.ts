import {
  boxOf, EARTHWORK_CREST_WIDTH_M, EARTHWORK_FREEBOARD_M, fromLocalMeters, groundAt,
  localMeters, OUTER_FACE_SLOPE, segmentOffset, solveEarthwork, terrainMetrics,
  validElevation, WATER_FACE_SLOPE,
  type Point, type TerrainMetrics, type XY,
} from './earthwork';
import type { TerrainRecord } from './types';

/** Freeboard and crest are the pond berm's, so a dam and a pond edge are the
 * same structure at different scales. */
export const DAM_FREEBOARD_M = EARTHWORK_FREEBOARD_M;
export const DAM_CREST_WIDTH_M = EARTHWORK_CREST_WIDTH_M;
export const DAM_UPSTREAM_SLOPE = WATER_FACE_SLOPE;
export const DAM_DOWNSTREAM_SLOPE = OUTER_FACE_SLOPE;
/** Past this an earth-fill embankment stops being something a resort builds
 * with scrapers, and the alignment wants to move to a narrower notch. */
export const MAX_DAM_HEIGHT_M = 40;

export interface DamEmbankmentDesign {
  /** Top of the dam: full pool plus freeboard. */
  crestElevationM: number;
  waterElevationM: number;
  /** Length of the alignment the player drew. */
  lengthM: number;
  /** How much of it actually stands above natural ground. */
  builtLengthM: number;
  maxHeightM: number;
  averageHeightM: number;
  cutM3: number;
  fillM3: number;
  /** Positive means surplus cut; an embankment is fill, so this runs negative. */
  balanceM3: number;
  disturbedAreaM2: number;
  /** True when a face had not reached natural ground at the reach limit, so the
   * surface would commit as a wall rather than a graded slope. */
  truncated: boolean;
  /** Deck polygon along the built stretch, for drawing the dam in plan. */
  crestRing: Point[];
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
}

/**
 * Grade an earth-fill embankment along the dam alignment.
 *
 * The design surface is `max(ground, embankment)`: the crest deck runs flat at
 * full pool plus freeboard, the upstream face lays back into the reservoir at
 * the pond berm's water-face slope, and the downstream face falls at the drier
 * outer slope until it daylights on natural ground. Because the surface only
 * ever rises to meet the crest, the dam ties itself into both abutments — where
 * the hillside is already above the crest nothing is built at all.
 *
 * `upstreamSide` is the flank of the alignment the reservoir sits on, as
 * `segmentOffset` reports it; the caller knows it from the basin it flooded.
 */
export function designDamEmbankment(record: TerrainRecord,
  points: [Point, Point], waterElevationM: number,
  upstreamSide: 1 | -1): DamEmbankmentDesign | null {
  const metrics = terrainMetrics(record);
  if (!metrics || !validElevation(waterElevationM)) return null;
  const a = localMeters(metrics, points[0]), b = localMeters(metrics, points[1]);
  const lengthM = Math.hypot(b.x - a.x, b.y - a.y);
  if (!(lengthM > 0)) return null;

  const crestM = waterElevationM + DAM_FREEBOARD_M;
  const halfCrestM = DAM_CREST_WIDTH_M / 2;
  const embankmentAt = (distanceM: number, side: 1 | -1 | 0): number => {
    if (distanceM <= halfCrestM) return crestM;
    const slope = side === upstreamSide ? DAM_UPSTREAM_SLOPE : DAM_DOWNSTREAM_SLOPE;
    return crestM - (distanceM - halfCrestM) / slope;
  };

  const sweep = solveEarthwork(metrics, boxOf([a, b]), halfCrestM + 8 * metrics.cellM, () => ({
    // The flatter of the two faces bounds how far any fill can reach, so this
    // stays conservative whichever flank the sample is on.
    untouchedBeyond: (outsideM, groundM) => !validElevation(groundM) ||
      crestM - Math.max(0, outsideM - halfCrestM) / DAM_UPSTREAM_SLOPE <= groundM,
    elevationAt: (x, y, groundM) => {
      const offset = segmentOffset(a, b, x, y);
      return Math.max(groundM, embankmentAt(offset.distanceM, offset.side));
    },
  }));

  const profile = alignmentProfile(metrics, a, b, lengthM, crestM);
  return {
    crestElevationM: crestM,
    waterElevationM,
    lengthM,
    builtLengthM: profile.builtLengthM,
    maxHeightM: profile.maxHeightM,
    averageHeightM: profile.averageHeightM,
    cutM3: sweep.cutM3,
    fillM3: sweep.fillM3,
    balanceM3: sweep.cutM3 - sweep.fillM3,
    disturbedAreaM2: sweep.disturbedCells * metrics.cellAreaM2,
    truncated: sweep.clipped,
    crestRing: profile.crestRing,
    patchIndices: Uint32Array.from(sweep.indices),
    patchHeights: Float32Array.from(sweep.heights),
  };
}

interface AlignmentProfile {
  builtLengthM: number;
  maxHeightM: number;
  averageHeightM: number;
  crestRing: Point[];
}

/**
 * Walk the alignment to measure the structure and cut the deck polygon. Heights
 * are read off the alignment rather than the nearest grid samples so the
 * average can never exceed the maximum on a coarse grid.
 */
function alignmentProfile(metrics: TerrainMetrics, a: XY, b: XY,
  lengthM: number, crestM: number): AlignmentProfile {
  const steps = Math.max(16, Math.ceil(lengthM / metrics.cellM));
  const ex = (b.x - a.x) / lengthM, ey = (b.y - a.y) / lengthM;
  const perp: XY = { x: -ey, y: ex };
  const stations: { point: XY; heightM: number }[] = [];
  for (let i = 0; i <= steps; i++) {
    const t = i / steps;
    const point: XY = { x: a.x + (b.x - a.x) * t, y: a.y + (b.y - a.y) * t };
    const groundM = groundAt(metrics, point.x, point.y);
    stations.push({ point, heightM: groundM == null ? 0 : Math.max(0, crestM - groundM) });
  }
  // The deck is the longest unbroken stretch standing above natural ground;
  // anywhere else the alignment is already buried in the abutment.
  let bestStart = 0, bestEnd = -1, runStart = -1;
  for (let i = 0; i <= stations.length; i++) {
    const standing = i < stations.length && stations[i].heightM > 0.05;
    if (standing && runStart < 0) runStart = i;
    if (!standing && runStart >= 0) {
      if (i - 1 - runStart > bestEnd - bestStart) { bestStart = runStart; bestEnd = i - 1; }
      runStart = -1;
    }
  }
  const stepM = lengthM / steps;
  const standing = stations.filter((station) => station.heightM > 0.05);
  const half = DAM_CREST_WIDTH_M / 2;
  const deck = bestEnd > bestStart ? stations.slice(bestStart, bestEnd + 1) : [];
  const crestRing: Point[] = deck.length >= 2 ? [
    ...deck.map((station) => fromLocalMeters(metrics,
      { x: station.point.x + perp.x * half, y: station.point.y + perp.y * half })),
    ...[...deck].reverse().map((station) => fromLocalMeters(metrics,
      { x: station.point.x - perp.x * half, y: station.point.y - perp.y * half })),
  ] : [];
  if (crestRing.length) crestRing.push(crestRing[0]);
  return {
    builtLengthM: standing.length * stepM,
    maxHeightM: stations.reduce((best, station) => Math.max(best, station.heightM), 0),
    averageHeightM: standing.length
      ? standing.reduce((sum, station) => sum + station.heightM, 0) / standing.length : 0,
    crestRing,
  };
}
