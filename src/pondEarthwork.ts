import {
  boxOf, EARTHWORK_CREST_WIDTH_M, EARTHWORK_CUT_SLOPE, EARTHWORK_FREEBOARD_M,
  earthworkTerrainPatch, localMeters, OUTER_FACE_SLOPE, signedDistanceM, solveEarthwork,
  terrainMetrics, validElevation, WATER_FACE_SLOPE,
  type EarthworkTerrainPatch, type Point, type TerrainMetrics, type XY,
} from './earthwork';
import { bilinearAt } from './earthwork';
import type { TerrainRecord } from './types/terrain';

/** The pond's share of the shared earthwork spec, re-exported under pond names
 * so the panel can quote the berm it is about to build. */
export const POND_FREEBOARD_M = EARTHWORK_FREEBOARD_M;
export const POND_CREST_WIDTH_M = EARTHWORK_CREST_WIDTH_M;
export const POND_INNER_SLOPE = WATER_FACE_SLOPE;
export const POND_OUTER_SLOPE = OUTER_FACE_SLOPE;
export const POND_CUT_SLOPE = EARTHWORK_CUT_SLOPE;
export const MAX_POND_EXCAVATION_M = 15;
/** Beyond this the structure stops being a pond berm and becomes a dam, which
 * the game builds with the dam tool against a real stream. */
export const MAX_POND_BERM_HEIGHT_M = 20;

export interface PondEarthworkOptions {
  topElevationM: number;
  /** How far the floor is dug below full pool. Zero leaves natural ground. */
  excavationDepthM?: number;
  /** Geodesic boundary area, used to scale the grid-integrated pool volume. */
  poolAreaM2: number;
}

export interface PondEarthworkDesign {
  crestElevationM: number;
  floorElevationM: number;
  excavationDepthM: number;
  cutM3: number;
  fillM3: number;
  /** Positive means surplus cut to haul away; negative means imported fill. */
  balanceM3: number;
  maxBermHeightM: number;
  maxCutDepthM: number;
  /** Shoreline length that needs an embankment to hold full pool. */
  bermLengthM: number;
  disturbedAreaM2: number;
  capacityM3: number;
  averageDepthM: number;
  maxDepthM: number;
  /** Grid samples inside the boundary, and how many carried usable elevation. */
  coveredSamples: number;
  validSamples: number;
  /** True when a face still had not reached natural ground at the reach limit,
   * so the surface would commit as a wall rather than a graded slope. */
  truncated: boolean;
  patchIndices: Uint32Array;
  patchHeights: Float32Array;
}

/**
 * Design surface at a point `distanceM` outboard of the drawn shoreline
 * (negative inside the pool), given the natural ground there.
 *
 * Two envelopes bracket the natural ground and the ground is clamped between
 * them. The fill envelope is the embankment: it climbs from the waterline at
 * the inner face slope, runs flat across the crest, then lays back down the
 * outer face until it daylights. The cut envelope is the excavation: inside the
 * pool it drops at the inner face slope to the floor, and outside it climbs the
 * back slope until it meets rising ground. Because the inner face is flatter
 * than the back slope, the two envelopes never cross, so `max(fill, min(ground,
 * cut))` is a single continuous surface: berm where the hill falls away, cut
 * where it rises, untouched ground everywhere beyond the daylight lines.
 */
function designElevationAt(distanceM: number, groundM: number,
  waterM: number, crestM: number, floorM: number): number {
  const crestOffsetM = POND_FREEBOARD_M * POND_INNER_SLOPE;
  const fillM = distanceM <= crestOffsetM
    ? waterM + distanceM / POND_INNER_SLOPE
    : distanceM <= crestOffsetM + POND_CREST_WIDTH_M
      ? crestM
      : crestM - (distanceM - crestOffsetM - POND_CREST_WIDTH_M) / POND_OUTER_SLOPE;
  const cutM = distanceM <= 0
    ? Math.max(floorM, waterM + distanceM / POND_INNER_SLOPE)
    : waterM + distanceM / POND_CUT_SLOPE;
  return Math.max(fillM, Math.min(groundM, cutM));
}

interface PoolTally {
  coveredSamples: number;
  validSamples: number;
  depthSumM: number;
  maxDepthM: number;
}

export function designPondEarthwork(record: TerrainRecord, boundary: Point[],
  options: PondEarthworkOptions): PondEarthworkDesign | null {
  const metrics = terrainMetrics(record);
  if (!metrics) return null;
  if (boundary.length < 4 || !validElevation(options.topElevationM)) return null;

  const waterM = options.topElevationM;
  const excavationDepthM = Math.max(0,
    Math.min(MAX_POND_EXCAVATION_M, options.excavationDepthM ?? 0));
  const crestM = waterM + POND_FREEBOARD_M;
  const floorM = waterM - excavationDepthM;

  const ring: XY[] = boundary.map((point) => localMeters(metrics, point));
  if (ring[0].x !== ring[ring.length - 1].x || ring[0].y !== ring[ring.length - 1].y)
    ring.push({ ...ring[0] });
  const box = boxOf(ring);

  let pool: PoolTally = { coveredSamples: 0, validSamples: 0, depthSumM: 0, maxDepthM: 0 };
  const seedReachM = POND_FREEBOARD_M * POND_INNER_SLOPE + POND_CREST_WIDTH_M + 4 * metrics.cellM;
  const sweep = solveEarthwork(metrics, box, seedReachM, () => {
    const tally: PoolTally = { coveredSamples: 0, validSamples: 0, depthSumM: 0, maxDepthM: 0 };
    pool = tally;
    return {
      // Distance to the boundary's bounding box is a lower bound on distance to
      // the boundary itself, and both envelopes only relax as distance grows —
      // so a sample the box already leaves alone needs no per-edge distance.
      untouchedBeyond: (outsideM, groundM) => !validElevation(groundM) ||
        designElevationAt(outsideM, groundM, waterM, crestM, floorM) === groundM,
      elevationAt: (x, y, groundM) => {
        const distanceM = signedDistanceM(ring, x, y);
        if (distanceM <= 0) tally.coveredSamples++;
        if (!validElevation(groundM)) return groundM;
        const designM = designElevationAt(distanceM, groundM, waterM, crestM, floorM);
        if (distanceM <= 0) {
          tally.validSamples++;
          const depthM = Math.max(0, waterM - designM);
          tally.depthSumM += depthM;
          tally.maxDepthM = Math.max(tally.maxDepthM, depthM);
        }
        return designM;
      },
    };
  });

  const sampledPoolAreaM2 = pool.validSamples * metrics.cellAreaM2;
  const capacityM3 = sampledPoolAreaM2 > 0
    ? pool.depthSumM * metrics.cellAreaM2 * options.poolAreaM2 / sampledPoolAreaM2 : 0;

  return {
    crestElevationM: crestM,
    floorElevationM: floorM,
    excavationDepthM,
    cutM3: sweep.cutM3,
    fillM3: sweep.fillM3,
    balanceM3: sweep.cutM3 - sweep.fillM3,
    maxBermHeightM: sweep.maxFillM,
    maxCutDepthM: sweep.maxCutM,
    bermLengthM: bermLengthM(ring, metrics, crestM),
    disturbedAreaM2: sweep.disturbedCells * metrics.cellAreaM2,
    capacityM3,
    averageDepthM: options.poolAreaM2 > 0 ? capacityM3 / options.poolAreaM2 : 0,
    maxDepthM: pool.maxDepthM,
    coveredSamples: pool.coveredSamples,
    validSamples: pool.validSamples,
    truncated: sweep.clipped,
    patchIndices: Uint32Array.from(sweep.indices),
    patchHeights: Float32Array.from(sweep.heights),
  };
}

/** Shoreline length where natural ground sits below the crest — the stretch
 * that has to be built up rather than dug into. */
function bermLengthM(ring: XY[], metrics: TerrainMetrics, crestM: number): number {
  let lengthM = 0;
  for (let i = 1; i < ring.length; i++) {
    const a = ring[i - 1], b = ring[i];
    const spanM = Math.hypot(b.x - a.x, b.y - a.y);
    if (spanM < 1e-6) continue;
    const steps = Math.max(1, Math.ceil(spanM / metrics.cellM));
    for (let step = 0; step < steps; step++) {
      const t = (step + 0.5) / steps;
      const groundM = bilinearAt(metrics.heights, metrics.n,
        (a.x + (b.x - a.x) * t) / metrics.dxM, (a.y + (b.y - a.y) * t) / metrics.dyM);
      if (groundM != null && groundM < crestM - 0.05) lengthM += spanM / steps;
    }
  }
  return lengthM;
}

export type PondTerrainPatch = EarthworkTerrainPatch;

/**
 * Turn a design into the elevation patch the terrain package commits, complete
 * with re-traced contours and the disturbed footprint the cover edit clears.
 */
export function pondTerrainPatch(record: TerrainRecord,
  design: PondEarthworkDesign): PondTerrainPatch {
  return earthworkTerrainPatch(record, design.patchIndices, design.patchHeights);
}
