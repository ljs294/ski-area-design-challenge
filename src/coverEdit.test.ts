import { describe, expect, it } from 'vitest';
import {
  appendCorridorToDisplayGeometry,
  grasslandCodeFor,
  liftCorridorRing,
  stampCorridorIntoGrid,
  LIFT_CLEAR_HALF_WIDTH_M,
  LIFT_CLEAR_JITTER_M,
} from './coverEdit';
import { coverDisplayToGeoJSON, inspectCoverDisplayGeometry } from './coverDisplay';
import { checksumBytes, coverDisplayMetadataOf, float32Bytes } from './terrainPackage';
import { boundsForSquareMeters, unitToLngLat } from './geo';
import { TERRAIN_COVER_CODES } from './fourClassCover';
import type { CoverGrid } from './types';

// A 240 m box at 2 m cells (120×120), filled with a single class.
const BOUNDS = boundsForSquareMeters(47, -121.5, 240);
const N = 120;

function grid(source: CoverGrid['source'], fill: number): CoverGrid {
  return {
    bounds: BOUNDS,
    width: N,
    height: N,
    cellSizeM: 2,
    data: new Uint8Array(N * N).fill(fill),
    complete: true,
    nodataCount: 0,
    source,
    vintage: '2021',
  } as unknown as CoverGrid;
}

// Horizontal lift across the middle third of the box.
const LIFT: [[number, number], [number, number]] = [
  unitToLngLat(0.3, 0.5, BOUNDS),
  unitToLngLat(0.7, 0.5, BOUNDS),
];

function ringFor(seed = 'lift-1') {
  return liftCorridorRing(LIFT, BOUNDS, { halfWidthM: LIFT_CLEAR_HALF_WIDTH_M, jitterM: LIFT_CLEAR_JITTER_M, seed });
}

describe('grasslandCodeFor', () => {
  it('is 3 for the four-class product and 30 for raw WorldCover', () => {
    expect(grasslandCodeFor(grid('usgs-four-class-v1', 1))).toBe(3);
    expect(grasslandCodeFor(grid('esa-worldcover-2021-v200', 10))).toBe(30);
  });
});

describe('liftCorridorRing', () => {
  it('returns a closed ring of at least four points', () => {
    const ring = ringFor();
    expect(ring.length).toBeGreaterThanOrEqual(4);
    expect(ring[0]).toEqual(ring[ring.length - 1]);
  });

  it('is deterministic for a given seed and differs across seeds', () => {
    expect(ringFor('a')).toEqual(ringFor('a'));
    expect(ringFor('a')).not.toEqual(ringFor('b'));
  });

  it('is a simple polygon — no self-intersections at either end cap', () => {
    // A proper turn at each terminal, not a bowtie. Check every non-adjacent
    // edge pair for a crossing.
    const ring = ringFor();
    const cross = (o: number[], a: number[], b: number[]) => (b[0] - o[0]) * (a[1] - o[1]) - (b[1] - o[1]) * (a[0] - o[0]);
    const segsIntersect = (p1: number[], p2: number[], p3: number[], p4: number[]) => {
      const d1 = cross(p3, p4, p1);
      const d2 = cross(p3, p4, p2);
      const d3 = cross(p1, p2, p3);
      const d4 = cross(p1, p2, p4);
      return ((d1 > 0) !== (d2 > 0)) && ((d3 > 0) !== (d4 > 0));
    };
    const n = ring.length - 1; // last point duplicates the first
    let crossings = 0;
    for (let i = 0; i < n; i++) {
      for (let j = i + 2; j < n; j++) {
        if (i === 0 && j === n - 1) continue; // adjacent across the closing seam
        if (segsIntersect(ring[i], ring[i + 1], ring[j], ring[j + 1])) crossings++;
      }
    }
    expect(crossings).toBe(0);
  });

  it('holds the corridor within half-width ± jitter of the centreline', () => {
    const ring = ringFor();
    const mPerLat = 111320;
    const mPerLng = 111320 * Math.cos((LIFT[0][1] * Math.PI) / 180);
    // The centreline is horizontal (constant lat), so perpendicular distance is
    // just the north/south offset in metres from the line's latitude.
    const lineLat = LIFT[0][1];
    const a = LIFT[0];
    const b = LIFT[1];
    const minLng = Math.min(a[0], b[0]);
    const maxLng = Math.max(a[0], b[0]);
    for (const [lng, lat] of ring) {
      const perpM = Math.abs(lat - lineLat) * mPerLat;
      // Points beside the line stay within the jittered half-width; points near
      // the rounded caps may sit slightly past the ends but never wider.
      const withinSpan = lng >= minLng - 1 && lng <= maxLng + 1;
      if (withinSpan) {
        expect(perpM).toBeLessThanOrEqual(LIFT_CLEAR_HALF_WIDTH_M + LIFT_CLEAR_JITTER_M + 0.5);
      }
      // Nothing anywhere on the ring exceeds half-width + jitter from the axis.
      const alongOffset = Math.max(0, minLng - lng, lng - maxLng) * mPerLng;
      const radial = Math.hypot(perpM, alongOffset);
      expect(radial).toBeLessThanOrEqual(LIFT_CLEAR_HALF_WIDTH_M + LIFT_CLEAR_JITTER_M + 1);
    }
  });
});

describe('stampCorridorIntoGrid', () => {
  it('clears interior forest to grassland, leaving distant cells forest', () => {
    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const { grid: cleared, changed } = stampCorridorIntoGrid(forest, ringFor());
    expect(changed).toBeGreaterThan(0);
    const data = cleared.data as Uint8Array;

    // A cell on the centreline mid-span is inside the corridor → grassland.
    const midRow = Math.floor(0.5 * N);
    const midCol = Math.floor(0.5 * N);
    expect(data[midRow * N + midCol]).toBe(TERRAIN_COVER_CODES.grassland);

    // The far corners are well outside the strip → still forest.
    expect(data[0]).toBe(TERRAIN_COVER_CODES.forest);
    expect(data[N * N - 1]).toBe(TERRAIN_COVER_CODES.forest);

    // `changed` equals the number of cells that actually flipped.
    let flipped = 0;
    for (let i = 0; i < data.length; i++) if (data[i] !== forest.data[i]) flipped++;
    expect(flipped).toBe(changed);

    // The source grid was not mutated.
    expect((forest.data as Uint8Array)[midRow * N + midCol]).toBe(TERRAIN_COVER_CODES.forest);
  });

  it('leaves water cells untouched', () => {
    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const midRow = Math.floor(0.5 * N);
    const midCol = Math.floor(0.5 * N);
    (forest.data as Uint8Array)[midRow * N + midCol] = TERRAIN_COVER_CODES.water;
    const { grid: cleared } = stampCorridorIntoGrid(forest, ringFor());
    expect((cleared.data as Uint8Array)[midRow * N + midCol]).toBe(TERRAIN_COVER_CODES.water);
  });
});

describe('appendCorridorToDisplayGeometry', () => {
  it('adds one decodable grassland polygon inside the bounds', () => {
    const ring = ringFor();
    const geometry = appendCorridorToDisplayGeometry([], ring, BOUNDS, TERRAIN_COVER_CODES.grassland);
    const fc = coverDisplayToGeoJSON(geometry, BOUNDS);
    expect(fc.features).toHaveLength(1);
    expect(fc.features[0].properties.code).toBe(TERRAIN_COVER_CODES.grassland);
    for (const [lng, lat] of fc.features[0].geometry.coordinates[0]) {
      expect(lng).toBeGreaterThanOrEqual(BOUNDS.west - 1e-6);
      expect(lng).toBeLessThanOrEqual(BOUNDS.east + 1e-6);
      expect(lat).toBeGreaterThanOrEqual(BOUNDS.south - 1e-6);
      expect(lat).toBeLessThanOrEqual(BOUNDS.north + 1e-6);
    }
  });

  it('keeps display metadata consistent with the geometry (the validate checks)', () => {
    const ring = ringFor();
    const geometry = appendCorridorToDisplayGeometry([], ring, BOUNDS, TERRAIN_COVER_CODES.grassland);
    const counts = inspectCoverDisplayGeometry(geometry);
    const metadata = coverDisplayMetadataOf(geometry, { ...counts, smoothingM: 6, simplifyM: 2, minFeatureM2: 16 });
    // These are exactly the equalities validateTerrainPackage re-checks.
    expect(metadata.polygonCount).toBe(counts.polygonCount);
    expect(metadata.ringCount).toBe(counts.ringCount);
    expect(metadata.vertexCount).toBe(counts.vertexCount);
    expect(metadata.checksum).toBe(checksumBytes(float32Bytes(geometry)));
  });
});
