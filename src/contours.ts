// Builds zoom-adaptive contour tiers from a real-world height grid: traces
// every isoline once at the finest shared interval (marchingSquares.ts),
// then classifies each line by which display tier it belongs to. Levels
// are computed in feet, anchored to absolute elevation (multiples of the
// interval from sea level) rather than the terrain's local min/max.
import { traceContours, type ContourSegment } from './marchingSquares';
import { buildTileIndex as buildGenericTileIndex, queryTileIndex as queryGenericTileIndex, type TileIndex as GenericTileIndex } from './tileIndex';
import { thinLabelsBySpacing, type WorldLabel } from './labels';
export type { ContourSegment } from './marchingSquares';

const METERS_TO_FEET = 3.280839895;

// Labels are only placed this far apart (in world/map units) along any one
// elevation level, so a long contour doesn't get a label every segment.
const LABEL_MIN_SPACING = 260;

// Segments are spatially binned into a tilesPerAxis x tilesPerAxis grid so
// rendering only has to touch segments actually in view. At the mesh
// densities this app now requests (millions of segments for a large,
// detailed mountain), iterating the full array every frame — even with a
// per-segment visibility check — is what was actually causing multi-second
// frame stalls, not the tracing itself (which is comparatively cheap, well
// under a second even at 4M grid points).
export const TILES_PER_AXIS = 40;

export interface ContourTierConfig {
  majorFt: number;
  minorFt: number;
  /** camera.zoom at/above which this tier becomes the active one. */
  minZoom: number;
}

// Tier list as data so adding another (e.g. a closer-in 50ft/10ft tier once
// trail-planning needs it) is a one-line change. Adjacent tiers cross-fade
// over a zoom band around each boundary (see blendToNextTier) rather than
// switching abruptly.
export const CONTOUR_TIERS: ContourTierConfig[] = [
  { majorFt: 1000, minorFt: 200, minZoom: 0 },
  { majorFt: 100, minorFt: 20, minZoom: 1.0 },
];

// How wide (in zoom units) the cross-fade band around a tier boundary is.
export const TIER_BLEND_BAND = 0.35;

const FINEST_INTERVAL_FT = Math.min(...CONTOUR_TIERS.map((t) => t.minorFt));

export type ContourLabel = WorldLabel;

export type TileIndex = GenericTileIndex<ContourSegment>;

export interface ContourTier {
  majorIndex: TileIndex;
  minorIndex: TileIndex;
  labels: ContourLabel[];
  minZoom: number;
}

const buildTileIndex = buildGenericTileIndex;

/** Segments whose tile(s) overlap the given world-space rectangle. A
 * segment spanning multiple tiles may appear more than once — harmless for
 * stroking (a redundant stroke of the same hairline), and cheaper than
 * deduplicating on every frame. Re-exported from tileIndex.ts so existing
 * callers don't need to change their import. */
export const queryTileIndex = queryGenericTileIndex;

function buildLabelsForLevel(segments: ContourSegment[], level: number): ContourLabel[] {
  const text = `${Math.round(level).toLocaleString()}ft`;

  const candidates: ContourLabel[] = segments.map((seg) => {
    let angle = Math.atan2(seg.y2 - seg.y1, seg.x2 - seg.x1);
    if (angle > Math.PI / 2 || angle < -Math.PI / 2) angle += Math.PI;
    return { x: (seg.x1 + seg.x2) / 2, y: (seg.y1 + seg.y2) / 2, angle, text };
  });

  return thinLabelsBySpacing(candidates, LABEL_MIN_SPACING);
}

function buildLabels(majorSegments: ContourSegment[]): ContourLabel[] {
  const byLevel = new Map<number, ContourSegment[]>();
  for (const seg of majorSegments) {
    const level = Math.round(seg.level);
    const bucket = byLevel.get(level);
    if (bucket) bucket.push(seg);
    else byLevel.set(level, [seg]);
  }

  const labels: ContourLabel[] = [];
  for (const [level, segs] of byLevel) {
    labels.push(...buildLabelsForLevel(segs, level));
  }
  return labels;
}

/**
 * Trace and classify every contour tier for a terrain. Call once per
 * terrain (cache the result), not per frame — segments are static
 * world-space line data that the renderer strokes fresh each frame under
 * the current camera transform.
 */
export function buildContourTiers(heightsMeters: number[], gridSize: number, mapSize: number): ContourTier[] {
  const heightsFt = heightsMeters.map((h) => h * METERS_TO_FEET);
  const allSegments = traceContours(heightsFt, gridSize, mapSize, FINEST_INTERVAL_FT);

  return CONTOUR_TIERS.map((tier) => {
    const major: ContourSegment[] = [];
    const minor: ContourSegment[] = [];
    for (const seg of allSegments) {
      const level = Math.round(seg.level);
      if (level % tier.majorFt === 0) major.push(seg);
      else if (level % tier.minorFt === 0) minor.push(seg);
    }
    return {
      majorIndex: buildTileIndex(major, mapSize, TILES_PER_AXIS),
      minorIndex: buildTileIndex(minor, mapSize, TILES_PER_AXIS),
      labels: buildLabels(major),
      minZoom: tier.minZoom,
    };
  });
}

/** The entry in a `{ minZoom }[]` list whose threshold the given zoom satisfies. */
export function pickActiveTier<T extends { minZoom: number }>(tiers: T[], zoom: number): T {
  let active = tiers[0];
  for (const tier of tiers) {
    if (zoom >= tier.minZoom) active = tier;
  }
  return active;
}

export interface TierBlend {
  tier: ContourTier;
  /** 0 = fully faded in as the "from" tier, 1 = fully faded in as the "to" tier. */
  weight: number;
}

/**
 * The tier(s) that should be drawn for a given zoom, with a cross-fade
 * weight — within a band around a tier boundary this returns both the
 * outgoing and incoming tier so the caller can blend their opacity,
 * instead of an abrupt swap the instant zoom crosses the threshold.
 */
export function blendedTiers(tiers: ContourTier[], zoom: number): TierBlend[] {
  for (let i = 0; i < tiers.length - 1; i++) {
    const boundary = tiers[i + 1].minZoom;
    const lo = boundary - TIER_BLEND_BAND;
    const hi = boundary + TIER_BLEND_BAND;
    if (zoom >= lo && zoom <= hi) {
      const t = (zoom - lo) / (hi - lo);
      return [
        { tier: tiers[i], weight: 1 - t },
        { tier: tiers[i + 1], weight: t },
      ];
    }
  }
  return [{ tier: pickActiveTier(tiers, zoom), weight: 1 }];
}
