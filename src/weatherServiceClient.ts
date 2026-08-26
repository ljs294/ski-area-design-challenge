import type { TerrainRecord } from './types/terrain';
import type { WeatherDataPackage } from './weather/weatherModel';
import { validateWeatherPackage } from './weatherStorageClient';
import { weatherTerrainBinding } from './weather/terrainBinding';

const DEFAULT_SERVICE_URL = 'http://127.0.0.1:8787';

function serviceUrl(): string {
  return import.meta.env.VITE_WEATHER_SERVICE_URL || DEFAULT_SERVICE_URL;
}

/** This is the only online path. It is called explicitly while preparing a map. */
export async function downloadWeatherPackage(record: TerrainRecord, signal?: AbortSignal): Promise<WeatherDataPackage> {
  const response = await fetch(`${serviceUrl()}/v1/weather-packages`, {
    method: 'POST', signal, headers: { 'content-type': 'application/json' },
    body: JSON.stringify({
      terrainKey: record.key,
      latitude: record.latitude,
      longitude: record.longitude,
      bounds: record.bounds,
      areaSizeMeters: record.areaSizeMeters,
      terrainBinding: weatherTerrainBinding(record),
    }),
  });
  if (!response.ok) throw new Error(`Weather service returned ${response.status}.`);
  const weatherPackage: unknown = await response.json();
  if (!validateWeatherPackage(weatherPackage)) throw new Error('Weather service returned an invalid or incomplete package.');
  if (weatherPackage.manifest.terrainBinding !== weatherTerrainBinding(record)) {
    throw new Error('Weather service returned a package for a different terrain map.');
  }
  return weatherPackage;
}
