import type { SnowmakingSegmentAnalysisResult } from '../snowmakingHydraulics';

export interface SnowmakingPressureRange {
  minPsi: number;
  maxPsi: number;
}

const PRESSURE_STOPS = [
  { at: 0, rgb: [244, 114, 74] },
  { at: 0.35, rgb: [245, 185, 66] },
  { at: 0.65, rgb: [45, 190, 173] },
  { at: 1, rgb: [50, 103, 214] },
] as const;

export function snowmakingPressureRange(
  segments: readonly SnowmakingSegmentAnalysisResult[],
): SnowmakingPressureRange | null {
  const values = segments.flatMap((segment) =>
    [segment.fromPressurePsi, segment.toPressurePsi].filter(Number.isFinite));
  if (!values.length) return null;
  let minPsi = Math.min(...values), maxPsi = Math.max(...values);
  if (maxPsi - minPsi < 10) {
    const center = (minPsi + maxPsi) / 2;
    minPsi = center - 5;
    maxPsi = center + 5;
  }
  return { minPsi, maxPsi };
}

export function snowmakingPressureColor(pressurePsi: number,
  range: SnowmakingPressureRange): string {
  const normalized = Math.max(0, Math.min(1,
    (pressurePsi - range.minPsi) / (range.maxPsi - range.minPsi)));
  const upperIndex = PRESSURE_STOPS.findIndex((stop) => stop.at >= normalized);
  const upper = PRESSURE_STOPS[Math.max(0, upperIndex)];
  const lower = PRESSURE_STOPS[Math.max(0, upperIndex - 1)];
  const span = upper.at - lower.at;
  const mix = span === 0 ? 0 : (normalized - lower.at) / span;
  const rgb = lower.rgb.map((channel, index) =>
    Math.round(channel + (upper.rgb[index] - channel) * mix));
  return `rgb(${rgb[0]} ${rgb[1]} ${rgb[2]})`;
}
