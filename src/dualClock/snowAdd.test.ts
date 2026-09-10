import { describe, expect, it } from 'vitest';
import { addFreshSnow, MAX_SNOW_DEPTH_M } from './snowAdd';

function grid(depth: readonly number[], surface = new Uint8Array(depth.length)) {
  return { bounds: { west: 0, south: 0, east: 1, north: 1 }, width: 2, height: 2,
    depthM: Float32Array.from(depth), surface };
}

describe('development snow blanket', () => {
  it('adds fresh snow, clears exposure only where snow was deposited, and reports cap clipping', () => {
    const snow = grid([0, MAX_SNOW_DEPTH_M - 0.05, MAX_SNOW_DEPTH_M, MAX_SNOW_DEPTH_M + 0.2], Uint8Array.from([0, 5, 7, 9]));
    const applied = addFreshSnow(snow, 0.1);
    expect(applied.result).toEqual({ requestedMeters: 0.1, affectedCells: 2, clippedCells: 3 });
    expect([...applied.grid.depthM]).toHaveLength(4);
    expect(applied.grid.depthM[0]).toBeCloseTo(0.1, 6);
    expect(applied.grid.depthM[1]).toBeCloseTo(MAX_SNOW_DEPTH_M, 6);
    expect(applied.grid.depthM[2]).toBeCloseTo(MAX_SNOW_DEPTH_M, 6);
    expect(applied.grid.depthM[3]).toBeCloseTo(snow.depthM[3]!, 6);
    expect([...applied.grid.surface]).toEqual([1, 1, 1, 1]);
    expect(snow.depthM[0]).toBe(0);
    expect(snow.depthM[1]).toBeCloseTo(MAX_SNOW_DEPTH_M - 0.05, 6);
  });

  it('rejects amounts that cannot represent a positive blanket', () => {
    const snow = grid([0, 0, 0, 0]);
    for (const value of [0, -0.1, Infinity, NaN]) expect(() => addFreshSnow(snow, value)).toThrow(/positive finite/);
  });
});
