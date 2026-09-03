import { snowSlopeRetention } from './snow';
import type { SnowGrid } from './types/snow';
import type { TerrainRecord } from './types/terrain';
import type { ResolvedWeatherHour, TerrainThermalModel, TerrainWeatherField } from './weather/weatherModel';
import { createTerrainThermalModel, terrainWeatherFieldForHour } from './weather/terrainThermal';

export const SNOW_MODEL_VERSION = 1 as const;
export const DEFAULT_SNOW_MODEL_CONFIG = {
  version: SNOW_MODEL_VERSION,
  visibleDepthM: 0.02,
  hourlyCompaction: 0.9985,
  positiveDegreeMeltMPerC: 0.00055,
  rainMeltMPerMm: 0.00035,
  radiationMeltMPerWm2: 0.0000012,
  maxDepthM: 4.095,
} as const;

export interface SnowStepResult {
  grid: SnowGrid;
  changedCells: number;
  hoursApplied: number;
}

function terrainHeight(record: TerrainRecord, u: number, v: number): number {
  const size = record.sampleGridSize;
  const x = Math.max(0, Math.min(size - 1, u * (size - 1)));
  const y = Math.max(0, Math.min(size - 1, v * (size - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y);
  const x1 = Math.min(size - 1, x0 + 1), y1 = Math.min(size - 1, y0 + 1);
  const tx = x - x0, ty = y - y0;
  const at = (col: number, row: number) => record.sampleHeights[row * size + col];
  return (at(x0, y0) * (1 - tx) + at(x1, y0) * tx) * (1 - ty) +
    (at(x0, y1) * (1 - tx) + at(x1, y1) * tx) * ty;
}

function slopeRetention(record: TerrainRecord, width: number, height: number, col: number, row: number): number {
  if (!record.bounds) return 1;
  const u = col / Math.max(1, width - 1), v = row / Math.max(1, height - 1);
  const du = 1 / Math.max(1, width - 1), dv = 1 / Math.max(1, height - 1);
  const midLat = (record.bounds.north + record.bounds.south) / 2;
  const cellX = (record.bounds.east - record.bounds.west) * 111_320 * Math.cos(midLat * Math.PI / 180) /
    Math.max(1, width - 1);
  const cellY = (record.bounds.north - record.bounds.south) * 111_320 / Math.max(1, height - 1);
  const dzdx = (terrainHeight(record, Math.min(1, u + du), v) -
    terrainHeight(record, Math.max(0, u - du), v)) / Math.max(0.01, 2 * cellX);
  const dzdy = (terrainHeight(record, u, Math.min(1, v + dv)) -
    terrainHeight(record, u, Math.max(0, v - dv))) / Math.max(0.01, 2 * cellY);
  return snowSlopeRetention(Math.atan(Math.hypot(dzdx, dzdy)) * 180 / Math.PI);
}

function fieldIndex(field: TerrainWeatherField, width: number, height: number, col: number, row: number): number {
  const x = Math.round(col / Math.max(1, width - 1) * (field.width - 1));
  const y = Math.round(row / Math.max(1, height - 1) * (field.height - 1));
  return y * field.width + x;
}

function stepHour(
  depth: Float32Array,
  surface: Uint8Array,
  grid: SnowGrid,
  terrain: TerrainRecord,
  thermal: TerrainThermalModel,
  hour: ResolvedWeatherHour,
): number {
  const config = DEFAULT_SNOW_MODEL_CONFIG;
  const field = terrainWeatherFieldForHour(thermal, hour);
  let changed = 0;
  for (let row = 0; row < grid.height; row += 1) for (let col = 0; col < grid.width; col += 1) {
    const index = row * grid.width + col;
    const weatherIndex = fieldIndex(field, grid.width, grid.height, col, row);
    const beforeDepth = depth[index];
    const beforeSurface = surface[index];
    const temperatureC = field.temperatureC[weatherIndex];
    const snowRatio = field.snowRatio[weatherIndex];
    const windRetention = Math.max(0.55, 1 - Math.max(0, hour.windSpeedKph - 20) / 140);
    const accumulationM = snowRatio > 0
      ? hour.precipitationMm * snowRatio / 1000 * slopeRetention(terrain, grid.width, grid.height, col, row) * windRetention
      : 0;
    const rainMelt = snowRatio === 0 ? hour.precipitationMm * config.rainMeltMPerMm : 0;
    const thermalMelt = Math.max(0, temperatureC) * config.positiveDegreeMeltMPerC;
    const radiationMelt = Math.max(0, hour.globalRadiationWm2) * config.radiationMeltMPerWm2;
    let nextDepth = beforeDepth * config.hourlyCompaction + accumulationM - rainMelt - thermalMelt - radiationMelt;
    nextDepth = Math.max(0, Math.min(config.maxDepthM, nextDepth));
    let nextSurface = beforeSurface;
    if (nextDepth < config.visibleDepthM) {
      nextDepth = 0;
      nextSurface = 0;
    } else if (accumulationM >= 0.005) nextSurface = temperatureC > -0.5 ? 11 : 1;
    else if (temperatureC > 1 || rainMelt > 0) nextSurface = 10;
    else if (temperatureC < -2 && (beforeSurface === 10 || beforeSurface === 11)) nextSurface = 5;
    else if (beforeSurface === 1 && accumulationM === 0) nextSurface = 2;
    depth[index] = nextDepth;
    surface[index] = nextSurface;
    if (Math.abs(nextDepth - beforeDepth) >= 0.00001 || nextSurface !== beforeSurface) changed += 1;
  }
  return changed;
}

/** Pure sequential hourly model. Passing many hours is exactly equivalent to repeated one-hour calls. */
export function stepNaturalSnow(
  grid: SnowGrid,
  terrain: TerrainRecord,
  hours: readonly ResolvedWeatherHour[],
): SnowStepResult {
  const depthM = new Float32Array(grid.depthM);
  const surface = new Uint8Array(grid.surface);
  const next = { ...grid, depthM, surface };
  const thermal = createTerrainThermalModel(terrain);
  let changedCells = 0;
  for (const hour of hours) changedCells += stepHour(depthM, surface, next, terrain, thermal, hour);
  return { grid: next, changedCells, hoursApplied: hours.length };
}
