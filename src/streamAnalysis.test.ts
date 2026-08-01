import { describe, expect, it } from 'vitest';
import { analyzeStream, gameplayWaterwayFlowM3s, sanitizeStreamWidthOverrides,
  streamWidthFromDisplay, streamWidthToDisplay } from './streamAnalysis';

describe('stream analysis', () => {
  it('guarantees a fixed gameplay flow for every width band', () => {
    expect([1, 2, 4, 8, 15, 30, 60, 61].map(gameplayWaterwayFlowM3s))
      .toEqual([0.03, 0.1, 0.3, 1, 3, 10, 30, 75]);
  });

  it('uses override, OSM width, then class default', () => {
    const base = { id: 'way/1', waterClass: 'stream' as const,
      points: [[-121, 47], [-121, 47.001]] as [number, number][] };
    expect(analyzeStream(base).widthM).toBe(3);
    expect(analyzeStream({ ...base, widthM: 6 }).widthSource).toBe('osm');
    expect(analyzeStream({ ...base, widthM: 6 }, 9)).toMatchObject({ widthM: 9, widthSource: 'override', flowM3s: 3 });
  });

  it('round-trips imperial edits and sanitizes persisted overrides', () => {
    expect(streamWidthFromDisplay(streamWidthToDisplay(12, 'imperial'), 'imperial')).toBeCloseTo(12);
    expect(sanitizeStreamWidthOverrides({ good: 4, zero: 0, text: '3' })).toEqual({ good: 4 });
  });
});
