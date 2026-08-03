import { describe, expect, it } from 'vitest';
import { analyzeStream, formatStreamDischarge, gameplayStreamFlowM3s,
  parseOsmWidthM, sanitizeStreamWidthOverrides, widthFromDisplay, widthToDisplay } from './streamAnalysis';
import type { WaterLineFeature } from './types';

const line = (points: [number, number][] = [[-0.001, 0], [0.001, 0]]): WaterLineFeature => ({
  id: 'way/7', name: 'Cold Creek', waterClass: 'stream', points,
});

describe('stream analysis', () => {
  it('parses common OSM widths and rejects unsafe values', () => {
    expect(parseOsmWidthM('4.5')).toBe(4.5);
    expect(parseOsmWidthM('12 ft')).toBeCloseTo(3.6576, 4);
    expect(parseOsmWidthM(`6' 3"`)).toBeCloseTo(1.905, 3);
    expect(parseOsmWidthM('unknown')).toBeNull();
    expect(parseOsmWidthM('1000 m')).toBeNull();
  });

  it('assigns a non-zero hard-coded flow to every supported width', () => {
    expect([0.25, 1, 1.01, 2, 3, 8, 15, 30, 60, 500].map(gameplayStreamFlowM3s))
      .toEqual([0.03, 0.03, 0.10, 0.10, 0.30, 1, 3, 10, 30, 75]);
  });

  it('uses override, OSM, then class default widths and width-band flow', () => {
    const feature = { ...line(), sourceWidthM: 5 };
    const osm = analyzeStream(feature);
    const custom = analyzeStream(feature, 8);
    const fallback = analyzeStream({ ...feature, sourceWidthM: undefined });
    expect(osm.widthSource).toBe('osm');
    expect(custom.widthSource).toBe('override');
    expect(fallback.widthM).toBe(3);
    expect(osm.dischargeM3s).toBe(1);
    expect(custom.dischargeM3s).toBe(1);
    expect(fallback.dischargeM3s).toBe(0.30);
  });

  it('converts display units, formats discharge, and sanitizes saves', () => {
    expect(widthFromDisplay(widthToDisplay(4, 'imperial'), 'imperial')).toBeCloseTo(4);
    expect(formatStreamDischarge(0.25, 'metric')).toBe('250 L/s');
    expect(formatStreamDischarge(2, 'metric')).toBe('2,000 L/s');
    expect(formatStreamDischarge(1, 'imperial')).toBe('15,850 US gal/min');
    expect(sanitizeStreamWidthOverrides({ good: 4, low: 0.1, high: 501, text: '3' })).toEqual({ good: 4 });
  });
});
