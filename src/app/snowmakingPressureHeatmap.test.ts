import { describe, expect, it } from 'vitest';
import type { SnowmakingSegmentAnalysisResult } from '../snowmakingHydraulics';
import { snowmakingPressureColor, snowmakingPressureRange } from './snowmakingPressureHeatmap';

const segment = (fromPressurePsi: number, toPressurePsi: number) => ({
  fromPressurePsi, toPressurePsi,
}) as SnowmakingSegmentAnalysisResult;

describe('snowmaking pressure heat map', () => {
  it('derives the operating pressure extent from both ends of every segment', () => {
    expect(snowmakingPressureRange([segment(42, 75), segment(110, 91)]))
      .toEqual({ minPsi: 42, maxPsi: 110 });
  });

  it('expands a narrow range so a nearly uniform system remains legible', () => {
    expect(snowmakingPressureRange([segment(100, 102)]))
      .toEqual({ minPsi: 96, maxPsi: 106 });
  });

  it('clamps colors at the low and high ends of the scale', () => {
    const range = { minPsi: 0, maxPsi: 100 };
    expect(snowmakingPressureColor(-20, range)).toBe('rgb(244 114 74)');
    expect(snowmakingPressureColor(120, range)).toBe('rgb(50 103 214)');
  });
});
