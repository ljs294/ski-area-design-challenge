import { describe, expect, it } from 'vitest';
import { analyzeDam, damFillTimeSeconds, damStreamCrossings, effectiveWaterwayWidthM,
  gameplayStreamFlowM3s, sanitizeDams, snapDamEndpoint } from './damAnalysis';
import type { TerrainRecord, WaterLineFeature } from './types';
import { parseWaterwayWidthM } from './vectorFeatures';

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
    expect(parseWaterwayWidthM('12 ft 6 in')).toBeCloseTo(3.81, 4);
    expect(parseWaterwayWidthM('4.5 m')).toBe(4.5);
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
  it('rejects a basin that reaches the terrain boundary', () => {
    const outcome = analyzeDam(terrain(new Array<number>(21 * 21).fill(8)),
      [point(5, 10), point(15, 10)], 10, [stream]);
    expect(outcome).toEqual({ ok: false, error: 'The pond is not contained within the available terrain.' });
  });
  it('drops malformed saved dams', () => expect(sanitizeDams([{ id: 'bad' }])).toEqual([]));
});
