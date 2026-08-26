export type WeatherQuality = 'verified' | 'estimated' | 'limited';
export type WeatherEventType = 'storm' | 'freeze-thaw' | 'cold-snap' | 'warm-up';
export type StormStyle =
  | 'pacific-system' | 'atmospheric-river' | 'nor-easter' | 'clipper'
  | 'lake-effect' | 'upslope' | 'frontal' | 'tropical-remnant' | 'convective';

export interface WeatherReferenceHour {
  at: string;
  temperatureC: number;
  wetBulbC: number;
  humidityPct: number;
  precipitationMm: number;
  precipitationType: 'none' | 'rain' | 'mixed' | 'snow' | 'freezing-rain';
  snowfallCm: number;
  windSpeedKph: number;
  windGustKph: number;
  windDirectionDeg: number;
  cloudCoverPct: number;
  visibilityKm: number;
  pressureHpa: number;
  radiationWm2: number;
}

export interface HistoricalWeatherYear {
  year: number;
  hours: WeatherReferenceHour[];
}

export interface WeatherPackageManifest {
  schemaVersion: 1;
  terrainKey: string;
  terrainBinding: string;
  timezone: string;
  historicalStartYear: 1991;
  historicalEndYear: 2020;
  quality: WeatherQuality;
  sourceSummary: string;
  sourceVersion: string;
  generatorVersion: 1;
  contentHash: string;
  complete: boolean;
  createdAt: string;
}

/** The durable, offline-only weather artifact associated with one terrain map. */
export interface WeatherDataPackage {
  manifest: WeatherPackageManifest;
  /** Compact normalized hourly source records. Raw provider files stay in the service cache. */
  historicalYears: HistoricalWeatherYear[];
}

export function isWeatherDataPackage(value: unknown): value is WeatherDataPackage {
  if (!value || typeof value !== 'object') return false;
  const weatherPackage = value as Partial<WeatherDataPackage>;
  const manifest = weatherPackage.manifest;
  return !!manifest && manifest.schemaVersion === 1 && manifest.complete === true &&
    typeof manifest.terrainKey === 'string' && typeof manifest.terrainBinding === 'string' &&
    typeof manifest.contentHash === 'string' && Array.isArray(weatherPackage.historicalYears) &&
    weatherPackage.historicalYears.every((year) => Number.isInteger(year.year) && Array.isArray(year.hours));
}

export interface WeatherEvent {
  id: string;
  type: WeatherEventType;
  startsAt: string;
  endsAt: string;
  severity: 'minor' | 'notable' | 'major';
  stormStyle?: StormStyle;
}

export interface SyntheticWeatherPlan {
  seed: string;
  startsAt: string;
  endsAt: string;
  hours: WeatherReferenceHour[];
  events: WeatherEvent[];
}

export interface TerrainThermalModel {
  width: number;
  height: number;
  bounds: { west: number; south: number; east: number; north: number };
  referenceElevationM: number;
  elevationDeltaM: Float32Array;
  coldAirDrainage: Float32Array;
}

export interface TemperatureField extends TerrainThermalModel {
  temperatureC: Float32Array;
}

function seeded(seed: string): () => number {
  let state = 2166136261;
  for (let index = 0; index < seed.length; index += 1) {
    state ^= seed.charCodeAt(index);
    state = Math.imul(state, 16777619);
  }
  return () => {
    state += 0x6D2B79F5;
    let value = state;
    value = Math.imul(value ^ value >>> 15, value | 1);
    value ^= value + Math.imul(value ^ value >>> 7, value | 61);
    return ((value ^ value >>> 14) >>> 0) / 4294967296;
  };
}

function cloneHour(hour: WeatherReferenceHour, at: string, anomaly: number): WeatherReferenceHour {
  const temperatureC = Math.round((hour.temperatureC + anomaly) * 10) / 10;
  const wetBulbC = Math.round((hour.wetBulbC + anomaly) * 10) / 10;
  const precipitationType = hour.precipitationMm <= 0.001 ? 'none'
    : wetBulbC <= -1 ? 'snow' : wetBulbC < 1 ? 'mixed' : 'rain';
  return { ...hour, at, temperatureC, wetBulbC, precipitationType,
    snowfallCm: precipitationType === 'snow' ? hour.snowfallCm : precipitationType === 'mixed' ? hour.snowfallCm * 0.5 : 0 };
}

export function detectWeatherEvents(hours: readonly WeatherReferenceHour[]): WeatherEvent[] {
  const events: WeatherEvent[] = [];
  let stormStart = -1;
  let coldStart = -1;
  let warmStart = -1;
  for (let index = 0; index <= hours.length; index += 1) {
    const hour = hours[index];
    const storm = !!hour && hour.precipitationMm >= 0.5;
    const cold = !!hour && hour.temperatureC <= -12;
    const warm = !!hour && hour.temperatureC >= 8;
    const finish = (start: number, active: boolean, type: WeatherEventType, style?: StormStyle) => {
      if (active) return start;
      if (start < 0) return -1;
      const duration = index - start;
      if (duration >= (type === 'storm' ? 3 : 12)) {
        events.push({ id: `${type}-${start}`, type, startsAt: hours[start].at,
          endsAt: hours[index - 1].at, severity: duration >= 24 ? 'major' : 'notable', ...(style ? { stormStyle: style } : {}) });
      }
      return -1;
    };
    if (storm && stormStart < 0) stormStart = index;
    stormStart = finish(stormStart, storm, 'storm', hour?.windSpeedKph && hour.windSpeedKph > 45 ? 'frontal' : 'pacific-system');
    if (cold && coldStart < 0) coldStart = index;
    coldStart = finish(coldStart, cold, 'cold-snap');
    if (warm && warmStart < 0) warmStart = index;
    warmStart = finish(warmStart, warm, 'warm-up');
  }
  for (let index = 1; index < hours.length; index += 1) {
    if (hours[index - 1].temperatureC > 1 && hours[index].temperatureC < -1) {
      events.push({ id: `freeze-thaw-${index}`, type: 'freeze-thaw', startsAt: hours[index - 1].at,
        endsAt: hours[index].at, severity: 'notable' });
    }
  }
  return events.sort((left, right) => left.startsAt.localeCompare(right.startsAt));
}

/**
 * Uses complete historical hourly days as analogs. The package is immutable;
 * only the selected analog days and small temperature perturbations are random.
 */
export function generateSyntheticWeather(
  weatherPackage: WeatherDataPackage,
  startsAt: string,
  seed: string,
  days = 366,
): SyntheticWeatherPlan {
  if (!weatherPackage.manifest.complete || weatherPackage.historicalYears.length === 0) {
    throw new Error('A complete offline weather package is required.');
  }
  const random = seeded(seed);
  const sourceHours = weatherPackage.historicalYears.flatMap((year) => year.hours);
  if (sourceHours.length < 24) throw new Error('Weather package has no hourly history.');
  const start = new Date(startsAt);
  const hours: WeatherReferenceHour[] = [];
  for (let day = 0; day < days; day += 1) {
    const sourceDay = Math.floor(random() * Math.floor(sourceHours.length / 24)) * 24;
    const anomaly = (random() - 0.5) * 2;
    for (let hour = 0; hour < 24; hour += 1) {
      const at = new Date(start.getTime() + (day * 24 + hour) * 3_600_000).toISOString();
      hours.push(cloneHour(sourceHours[sourceDay + hour] ?? sourceHours[hour], at, anomaly));
    }
  }
  return { seed, startsAt: start.toISOString(), endsAt: hours.at(-1)?.at ?? start.toISOString(), hours,
    events: detectWeatherEvents(hours) };
}
