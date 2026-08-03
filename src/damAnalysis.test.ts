import { describe, expect, it } from 'vitest';
import { analyzeDam, damFillTimeSeconds, damStreamCrossings, effectiveWaterwayWidthM,
  gameplayStreamFlowM3s, sanitizeDams, snapDamEndpoint } from './damAnalysis';
import type { TerrainRecord, WaterLineFeature } from './types';
import { parseOsmWidthM } from './streamAnalysis';

const bounds = { west: 0, east: 0.002, south: 0, north: 0.002 };
function point(x: number, y: number, n = 21): [number, number] {
  return [x / (n - 1) * 0.002, 0.002 - y / (n - 1) * 0.002];
}
function terrain(heights: number[], n = 21): TerrainRecord {
  return { bounds, sampleGridSize: n, sampleHeights: heights } as TerrainRecord;
}
const stream: WaterLineFeature = { id: 'way/1', name: 'Creek', waterClass: 'stream',
  points: [point(10, 0), point(10, 20)] };

describe('dam gameplay flow', () => {
  it('assigns every positive width to a fixed nonzero band', () => {
    expect([0.5, 1.5, 3, 7, 15, 25, 50, 80].map(gameplayStreamFlowM3s))
      .toEqual([0.03, 0.1, 0.3, 1, 3, 10, 30, 75]);
    expect(effectiveWaterwayWidthM(stream)).toBe(3);
    expect(effectiveWaterwayWidthM({ ...stream, waterClass: 'river' })).toBe(15);
    expect(damFillTimeSeconds(300, 0.3)).toBe(1000);
    expect(parseOsmWidthM('12 ft 6 in')).toBeCloseTo(3.81, 4);
    expect(parseOsmWidthM('4.5 m')).toBe(4.5);
  });
});

describe('dam terrain analysis', () => {
  it('finds the one crossed stream and rejects multiple distinct waterways', () => {
    const dam: [[number, number], [number, number]] = [point(5, 10), point(15, 10)];
    expect(damStreamCrossings(dam, [stream])).toHaveLength(1);
    expect(damStreamCrossings(dam, [stream, { ...stream, id: 'way/2' }])).toHaveLength(2);
  });
  it('snaps the second endpoint to the first endpoint elevation', () => {
    const n = 21;
    const heights = Array.from({ length: n * n }, (_, index) => index % n);
    const first = point(5.3, 4, n);
    const snapped = snapDamEndpoint(terrain(heights, n), first, point(5.7, 16, n), 30);
    expect(snapped).not.toBeNull();
    expect(snapped![0]).toBeCloseTo(first[0], 8);
  });
  it('delineates a contained upstream pond and integrates capacity', () => {
    const n = 21;
    const heights = new Array<number>(n * n).fill(20);
    for (let y = 2; y <= 8; y++) for (let x = 6; x <= 14; x++) heights[y * n + x] = 8;
    for (let y = 12; y <= 18; y++) heights[y * n + 10] = 7;
    heights[10 * n + 5] = 10; heights[10 * n + 15] = 10;
    const outcome = analyzeDam(terrain(heights), [point(5, 10), point(15, 10)], 10, [stream]);
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.result.areaM2).toBeGreaterThan(100);
    expect(outcome.result.capacityM3).toBeGreaterThan(outcome.result.areaM2);
    expect(outcome.result.averageDamHeightM).toBeGreaterThanOrEqual(0);
    expect(outcome.result.averageDamHeightM).toBeLessThanOrEqual(outcome.result.maxDamHeightM);
    expect(outcome.result.pondRings[0].length).toBeGreaterThan(3);
    expect(outcome.result.inflowM3s).toBe(0.3);
  });
  it('uses terrain rather than OSM line order to find upstream', () => {
    const n = 21;
    const heights = new Array<number>(n * n).fill(20);
    for (let y = 2; y <= 8; y++) for (let x = 6; x <= 14; x++) heights[y * n + x] = 8;
    for (let y = 12; y <= 18; y++) heights[y * n + 10] = 7;
    heights[10 * n + 5] = 10; heights[10 * n + 15] = 10;
    const reversed = { ...stream, points: [...stream.points].reverse() };
    const outcome = analyzeDam(terrain(heights), [point(5, 10), point(15, 10)], 10, [reversed]);
    expect(outcome.ok).toBe(true);
  });
  it('does not let the raster flood detour around an equal-contour endpoint', () => {
    const n = 21;
    const heights = new Array<number>(n * n).fill(20);
    for (let y = 2; y <= 8; y++) for (let x = 6; x <= 14; x++) heights[y * n + x] = 8;
    // A low route passes around the left clicked endpoint and joins the
    // downstream channel. It is on the far side of the same cross-valley plane
    // and must not turn a contained upstream pool into a terrain-edge escape.
    for (let x = 2; x <= 6; x++) heights[8 * n + x] = 8;
    for (let y = 8; y <= 12; y++) heights[y * n + 2] = 8;
    for (let x = 2; x <= 10; x++) heights[12 * n + x] = 7;
    for (let y = 12; y <= 20; y++) heights[y * n + 10] = 7;
    heights[10 * n + 5] = 10; heights[10 * n + 15] = 10;
    const outcome = analyzeDam(terrain(heights), [point(5, 10), point(15, 10)], 10, [stream]);
    expect(outcome.ok).toBe(true);
  });
  it('grades a bermed embankment and bills it as borrow fill', () => {
    const n = 21;
    // A valley falling south with walls either side of the stream at column 10.
    const heights = Array.from({ length: n * n }, (_, index) =>
      100 + (20 - Math.floor(index / n)) * 2 + Math.abs((index % n) - 10) * 3);
    const record = terrain(heights);
    const outcome = analyzeDam(record, [point(5, 10), point(15, 10)], 135, [stream]);
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    const result = outcome.result;
    expect(result.damCrestElevationM).toBeCloseTo(135.6, 6);
    expect(result.maxDamHeightM).toBeCloseTo(15.6, 1);
    expect(result.averageDamHeightM).toBeLessThanOrEqual(result.maxDamHeightM);
    expect(result.builtLengthM).toBeGreaterThan(0);
    expect(result.crestRing.length).toBeGreaterThan(4);
    expect(result.disturbedAreaM2).toBeGreaterThan(0);
    // An embankment is built up, so the whole bill is imported material.
    expect(result.earthwork.fillM3).toBeGreaterThan(0);
    expect(result.earthwork.cutM3).toBe(0);
    expect(result.earthwork.balanceM3).toBeLessThan(0);
    expect(result.patchIndices.length).toBe(result.patchHeights.length);
    expect(result.patchIndices.length).toBeGreaterThan(0);
    for (let i = 0; i < result.patchIndices.length; i++)
      expect(result.patchHeights[i]).toBeGreaterThan(heights[result.patchIndices[i]]);
  });

  it('keeps the shoreline off the embankment it just built', () => {
    const n = 21;
    const heights = Array.from({ length: n * n }, (_, index) =>
      100 + (20 - Math.floor(index / n)) * 2 + Math.abs((index % n) - 10) * 3);
    const outcome = analyzeDam(terrain(heights), [point(5, 10), point(15, 10)], 135, [stream]);
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    // Every cell the pond covers is still below full pool on the graded
    // surface, so no water is drawn over the upstream face.
    const graded = Float32Array.from(heights);
    for (let i = 0; i < outcome.result.patchIndices.length; i++)
      graded[outcome.result.patchIndices[i]] = outcome.result.patchHeights[i];
    expect(outcome.result.averageDepthM).toBeGreaterThan(0);
    expect(outcome.result.capacityM3).toBeGreaterThan(0);
    const ring = outcome.result.pondRings[0];
    for (const [lng, lat] of ring) {
      const x = Math.round(lng / 0.002 * (n - 1));
      const y = Math.round((0.002 - lat) / 0.002 * (n - 1));
      expect(graded[y * n + x]).toBeLessThanOrEqual(136.5);
    }
  });

  it('refuses an embankment past earth-fill height', () => {
    const n = 21;
    const heights = Array.from({ length: n * n }, (_, index) =>
      100 + (20 - Math.floor(index / n)) * 2 + Math.abs((index % n) - 10) * 3);
    // A gorge at the dam site: the same pool now needs a 45 m structure.
    for (let x = 9; x <= 11; x++) heights[10 * n + x] = 90;
    const outcome = analyzeDam(terrain(heights), [point(5, 10), point(15, 10)], 135, [stream]);
    expect(outcome.ok).toBe(false);
    if (outcome.ok) return;
    expect(outcome.error).toMatch(/Keep it under 40 m/);
  });

  it('keeps a pond on a lidar-fine grid, where the fill buries the deepest natural cell', () => {
    // The shipped grid is always 2000 samples across, so a real site is 1-2 m
    // per cell — fine enough that the upstream face covers the thalweg cell the
    // natural basin is deepest at. Seeding the graded flood there refused every
    // dam as "not contained"; the seed has to survive the embankment.
    const n = 81;
    const fine = { west: 0, east: 0.0012, south: 0, north: 0.0012 };
    const at = (x: number, y: number): [number, number] =>
      [x / (n - 1) * 0.0012, 0.0012 - y / (n - 1) * 0.0012];
    // A valley falling south at 2.4% with 21% walls either side of column 40.
    const heights = Array.from({ length: n * n }, (_, index) =>
      100 + (n - 1 - Math.floor(index / n)) * 0.04 + Math.abs((index % n) - 40) * 0.35);
    const record = { bounds: fine, sampleGridSize: n, sampleHeights: heights } as TerrainRecord;
    const creek: WaterLineFeature = { ...stream, points: [at(40, 0), at(40, 80)] };
    const outcome = analyzeDam(record, [at(30, 40), at(50, 40)], 102.6, [creek]);
    expect(outcome.ok).toBe(true);
    if (!outcome.ok) return;
    expect(outcome.result.areaM2).toBeGreaterThan(100);
    expect(outcome.result.capacityM3).toBeGreaterThan(1);
    expect(outcome.result.maxDamHeightM).toBeGreaterThan(0);
    // The seed the old code used is under the upstream face; the pond is real
    // anyway, and none of it sits on the embankment.
    const graded = Float32Array.from(heights);
    for (let i = 0; i < outcome.result.patchIndices.length; i++)
      graded[outcome.result.patchIndices[i]] = outcome.result.patchHeights[i];
    expect(graded[38 * n + 40]).toBeGreaterThan(102.6);
  });

  it('rejects a basin that reaches the terrain boundary', () => {
    const outcome = analyzeDam(terrain(new Array<number>(21 * 21).fill(8)),
      [point(5, 10), point(15, 10)], 10, [stream]);
    expect(outcome.ok).toBe(false);
    if (outcome.ok) return;
    expect(outcome.error).toMatch(/backs water past the edge of the available terrain/);
  });
  it('drops malformed saved dams', () => expect(sanitizeDams([{ id: 'bad' }])).toEqual([]));
});
