import type { PrecipitationType, TerrainWeatherField } from '../weather/weatherModel';

let activeField: TerrainWeatherField | null = null;
const PHASES: readonly PrecipitationType[] = ['none', 'rain', 'mixed', 'snow', 'freezing-rain'];

export function setActiveTerrainWeather(field: TerrainWeatherField | null): void {
  activeField = field;
}

export function sampleActiveTerrainWeather(lng: number, lat: number):
  { temperatureC: number; precipitationType: PrecipitationType } | null {
  const field = activeField;
  if (!field || lng < field.bounds.west || lng > field.bounds.east ||
    lat < field.bounds.south || lat > field.bounds.north) return null;
  const col = Math.round((lng - field.bounds.west) / (field.bounds.east - field.bounds.west) * (field.width - 1));
  const row = Math.round((field.bounds.north - lat) / (field.bounds.north - field.bounds.south) * (field.height - 1));
  const index = row * field.width + col;
  return { temperatureC: field.temperatureC[index], precipitationType: PHASES[field.precipitationPhase[index]] ?? 'none' };
}
