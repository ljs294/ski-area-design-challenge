import { describe, expect, it } from 'vitest';
import { boundsForSquareMeters, haversineMeters, unitToLngLat } from './geo';
import { stampPolygonsIntoGrid } from './coverEdit';
import { TERRAIN_COVER_CODES } from './fourClassCover';
import type { CoverGrid } from './types';
import {
  nextRoadName,
  roadClearingPolygons,
  roadLengthM,
  sanitizeRoads,
  TWO_LANE_CLEAR_HALF_WIDTH_M,
  TWO_LANE_ROAD_WIDTH_M,
} from './roads';
import type { SavedRoad } from './types';

const A: [number, number] = [-121.5, 46.93];
const B: [number, number] = [-121.499, 46.93];

describe('roads', () => {
  it('calculates multi-segment length', () => {
    const c: [number, number] = [B[0], B[1] + 0.001];
    expect(roadLengthM([A, B, c])).toBeCloseTo(haversineMeters(A, B) + haversineMeters(B, c), 8);
  });

  it('sanitizes roads and derives fixed width and length', () => {
    const raw = { id: 'r1', name: 'Access Road', roadType: 'two-lane', widthM: 999,
      points: [A, A, B], lengthM: 1, createdAt: '2026-01-01T00:00:00.000Z' };
    const road = sanitizeRoads([raw])[0];
    expect(road.widthM).toBe(TWO_LANE_ROAD_WIDTH_M);
    expect(road.points).toEqual([A, B]);
    expect(road.lengthM).toBeCloseTo(haversineMeters(A, B), 8);
  });

  it('drops malformed and unsupported roads', () => {
    expect(sanitizeRoads([null, { id: 'x', name: 'x', roadType: 'four-lane', points: [A, B] },
      { id: 'x', name: 'x', roadType: 'two-lane', points: [A] }])).toEqual([]);
  });

  it('builds a closed 13 m-wide clearing capsule per segment', () => {
    const polygon = roadClearingPolygons([A, B])[0];
    const ring = polygon[0];
    expect(ring[0]).toEqual(ring.at(-1));
    const minLat = Math.min(...ring.map((point) => point[1]));
    const maxLat = Math.max(...ring.map((point) => point[1]));
    expect(haversineMeters([A[0], minLat], [A[0], maxLat]))
      .toBeCloseTo(TWO_LANE_CLEAR_HALF_WIDTH_M * 2, 1);
    expect(roadClearingPolygons([A, B, [B[0], B[1] + 0.001]])).toHaveLength(2);
  });

  it('stamps a 13 m corridor into cover while preserving water', () => {
    const bounds = boundsForSquareMeters(46.93, -121.5, 100);
    const size = 100;
    const data = new Uint8Array(size * size).fill(TERRAIN_COVER_CODES.forest);
    data[50 * size + 50] = TERRAIN_COVER_CODES.water;
    const grid = { bounds, width: size, height: size, cellSizeM: 1, data,
      complete: true, nodataCount: 0, source: 'usgs-four-class-v1', vintage: '2026' } as unknown as CoverGrid;
    const line: [number, number][] = [unitToLngLat(0.3, 0.5, bounds), unitToLngLat(0.7, 0.5, bounds)];
    const cleared = stampPolygonsIntoGrid(grid, roadClearingPolygons(line)).grid;
    const centerColumn = Array.from({ length: size }, (_, row) => cleared.data[row * size + 50]);
    const clearedRows = centerColumn.filter((code) => code === TERRAIN_COVER_CODES.grassland).length;
    expect(clearedRows).toBeGreaterThanOrEqual(12);
    expect(clearedRows).toBeLessThanOrEqual(14);
    expect(cleared.data[50 * size + 50]).toBe(TERRAIN_COVER_CODES.water);
  });

  it('chooses the first unused default name', () => {
    const road = (name: string) => ({ name }) as SavedRoad;
    expect(nextRoadName([road('Road 1'), road('Road 3')])).toBe('Road 2');
  });
});
