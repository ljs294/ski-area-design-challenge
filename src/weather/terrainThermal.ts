import type { TerrainRecord } from '../types/terrain';
import type { TemperatureField, TerrainThermalModel, WeatherReferenceHour } from './weatherModel';

const MAX_DIMENSION = 512;

function sampleHeight(record: TerrainRecord, u: number, v: number): number {
  const n = record.sampleGridSize;
  const x = Math.max(0, Math.min(n - 1, u * (n - 1)));
  const y = Math.max(0, Math.min(n - 1, v * (n - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const a = record.sampleHeights[y0 * n + x0], b = record.sampleHeights[y0 * n + x1];
  const c = record.sampleHeights[y1 * n + x0], d = record.sampleHeights[y1 * n + x1];
  return (a * (1 - tx) + b * tx) * (1 - ty) + (c * (1 - tx) + d * tx) * ty;
}

export function createTerrainThermalModel(record: TerrainRecord): TerrainThermalModel {
  if (!record.bounds || record.sampleGridSize < 2) throw new Error('A valid terrain elevation grid is required.');
  const width = Math.min(MAX_DIMENSION, record.sampleGridSize);
  const height = width;
  const count = width * height;
  const elevationDeltaM = new Float32Array(count);
  const coldAirDrainage = new Float32Array(count);
  const referenceElevationM = sampleHeight(record, 0.5, 0.5);
  for (let row = 0; row < height; row += 1) for (let col = 0; col < width; col += 1) {
    const u = col / Math.max(1, width - 1), v = row / Math.max(1, height - 1);
    const elevation = sampleHeight(record, u, v);
    const index = row * width + col;
    elevationDeltaM[index] = elevation - referenceElevationM;
    // Lower terrain is a stable cold-air collection proxy. It is static and
    // only becomes active while the current atmosphere is inverted.
    coldAirDrainage[index] = Math.max(0, referenceElevationM - elevation) / 500;
  }
  return { width, height, bounds: { ...record.bounds }, referenceElevationM, elevationDeltaM, coldAirDrainage };
}

export function temperatureFieldForHour(
  model: TerrainThermalModel,
  hour: WeatherReferenceHour,
  options: { lapseRateCPerKm?: number; inversionStrengthC?: number } = {},
): TemperatureField {
  const lapseRate = options.lapseRateCPerKm ?? -6.5;
  const inversion = options.inversionStrengthC ?? (hour.cloudCoverPct < 35 && hour.windSpeedKph < 12 ? 1.5 : 0);
  const temperatureC = new Float32Array(model.elevationDeltaM.length);
  for (let index = 0; index < temperatureC.length; index += 1) {
    temperatureC[index] = hour.temperatureC + model.elevationDeltaM[index] / 1000 * lapseRate - model.coldAirDrainage[index] * inversion;
  }
  return { ...model, temperatureC };
}

export function sampleTemperatureField(field: TemperatureField, lng: number, lat: number): number | null {
  const b = field.bounds;
  if (lng < b.west || lng > b.east || lat < b.south || lat > b.north) return null;
  const u = (lng - b.west) / (b.east - b.west);
  const v = (b.north - lat) / (b.north - b.south);
  const x = Math.max(0, Math.min(field.width - 1, u * (field.width - 1)));
  const y = Math.max(0, Math.min(field.height - 1, v * (field.height - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y), x1 = Math.min(field.width - 1, x0 + 1), y1 = Math.min(field.height - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const at = (col: number, row: number) => field.temperatureC[row * field.width + col];
  return (at(x0, y0) * (1 - tx) + at(x1, y0) * tx) * (1 - ty) +
    (at(x0, y1) * (1 - tx) + at(x1, y1) * tx) * ty;
}
