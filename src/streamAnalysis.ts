import { haversineMeters } from './geo';
import type { WaterLineFeature } from './types';

export interface StreamAnalysis {
  id: string;
  name: string;
  waterClass: WaterLineFeature['waterClass'];
  sourceWidthM: number | null;
  widthM: number;
  widthSource: 'override' | 'osm' | 'default';
  lengthM: number;
  flowM3s: number;
}

export function defaultWaterwayWidthM(waterClass: WaterLineFeature['waterClass']): number {
  return waterClass === 'stream' ? 3 : 15;
}

export function gameplayWaterwayFlowM3s(widthM: number): number {
  if (widthM <= 1) return 0.03;
  if (widthM <= 2) return 0.10;
  if (widthM <= 4) return 0.30;
  if (widthM <= 8) return 1;
  if (widthM <= 15) return 3;
  if (widthM <= 30) return 10;
  if (widthM <= 60) return 30;
  return 75;
}

export function analyzeStream(feature: WaterLineFeature, overrideM?: number): StreamAnalysis {
  const validOverride = typeof overrideM === 'number' && Number.isFinite(overrideM) && overrideM > 0;
  const sourceWidthM = typeof feature.widthM === 'number' && Number.isFinite(feature.widthM) && feature.widthM > 0
    ? feature.widthM : null;
  const widthM = validOverride ? overrideM : sourceWidthM ?? defaultWaterwayWidthM(feature.waterClass);
  let lengthM = 0;
  for (let i = 1; i < feature.points.length; i++) lengthM += haversineMeters(feature.points[i - 1], feature.points[i]);
  return {
    id: feature.id,
    name: feature.name?.trim() || `Unnamed ${feature.waterClass}`,
    waterClass: feature.waterClass,
    sourceWidthM,
    widthM,
    widthSource: validOverride ? 'override' : sourceWidthM == null ? 'default' : 'osm',
    lengthM,
    flowM3s: gameplayWaterwayFlowM3s(widthM),
  };
}

export function sanitizeStreamWidthOverrides(raw: unknown): Record<string, number> {
  if (!raw || typeof raw !== 'object' || Array.isArray(raw)) return {};
  const result: Record<string, number> = {};
  for (const [id, value] of Object.entries(raw)) {
    if (id && typeof value === 'number' && Number.isFinite(value) && value > 0 && value <= 1000) result[id] = value;
  }
  return result;
}

export function streamWidthToDisplay(widthM: number, units: 'imperial' | 'metric'): number {
  return units === 'imperial' ? widthM * 3.280839895 : widthM;
}

export function streamWidthFromDisplay(width: number, units: 'imperial' | 'metric'): number {
  return units === 'imperial' ? width / 3.280839895 : width;
}
