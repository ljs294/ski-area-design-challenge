import type { HistoricalWeatherSeriesV1, LocationClimateModelV1, MonthlyClimateModelV1, ObservedWeatherHourV1, WeatherCondition, WeatherLabRunRequestV1 } from '../contracts.ts';
import { HISTORICAL_DIFFICULTY } from '../contracts.ts';
import { hashWithout, sha256Hex } from '../engine/canonical.ts';
import { weatherCalendarYear } from '../engine/calendar.ts';
import { clamp, precipitationPhase, relativeHumidityPct, wetBulbTemperatureC } from '../engine/psychrometrics.ts';

export const JACKSON_LOCATION = Object.freeze({ id: 'jackson-nh', name: 'Jackson, New Hampshire', latitude: 44.1672897,
  longitude: -71.164239, comparisonElevationM: 427, bands: { baseElevationM: 396, midElevationM: 556, summitElevationM: 716 } });
export const JACKSON_STATION = Object.freeze({ id: 'KMWN', sourceIds: ['726130-14755', 'KMWN'], name: 'Mount Washington Regional Composite',
  latitude: 44.266, longitude: -71.303, elevationM: 427, timezone: 'America/New_York', distanceKm: 15.8, score: 0.91 });

const CONDITIONS: readonly WeatherCondition[] = ['clear', 'partly-cloudy', 'overcast', 'flurries', 'snow', 'heavy-snow', 'mixed', 'freezing-rain', 'rain'];
function season(month: number): number { return -6 + 17 * Math.cos((month - 7) / 12 * Math.PI * 2); }

export function createJacksonClimateModel(): LocationClimateModelV1 {
  const months: MonthlyClimateModelV1[] = Array.from({ length: 12 }, (_, monthIndex) => {
    const month = monthIndex + 1;
    const monthlyTemperature = season(month);
    const normals = Array.from({ length: 24 }, (_, hour) => {
      const daylight = Math.max(0, Math.sin((hour - 6) / 12 * Math.PI));
      const temperatureC = monthlyTemperature + 4 * Math.cos((hour - 15) / 24 * Math.PI * 2);
      return { temperatureC, temperatureStdDevC: 5.5, dewPointC: temperatureC - 4.5, dewPointStdDevC: 4,
        pressureHpa: 1010, relativeHumidityPct: 72, windSpeedKph: 14, windDirectionDeg: 285,
        cloudCoverPct: 58, visibilityKm: 18, clearSkyRadiationWm2: daylight * (month >= 5 && month <= 8 ? 760 : 520) };
    });
    const macroStates = [
      ['arctic', 72, -9, -9, 9, 1.1, 0.5], ['continental-polar', 72, -2, -4, 4, 0.9, 0.5],
      ['maritime-polar', 60, -1, 1, -2, 1, 1.2], ['warm-wet', 48, 6, 6, -5, 1.15, 1.6],
      ['frontal', 12, 0, 1, -8, 1.5, 1.4],
    ].map(([id, duration, temperature, dew, pressure, wind, precipitation]) => ({ id, meanDurationHours: duration,
      durationStdDevHours: Number(duration) / 3, temperatureAnomalyC: temperature, dewPointAnomalyC: dew,
      pressureAnomalyHpa: pressure, windSpeedMultiplier: wind, precipitationMultiplier: precipitation })) as MonthlyClimateModelV1['macro']['states'];
    const dry = month >= 5 && month <= 9;
    const baseRow = CONDITIONS.map((condition) => condition === 'clear' ? (dry ? 0.22 : 0.12) : condition === 'partly-cloudy' ? 0.3 :
      condition === 'overcast' ? 0.28 : condition === 'rain' ? (dry ? 0.1 : 0.03) : condition === 'snow' ? (dry ? 0.01 : 0.1) : 0.02);
    const sum = baseRow.reduce((a, b) => a + b, 0); const row = baseRow.map((value) => value / sum);
    const matrices = Object.fromEntries(['arctic', 'continental-polar', 'maritime-polar', 'warm-wet', 'frontal'].map((macro) =>
      [macro, CONDITIONS.map((_, index) => row.map((value, target) => target === index ? value + 0.35 : value * 0.65))]));
    return { month: month as MonthlyClimateModelV1['month'], hourlyNormals: normals,
      macro: { states: macroStates, transitionMatrix: [[.45,.25,.12,.03,.15],[.16,.48,.15,.05,.16],[.08,.17,.48,.1,.17],[.02,.08,.18,.52,.2],[.12,.18,.27,.18,.25]] },
      local: { states: CONDITIONS, hourlyMatricesByMacro: matrices as unknown as MonthlyClimateModelV1['local']['hourlyMatricesByMacro'], backoffRows: row },
      emissions: { temperatureAr1: .82, dewPointAr1: .86, temperatureDewPointCorrelation: .72,
        precipitationShape: .85, precipitationScaleMm: 1.7, snowfallRatio: 11, windShape: 2, windScaleKph: 7, gustFactor: 1.45 },
      sampleCounts: { hours: 262800, wetHours: 31200 } };
  });
  const base = { version: 1 as const, generatorVersion: 2 as const, location: JACKSON_LOCATION, primaryStation: JACKSON_STATION,
    trainingStations: [JACKSON_STATION], trainingPeriod: { years: Array.from({ length: 30 }, (_, index) => 1989 + index), policy: { kind: 'prior-30' as const } },
    excludedValidationYear: 2019, months, sourceHash: sha256Hex('jackson-nh-normalized-observations-v1'), climateModelHash: '',
    provenance: { compilerVersion: 'weather-compiler-v1-fixture', providers: ['committed-development-fixture'],
      sourceHashes: [sha256Hex('jackson-nh-2010-2019-committed-fixture')], warnings: ['Development fixture; live provider compilation is not represented'] } } satisfies LocationClimateModelV1;
  return { ...base, climateModelHash: hashWithout(base, ['climateModelHash']) };
}

export function createJacksonObserved2019(): HistoricalWeatherSeriesV1 {
  const calendar = weatherCalendarYear(2019, JACKSON_STATION.timezone);
  const hours: ObservedWeatherHourV1[] = calendar.map((entry, index) => {
    const temperatureC = season(entry.month) + 4 * Math.cos((entry.hour - 15) / 24 * Math.PI * 2) + 2.4 * Math.sin(index * 0.071);
    const dewPointC = Math.min(temperatureC, temperatureC - 3 - 2 * Math.sin(index * 0.037));
    const event = (index * 17 + entry.month * 13) % 197 < (entry.month <= 3 || entry.month >= 11 ? 13 : 8);
    const precipitationMm = event ? 0.25 + ((index * 29) % 170) / 100 : 0;
    const relativeHumidity = relativeHumidityPct(temperatureC, dewPointC); const wetBulbC = wetBulbTemperatureC(temperatureC, relativeHumidity, 1010);
    const phase = precipitationPhase(temperatureC, wetBulbC, precipitationMm);
    const condition: WeatherCondition = precipitationMm ? phase === 'snow' ? (precipitationMm >= 1.5 ? 'heavy-snow' : 'snow') : phase === 'rain' ? 'rain' : phase === 'mixed' ? 'mixed' : 'freezing-rain'
      : index % 5 === 0 ? 'clear' : index % 3 === 0 ? 'partly-cloudy' : 'overcast';
    const cloud = precipitationMm ? 95 : condition === 'clear' ? 12 : condition === 'partly-cloudy' ? 48 : 82;
    return { at: entry.at, localDateTime: entry.localDateTime, utcOffsetMinutes: entry.utcOffsetMinutes, fold: entry.fold,
      temperatureC, dewPointC, pressureHpa: 1010 + 8 * Math.sin(index * .011), relativeHumidityPct: relativeHumidity, wetBulbC,
      precipitationMm, precipitationPhase: phase, snowfallCm: phase === 'snow' ? precipitationMm * 1.1 : 0,
      windSpeedKph: 10 + 8 * Math.abs(Math.sin(index * .043)), windDirectionDeg: (270 + index * 3) % 360,
      windGustKph: 18 + 10 * Math.abs(Math.sin(index * .043)), shortwaveRadiationWm2: Math.max(0, 550 * Math.sin((entry.hour - 6) / 12 * Math.PI)) * (1 - cloud / 130),
      cloudCoverPct: cloud, visibilityKm: clamp(20 / (1 + precipitationMm), .3, 30), condition, hazards: [], quality: {} };
  });
  const base = { version: 1 as const, station: JACKSON_STATION, validationYear: 2019, startInclusive: hours[0].at,
    endExclusive: '2020-01-01T05:00:00.000Z', hours, completeness: Object.fromEntries(['temperatureC','dewPointC','pressureHpa','relativeHumidityPct','wetBulbC','precipitationMm','snowfallCm','windSpeedKph','windDirectionDeg','windGustKph','shortwaveRadiationWm2','cloudCoverPct','visibilityKm','condition'].map((key) => [key, 1])) as HistoricalWeatherSeriesV1['completeness'],
    observationHash: '', provenance: { providers: ['committed-development-fixture'], sourceIds: JACKSON_STATION.sourceIds,
      warnings: ['Deterministic development fixture; do not interpret as provider-observed 2019 weather'] } };
  return { ...base, observationHash: hashWithout(base, ['observationHash']) };
}

export function createJacksonRun(seed = 'Historical'): WeatherLabRunRequestV1 {
  const model = createJacksonClimateModel();
  return { version: 1, location: JACKSON_LOCATION, stationId: JACKSON_STATION.id, stationTimeZone: JACKSON_STATION.timezone,
    validationYear: 2019, trainingPolicy: { kind: 'prior-30' }, worldSeed: seed, difficultyProfile: HISTORICAL_DIFFICULTY,
    generatorVersion: 2, climateModelHash: model.climateModelHash };
}
