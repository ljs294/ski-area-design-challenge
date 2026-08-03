import { haversineMeters } from './geo';
import type { WaterLineFeature } from './types';
import type { WaterLineClass, WaterLineFeature } from './types';

const EARTH_RADIUS_M = 6_371_000;
const M_TO_FT = 3.280839895;
const M3S_TO_US_GPM = 15_850.3231;
const MIN_WIDTH_M = 0.25;
const MAX_WIDTH_M = 500;

export type StreamWidthSource = 'override' | 'osm' | 'default';

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
  waterClass: WaterLineClass;
  lengthM: number;
  sourceWidthM: number | null;
  defaultWidthM: number;
  widthM: number;
  widthSource: StreamWidthSource;
  /** Guaranteed gameplay flow determined solely by the effective channel width. */
  dischargeM3s: number;
}

type Position = [number, number];

function distanceM(a: Position, b: Position): number {
  const radians = Math.PI / 180;
  const lat1 = a[1] * radians, lat2 = b[1] * radians;
  const dLat = lat2 - lat1, dLng = (b[0] - a[0]) * radians;
  const h = Math.sin(dLat / 2) ** 2 + Math.cos(lat1) * Math.cos(lat2) * Math.sin(dLng / 2) ** 2;
  return 2 * EARTH_RADIUS_M * Math.asin(Math.min(1, Math.sqrt(h)));
}

export function streamLengthM(feature: WaterLineFeature): number {
  let length = 0;
  for (let i = 1; i < feature.points.length; i++) length += distanceM(feature.points[i - 1], feature.points[i]);
  return length;
}

/** Parse common OSM width values. Bare numbers are metres, per OSM convention. */
export function parseOsmWidthM(raw: string | undefined): number | null {
  if (!raw) return null;
  const value = raw.trim().toLowerCase().replace(',', '.');
  let metres: number | null = null;
  const feetInches = value.match(/^(\d+(?:\.\d+)?)\s*(?:ft|feet|foot|')\s*(?:(\d+(?:\.\d+)?)\s*(?:in|inches|inch|"))?$/);
  if (feetInches) metres = (Number(feetInches[1]) + Number(feetInches[2] ?? 0) / 12) / M_TO_FT;
  const simple = value.match(/^(\d+(?:\.\d+)?)\s*(m|metres?|meters?|ft|feet|foot)?$/);
  if (metres == null && simple) {
    const amount = Number(simple[1]);
    metres = simple[2] && !simple[2].startsWith('m') ? amount / M_TO_FT : amount;
  }
  return metres != null && Number.isFinite(metres) && metres >= MIN_WIDTH_M && metres <= MAX_WIDTH_M
    ? metres : null;
}

export function defaultStreamWidthM(waterClass: WaterLineClass): number {
  return waterClass === 'stream' ? 3 : 15;
}

export function effectiveStreamWidth(feature: WaterLineFeature, overrideWidthM?: number): {
  widthM: number; source: StreamWidthSource;
} {
  if (Number.isFinite(overrideWidthM) && overrideWidthM! >= MIN_WIDTH_M && overrideWidthM! <= MAX_WIDTH_M) {
    return { widthM: overrideWidthM!, source: 'override' };
  }
  if (Number.isFinite(feature.sourceWidthM) && feature.sourceWidthM! >= MIN_WIDTH_M && feature.sourceWidthM! <= MAX_WIDTH_M) {
    return { widthM: feature.sourceWidthM!, source: 'osm' };
  }
  return { widthM: defaultStreamWidthM(feature.waterClass), source: 'default' };
}

/**
 * Stable flow values for snowmaking gameplay. These are intentionally simple
 * width bands, not hydrologic or seasonal predictions.
 */
export function gameplayStreamFlowM3s(widthM: number): number {
  if (widthM <= 1) return 0.03;
  if (widthM <= 2) return 0.10;
  if (widthM <= 4) return 0.30;
  if (widthM <= 8) return 1.00;
  if (widthM <= 15) return 3.00;
  if (widthM <= 30) return 10.00;
  if (widthM <= 60) return 30.00;
  return 75.00;
}

export function analyzeStream(feature: WaterLineFeature, overrideWidthM?: number): StreamAnalysis {
  const effective = effectiveStreamWidth(feature, overrideWidthM);
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

    lengthM: streamLengthM(feature),
    sourceWidthM: feature.sourceWidthM ?? null,
    defaultWidthM: defaultStreamWidthM(feature.waterClass),
    widthM: effective.widthM,
    widthSource: effective.source,
    dischargeM3s: gameplayStreamFlowM3s(effective.widthM),
  };
}

export function widthToDisplay(widthM: number, units: 'imperial' | 'metric'): number {
  return units === 'imperial' ? widthM * M_TO_FT : widthM;
}

export function widthFromDisplay(width: number, units: 'imperial' | 'metric'): number {
  return units === 'imperial' ? width / M_TO_FT : width;
}

export function formatStreamDistance(metres: number, units: 'imperial' | 'metric'): string {
  if (units === 'imperial') {
    const feet = metres * M_TO_FT;
    return feet >= 5280 ? `${(feet / 5280).toFixed(1)} mi` : `${feet.toFixed(0)} ft`;
  }
  return metres >= 1000 ? `${(metres / 1000).toFixed(1)} km` : `${metres.toFixed(0)} m`;
}

export function formatStreamDischarge(m3s: number, units: 'imperial' | 'metric'): string {
  if (units === 'imperial') return `${Math.round(m3s * M3S_TO_US_GPM).toLocaleString('en-US')} US gal/min`;
  return `${Math.round(m3s * 1000).toLocaleString('en-US')} L/s`;
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
    if (id && typeof value === 'number' && Number.isFinite(value) && value >= MIN_WIDTH_M && value <= MAX_WIDTH_M) {
      result[id] = value;
    }
  }
  return result;
}
