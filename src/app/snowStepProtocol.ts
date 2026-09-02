import type { SnowGrid } from '../types/snow';
import type { TerrainRecord } from '../types/terrain';
import type { ResolvedWeatherHour } from '../weather/weatherModel';

export interface SnowStepRequest {
  id: number;
  terrainBinding: string;
  terrain: TerrainRecord;
  grid: SnowGrid;
  hours: readonly ResolvedWeatherHour[];
}

export type SnowStepResponse =
  | { id: number; terrainBinding: string; ok: true; grid: SnowGrid; changedCells: number; hoursApplied: number }
  | { id: number; terrainBinding: string; ok: false; error: string };
