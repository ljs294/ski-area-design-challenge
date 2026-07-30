import { describe, expect, it } from 'vitest';
import {
  grasslandCodeFor,
  jitterPolygon,
  jitterRing,
  liftCorridorRing,
  stampPolygonsIntoGrid,
  LIFT_CLEAR_HALF_WIDTH_M,
  LIFT_CLEAR_JITTER_M,
  TRAIL_CLEAR_BUBBLE_AMPLITUDE_M,
  TRAIL_CLEAR_BUBBLE_STEP_M,
  TRAIL_CLEAR_BUBBLE_WAVELENGTH_M,
} from './coverEdit';
import { boundsForSquareMeters, lngLatToUnit, unitToLngLat } from './geo';
import { TERRAIN_COVER_CODES } from './fourClassCover';
import { coverDisplayToGeoJSON, deriveCoverDisplayGeometry } from './coverDisplay';
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

const BUBBLE_OPTIONS = {
  amplitudeM: TRAIL_CLEAR_BUBBLE_AMPLITUDE_M,
  wavelengthM: TRAIL_CLEAR_BUBBLE_WAVELENGTH_M,
  maxSegmentM: TRAIL_CLEAR_BUBBLE_STEP_M,
  periodic: true,
  outwardOnly: true,
} as const;

function squareSignedDistanceM(point: [number, number], halfSizeM: number): number {
  const [u, v] = lngLatToUnit(point[0], point[1], BOUNDS);
  const x = Math.abs((u - 0.5) * 240);
  const y = Math.abs((v - 0.5) * 240);
  return Math.max(x, y) - halfSizeM;
}

function circleRing(radiusM: number, points = 72): [number, number][] {
  const ring: [number, number][] = [];
  for (let i = 0; i < points; i++) {
    const angle = i / points * Math.PI * 2;
    ring.push(unitToLngLat(
      0.5 + Math.cos(angle) * radiusM / 240,
      0.5 + Math.sin(angle) * radiusM / 240,
      BOUNDS
    ));
  }
  return [...ring, ring[0]];
}

describe('bubble-style trail clearing edges', () => {
  it('densifies sparse edges and adds deterministic outward lobes within the configured amplitude', () => {
    const source = outerRing();
    const bubbled = jitterRing(source, BUBBLE_OPTIONS, 'trail-bubbles');
    expect(bubbled).toEqual(jitterRing(source, BUBBLE_OPTIONS, 'trail-bubbles'));
    expect(bubbled.length).toBeGreaterThan(source.length);
    expect(bubbled[0]).toEqual(bubbled[bubbled.length - 1]);

    const signedDistances = bubbled.slice(0, -1).map((point) =>
      squareSignedDistanceM(point, 60));
    expect(Math.min(...signedDistances)).toBeGreaterThanOrEqual(-0.05);
    expect(Math.max(...signedDistances)).toBeGreaterThan(6);
    for (const distance of signedDistances) {
      expect(Math.abs(distance)).toBeLessThanOrEqual(TRAIL_CLEAR_BUBBLE_AMPLITUDE_M + 0.05);
    }
    // Long source sides are resampled independently of their sparse vertices.
    expect(bubbled.length).toBeGreaterThan(100);
  });

  it('joins the periodic displacement smoothly across a curved ring seam', () => {
    const source = circleRing(60);
    const bubbled = jitterRing(source, BUBBLE_OPTIONS, 'seam');
    const radialDisplacement = (point: [number, number]) => {
      const [u, v] = lngLatToUnit(point[0], point[1], BOUNDS);
      return Math.hypot((u - 0.5) * 240, (v - 0.5) * 240) - 60;
    };
    const first = radialDisplacement(bubbled[0]);
    const next = radialDisplacement(bubbled[1]);
    const previous = radialDisplacement(bubbled[bubbled.length - 2]);
    expect(Math.abs(next - first)).toBeLessThan(0.5);
    expect(Math.abs(first - previous)).toBeLessThan(0.5);
  });

  it('applies the same broad outward scallops to a lift corridor', () => {
    const base = liftCorridorRing(LIFT, BOUNDS, {
      halfWidthM: LIFT_CLEAR_HALF_WIDTH_M,
      jitterM: 0,
      seed: 'lift-clear',
    });
    const bubbled = jitterPolygon([base], BUBBLE_OPTIONS, 'lift-clear')[0];
    const lateral = bubbled
      .filter(([lng]) => lng > LIFT[0][0] && lng < LIFT[1][0])
      .map(([, lat]) => Math.abs(lat - LIFT[0][1]) * 111_320);
    expect(Math.max(...lateral)).toBeGreaterThan(LIFT_CLEAR_HALF_WIDTH_M + 6);

    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const cleared = stampPolygonsIntoGrid(forest, [[bubbled]]).grid;
    const display = coverDisplayToGeoJSON(
      deriveCoverDisplayGeometry(cleared).geometry,
      BOUNDS
    );
    const grass = display.features.find((feature) =>
      feature.properties.code === TERRAIN_COVER_CODES.grassland);
    expect(grass).toBeDefined();
    const visibleLateral = grass!.geometry.coordinates[0]
      .filter(([lng]) => lng > LIFT[0][0] && lng < LIFT[1][0])
      .map(([, lat]) => Math.abs(lat - LIFT[0][1]) * 111_320);
    expect(Math.max(...visibleLateral) - Math.min(...visibleLateral)).toBeGreaterThanOrEqual(3);
  });

  it('keeps repeated visible bays on both sides of a long lift after vector smoothing', () => {
    const bounds = boundsForSquareMeters(47, -121.5, 1000);
    const size = 500;
    const lift: [[number, number], [number, number]] = [
      unitToLngLat(0.1, 0.5, bounds),
      unitToLngLat(0.9, 0.5, bounds),
    ];
    const base = liftCorridorRing(lift, bounds, {
      halfWidthM: LIFT_CLEAR_HALF_WIDTH_M,
      jitterM: 0,
      seed: 'long-lift',
    });
    const bubbled = jitterPolygon([base], BUBBLE_OPTIONS, 'long-lift');

    const sourceNorth = bubbled[0]
      .map(([lng, lat]) => {
        const [u, v] = lngLatToUnit(lng, lat, bounds);
        return { x: u * 1000, displacement: (0.5 - v) * 1000 - LIFT_CLEAR_HALF_WIDTH_M };
      })
      .filter(({ x, displacement }) => x > 0.11 * 1000 && x < 0.89 * 1000 && displacement > -1)
      .sort((a, b) => a.x - b.x);
    const sourcePeaks = sourceNorth.filter((point, i, points) =>
      i > 0 && i < points.length - 1 &&
      point.displacement > points[i - 1].displacement &&
      point.displacement >= points[i + 1].displacement &&
      point.displacement > 6);
    const sourceSpacings = sourcePeaks.slice(1).map((point, i) =>
      point.x - sourcePeaks[i].x);
    const sourceHeights = sourcePeaks.map((point) => point.displacement);
    expect(sourcePeaks.length).toBeGreaterThanOrEqual(8);
    expect(Math.max(...sourceSpacings) - Math.min(...sourceSpacings)).toBeGreaterThan(15);
    expect(Math.max(...sourceHeights) - Math.min(...sourceHeights)).toBeGreaterThan(2);

    const forest = {
      ...grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest),
      bounds,
      width: size,
      height: size,
      data: new Uint8Array(size * size).fill(TERRAIN_COVER_CODES.forest),
    } as CoverGrid;
    const cleared = stampPolygonsIntoGrid(forest, [bubbled]).grid;
    const display = coverDisplayToGeoJSON(
      deriveCoverDisplayGeometry(cleared).geometry,
      bounds
    );
    const grass = display.features.find((feature) =>
      feature.properties.code === TERRAIN_COVER_CODES.grassland);
    expect(grass).toBeDefined();

    const sides = { north: [] as number[], south: [] as number[] };
    for (const [lng, lat] of grass!.geometry.coordinates[0]) {
      const [u, v] = lngLatToUnit(lng, lat, bounds);
      if (u < 0.13 || u > 0.87) continue; // exclude rounded terminal caps
      const signedLateralM = (v - 0.5) * 1000;
      const displacementM = Math.abs(signedLateralM) - LIFT_CLEAR_HALF_WIDTH_M;
      (signedLateralM < 0 ? sides.north : sides.south).push(displacementM);
    }
    for (const displacements of Object.values(sides)) {
      // Require repeated deep, broad arcs on both sides after smoothing. A
      // single global range check can pass while most of an edge stays flat.
      expect(displacements.filter((value) => value <= 6).length).toBeGreaterThanOrEqual(8);
      expect(displacements.filter((value) => value >= 15).length).toBeGreaterThanOrEqual(8);
    }
  });

  it('scales bubbles down on small rings and keeps holes forested when stamped', () => {
    const small = (() => {
      const half = 2 / 240;
      const corners: [number, number][] = [
        unitToLngLat(0.5 - half, 0.5 - half, BOUNDS),
        unitToLngLat(0.5 + half, 0.5 - half, BOUNDS),
        unitToLngLat(0.5 + half, 0.5 + half, BOUNDS),
        unitToLngLat(0.5 - half, 0.5 + half, BOUNDS),
      ];
      return [...corners, corners[0]];
    })();
    const smallBubbled = jitterRing(small, BUBBLE_OPTIONS, 'small');
    expect(smallBubbled.length).toBeGreaterThanOrEqual(5);
    expect(smallBubbled[0]).toEqual(smallBubbled[smallBubbled.length - 1]);
    for (const point of smallBubbled.slice(0, -1)) {
      expect(squareSignedDistanceM(point, 2)).toBeGreaterThanOrEqual(-0.05);
      expect(squareSignedDistanceM(point, 2)).toBeLessThanOrEqual(16 / 12 + 0.05);
    }

    const bubbledPolygon = jitterPolygon([outerRing(), holeRing()], BUBBLE_OPTIONS, 'with-hole');
    for (const point of bubbledPolygon[1].slice(0, -1)) {
      expect(squareSignedDistanceM(point, 20)).toBeLessThanOrEqual(0.05);
    }
    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const cleared = stampPolygonsIntoGrid(forest, [bubbledPolygon]).grid;
    const data = cleared.data as Uint8Array;
    const mid = Math.floor(0.5 * N);
    expect(data[mid * N + mid]).toBe(TERRAIN_COVER_CODES.forest);
    expect(data[mid * N + Math.floor((0.5 + 40 / 240) * N)])
      .toBe(TERRAIN_COVER_CODES.grassland);
  });

  it('keeps visible scallops after the normal cover-display smoothing pass', () => {
    const forest = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const bubbled = jitterPolygon([outerRing()], BUBBLE_OPTIONS, 'visible-edge');
    const cleared = stampPolygonsIntoGrid(forest, [bubbled]).grid;
    const display = coverDisplayToGeoJSON(
      deriveCoverDisplayGeometry(cleared).geometry,
      BOUNDS
    );
    const grass = display.features.find((feature) =>
      feature.properties.code === TERRAIN_COVER_CODES.grassland);
    expect(grass).toBeDefined();
    const distances = grass!.geometry.coordinates[0]
      .filter((point) => {
        const [u, v] = lngLatToUnit(point[0], point[1], BOUNDS);
        const x = Math.abs((u - 0.5) * 240);
        const y = Math.abs((v - 0.5) * 240);
        return Math.min(x, y) < 45;
      })
      .map((point) => squareSignedDistanceM(point as [number, number], 60));
    expect(Math.max(...distances) - Math.min(...distances)).toBeGreaterThanOrEqual(3);
  });
});

describe('scanline cover stamping', () => {
  it('matches even-odd point containment for a diagonal polygon with a hole', () => {
    const outer: [number, number][] = [
      unitToLngLat(0.1, 0.3, BOUNDS), unitToLngLat(0.7, 0.1, BOUNDS),
      unitToLngLat(0.9, 0.7, BOUNDS), unitToLngLat(0.3, 0.9, BOUNDS),
      unitToLngLat(0.1, 0.3, BOUNDS),
    ];
    const hole: [number, number][] = [
      unitToLngLat(0.42, 0.42, BOUNDS), unitToLngLat(0.58, 0.42, BOUNDS),
      unitToLngLat(0.58, 0.58, BOUNDS), unitToLngLat(0.42, 0.58, BOUNDS),
      unitToLngLat(0.42, 0.42, BOUNDS),
    ];
    const source = grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest);
    const actual = stampPolygonsIntoGrid(source, [[outer, hole]]).grid.data;
    const inside = (lng: number, lat: number) => {
      let hit = false;
      for (const ring of [outer, hole]) {
        for (let i = 0, j = ring.length - 1; i < ring.length; j = i++) {
          const [xi, yi] = ring[i], [xj, yj] = ring[j];
          if ((yi > lat) !== (yj > lat) &&
              lng < ((xj - xi) * (lat - yi)) / (yj - yi) + xi) hit = !hit;
        }
      }
      return hit;
    };
    const onBoundary = (lng: number, lat: number) => {
      const [u, v] = lngLatToUnit(lng, lat, BOUNDS);
      for (const ring of [outer, hole]) {
        for (let i = 1; i < ring.length; i++) {
          const [au, av] = lngLatToUnit(ring[i - 1][0], ring[i - 1][1], BOUNDS);
          const [bu, bv] = lngLatToUnit(ring[i][0], ring[i][1], BOUNDS);
          const dx = bu - au, dy = bv - av;
          const t = Math.max(0, Math.min(1,
            ((u - au) * dx + (v - av) * dy) / Math.max(1e-20, dx * dx + dy * dy)));
          if (Math.hypot(u - (au + dx * t), v - (av + dy * t)) < 1e-10) return true;
        }
      }
      return false;
    };
    for (let row = 0; row < N; row++) for (let col = 0; col < N; col++) {
      const [lng, lat] = unitToLngLat((col + 0.5) / N, (row + 0.5) / N, BOUNDS);
      if (onBoundary(lng, lat)) continue;
      expect(actual[row * N + col], `cell ${row},${col}`).toBe(inside(lng, lat)
        ? TERRAIN_COVER_CODES.grassland
        : TERRAIN_COVER_CODES.forest);
    }
  });

  it('handles a long, densely sampled diagonal corridor without a bounding-box point test', () => {
    const size = 1000;
    const dense: [number, number][] = [];
    for (let i = 0; i <= size; i++) dense.push(unitToLngLat(i / size, i / size * 0.9 + 0.02, BOUNDS));
    for (let i = size; i >= 0; i--) dense.push(unitToLngLat(i / size, i / size * 0.9 + 0.04, BOUNDS));
    dense.push(dense[0]);
    const source = {
      ...grid('usgs-four-class-v1', TERRAIN_COVER_CODES.forest),
      width: size,
      height: size,
      cellSizeM: 0.24,
      data: new Uint8Array(size * size).fill(TERRAIN_COVER_CODES.forest),
    } as CoverGrid;
    expect(stampPolygonsIntoGrid(source, [[dense]]).changed).toBeGreaterThan(10_000);
  });
});
