import { describe, expect, it } from 'vitest';
import { decodeSnowGrid, encodeSnowGrid, generateSnowBaseline, hydrateSnowGrid,
  sampleSnowGrid, SNOW_SURFACE_POWDER, snowSlopeRetention } from './snow';
import type { SnowGrid } from './types/snow';
import type { TerrainRecord } from './types/terrain';

function terrain(heightAt: (row: number, col: number, n: number) => number, latitude = 45,
  probabilities = new Array<number>(12).fill(0.5)): TerrainRecord {
  const n = 5;
  const heights = Array.from({ length: n * n }, (_, index) =>
    heightAt(Math.floor(index / n), index % n, n));
  return {
    schemaVersion: 6, key: 'snow-test', mountainName: 'Snow Test', latitude, longitude: 0,
    areaSizeMeters: 1000,
    bounds: { west: -0.00635, east: 0.00635, south: latitude - 0.0045, north: latitude + 0.0045 },
    sampleGridSize: n, sampleHeights: heights,
    climate: { monthly: probabilities.map((snowProbability) => ({ tempHigh: 25, tempLow: 10,
      snowProbability, avgWindSpeed: 10 })) },
    sourceType: 'live', createdAt: '', updatedAt: '',
  };
}

describe('snow baseline', () => {
  it('is deterministic, targets 10 m cells, caps dimensions, and initializes Powder', () => {
    const record = terrain(() => 1000);
    const first = generateSnowBaseline(record), second = generateSnowBaseline(record);
    expect(first.width).toBeGreaterThanOrEqual(95);
    expect(first.width).toBeLessThanOrEqual(105);
    expect(first.height).toBeGreaterThanOrEqual(95);
    expect(first.height).toBeLessThanOrEqual(105);
    expect([...first.depthM]).toEqual([...second.depthM]);
    expect(new Set(first.surface)).toEqual(new Set([SNOW_SURFACE_POWDER]));

    const huge = terrain(() => 1000);
    huge.bounds = { west: -0.2, east: 0.2, south: 44.9, north: 45.1 };
    const capped = generateSnowBaseline(huge);
    expect(Math.max(capped.width, capped.height)).toBe(512);
    expect(capped.width / capped.height).toBeCloseTo(
      (0.4 * Math.cos(45 * Math.PI / 180)) / 0.2, 1);
  });

  it('uses the correct winter months in each hemisphere', () => {
    const northern = new Array<number>(12).fill(0);
    for (const index of [10, 11, 0, 1, 2]) northern[index] = 1;
    const northernDepth = generateSnowBaseline(terrain(() => 1000, 45, northern)).depthM[0];
    const southernDepth = generateSnowBaseline(terrain(() => 1000, -45, northern)).depthM[0];
    expect(northernDepth).toBeGreaterThan(southernDepth * 2);
  });

  it('retains more snow on northern aspects and removes it from extreme slopes', () => {
    const northFacing = generateSnowBaseline(terrain((row) => row * 25));
    const southFacing = generateSnowBaseline(terrain((row, _col, n) => (n - 1 - row) * 25));
    const center = Math.floor(northFacing.height / 2) * northFacing.width +
      Math.floor(northFacing.width / 2);
    expect(northFacing.depthM[center]).toBeGreaterThan(southFacing.depthM[center]);
    expect(snowSlopeRetention(15)).toBe(1);
    expect(snowSlopeRetention(35)).toBeCloseTo(0.65);
    expect(snowSlopeRetention(50)).toBeCloseTo(0.15);
    expect(snowSlopeRetention(60)).toBe(0);
    const cliff = generateSnowBaseline(terrain((row) => row * 1000));
    expect([...cliff.depthM].every((depth) => depth === 0)).toBe(true);
  });

  it('recomputes different snow after committed elevation changes', () => {
    const original = terrain(() => 1000);
    const graded = { ...original, sampleHeights: original.sampleHeights.map((_height, index) =>
      Math.floor(index / original.sampleGridSize) * 1000) };
    const before = generateSnowBaseline(original), after = generateSnowBaseline(graded);
    expect([...before.depthM]).not.toEqual([...after.depthM]);
    expect([...after.depthM].some((depth) => depth === 0)).toBe(true);
  });
});

describe('snow persistence and sampling', () => {
  it('round-trips every condition and the maximum packed depth', () => {
    const grid: SnowGrid = {
      bounds: { west: 0, south: 0, east: 1, north: 1 }, width: 11, height: 2,
      depthM: new Float32Array(22), surface: new Uint8Array(22),
    };
    for (let index = 0; index < 11; index++) {
      grid.depthM[index] = index === 10 ? 40.95 : (index + 1) / 10;
      grid.surface[index] = index + 1;
    }
    const decoded = decodeSnowGrid(encodeSnowGrid(grid));
    expect(decoded).not.toBeNull();
    expect([...decoded!.surface.slice(0, 11)]).toEqual([1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);
    expect(decoded!.depthM[10]).toBeCloseTo(40.95, 2);
  });

  it('rejects malformed snapshots and regenerates mismatched legacy state', () => {
    expect(decodeSnowGrid({ version: 1, width: 2, height: 2,
      bounds: { west: 0, south: 0, east: 1, north: 1 }, cells: 'bad' })).toBeNull();
    const record = terrain(() => 1000);
    const wrong = encodeSnowGrid({ ...generateSnowBaseline(record),
      bounds: { west: 10, south: 10, east: 11, north: 11 } });
    const hydrated = hydrateSnowGrid(wrong, record);
    expect(hydrated.bounds).toEqual(record.bounds);
    expect(hydrated.surface[0]).toBe(SNOW_SURFACE_POWDER);
  });

  it('bilinearly samples depth, uses nearest surface, and rejects outside points', () => {
    const grid: SnowGrid = {
      bounds: { west: 0, south: 0, east: 1, north: 1 }, width: 2, height: 2,
      depthM: new Float32Array([0, 1, 2, 3]), surface: new Uint8Array([1, 2, 3, 4]),
    };
    expect(sampleSnowGrid(grid, 0.5, 0.5)).toEqual({ depthM: 1.5, surface: 4 });
    expect(sampleSnowGrid(grid, 2, 2)).toBeNull();
  });
});
