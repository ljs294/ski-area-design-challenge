import { describe, expect, it } from 'vitest';
import {
  grasslandCodeFor,
  jitterPolygon,
  jitterRing,
  liftCorridorRing,
  stampPolygonsIntoGrid,
  LIFT_CLEAR_HALF_WIDTH_M,
  LIFT_CLEAR_JITTER_M,
} from './coverEdit';
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

// A square hole (tree island) centred on the box, ±20 m in unit space, closed.
function holeRing(): [number, number][] {
  const half = 20 / 240; // 20 m in unit coords on the 240 m box
  const corners: [number, number][] = [
    unitToLngLat(0.5 - half, 0.5 - half, BOUNDS),
    unitToLngLat(0.5 + half, 0.5 - half, BOUNDS),
    unitToLngLat(0.5 + half, 0.5 + half, BOUNDS),
    unitToLngLat(0.5 - half, 0.5 + half, BOUNDS),
  ];
  return [...corners, corners[0]];
}

// A large outer ring covering the middle of the box (±60 m), closed.
function outerRing(): [number, number][] {
  const half = 60 / 240;
  const corners: [number, number][] = [
    unitToLngLat(0.5 - half, 0.5 - half, BOUNDS),
    unitToLngLat(0.5 + half, 0.5 - half, BOUNDS),
    unitToLngLat(0.5 + half, 0.5 + half, BOUNDS),
    unitToLngLat(0.5 - half, 0.5 + half, BOUNDS),
  ];
  return [...corners, corners[0]];
}

describe('stampPolygonsIntoGrid', () => {
  it('clears interior forest to grassland, leaving distant cells forest', () => {
    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const { grid: cleared, changed } = stampPolygonsIntoGrid(forest, [[ringFor()]]);
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
    // Stamp the big outer square (no hole) so the mid cell is inside the footprint.
    const { grid: cleared } = stampPolygonsIntoGrid(forest, [[outerRing()]]);
    expect((cleared.data as Uint8Array)[midRow * N + midCol]).toBe(TERRAIN_COVER_CODES.water);
  });

  it('leaves tree islands (holes) forested while clearing the footprint around them', () => {
    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const { grid: cleared, changed } = stampPolygonsIntoGrid(forest, [[outerRing(), holeRing()]]);
    expect(changed).toBeGreaterThan(0);
    const data = cleared.data as Uint8Array;

    // Dead centre is inside the hole → stays forest.
    const midRow = Math.floor(0.5 * N);
    const midCol = Math.floor(0.5 * N);
    expect(data[midRow * N + midCol]).toBe(TERRAIN_COVER_CODES.forest);

    // A cell 40 m east of centre sits between the hole edge (20 m) and the outer
    // edge (60 m) → grassland.
    const bandRow = Math.floor(0.5 * N);
    const bandCol = Math.floor((0.5 + 40 / 240) * N);
    expect(data[bandRow * N + bandCol]).toBe(TERRAIN_COVER_CODES.grassland);
  });
});

describe('jitterRing / jitterPolygon', () => {
  it('keeps the ring closed and stays within the amplitude of the input', () => {
    const ring = outerRing();
    const jittered = jitterRing(ring, LIFT_CLEAR_JITTER_M, 'trail-1:r0');
    expect(jittered[0]).toEqual(jittered[jittered.length - 1]); // still closed
    expect(jittered.length).toBe(ring.length);

    // Every vertex moved by at most the amplitude (+ a hair for float error).
    const mPerLat = 111320;
    const mPerLng = 111320 * Math.cos((ring[0][1] * Math.PI) / 180);
    for (let i = 0; i < ring.length; i++) {
      const dLng = (jittered[i][0] - ring[i][0]) * mPerLng;
      const dLat = (jittered[i][1] - ring[i][1]) * mPerLat;
      expect(Math.hypot(dLng, dLat)).toBeLessThanOrEqual(LIFT_CLEAR_JITTER_M + 0.5);
    }
  });

  it('is deterministic for a fixed seed and differs across seeds', () => {
    expect(jitterRing(outerRing(), LIFT_CLEAR_JITTER_M, 'a')).toEqual(jitterRing(outerRing(), LIFT_CLEAR_JITTER_M, 'a'));
    expect(jitterRing(outerRing(), LIFT_CLEAR_JITTER_M, 'a')).not.toEqual(jitterRing(outerRing(), LIFT_CLEAR_JITTER_M, 'b'));
  });

  it('jitters every ring of a polygon, keeping the hole', () => {
    const jittered = jitterPolygon([outerRing(), holeRing()], LIFT_CLEAR_JITTER_M, 'trail-1:0');
    expect(jittered).toHaveLength(2);
    expect(jittered[0][0]).toEqual(jittered[0][jittered[0].length - 1]);
    expect(jittered[1][0]).toEqual(jittered[1][jittered[1].length - 1]);
  });
});
