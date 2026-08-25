export interface ResortWeatherLocation {
  id: string;
  name: string;
  latitude: number;
  longitude: number;
  baseElevationM: number;
  midElevationM: number;
  summitElevationM: number;
}

export const JACKSON_NH_TEST_LOCATION: ResortWeatherLocation = {
  id: 'jackson-nh',
  name: 'Jackson, New Hampshire',
  latitude: 44.1672897,
  longitude: -71.164239,
  baseElevationM: 396,
  midElevationM: 556,
  summitElevationM: 716,
};

export interface RawClimateDay {
  year: number;
  dayOfYear: number;
  minTempC: number;
  maxTempC: number;
  precipitationMm: number;
  vaporPressurePa: number;
  snowWaterEquivalentKgM2: number;
  dayLengthSeconds: number;
}

export interface SeasonalClimateBin {
  index: number;
  dayOfYearStart: number;
  dayOfYearEnd: number;
  meanMinTempC: number;
  meanMaxTempC: number;
  minTempStdDevC: number;
  maxTempStdDevC: number;
  wetDayProbability: number;
  wetToWetProbability: number;
  dryToWetProbability: number;
  meanWetDayPrecipitationMm: number;
  precipitationShape: number;
  precipitationScale: number;
  precipitationP90Mm: number;
  precipitationP98Mm: number;
  temperaturePersistence: number;
  meanRelativeHumidityPct: number;
  meanDayLengthSeconds: number;
}

export interface ResortClimateBaseline {
  schemaVersion: 1;
  source: 'daymet' | 'nasa-power' | 'procedural';
  sourcePeriod: { startYear: 2010; endYear: 2019 };
  sourceElevationM: number;
  location: ResortWeatherLocation;
  bins: SeasonalClimateBin[];
  attribution: string;
  fetchedAt: string;
}

function mean(values: readonly number[]): number {
  return values.length ? values.reduce((sum, value) => sum + value, 0) / values.length : 0;
}

function variance(values: readonly number[], average = mean(values)): number {
  if (values.length < 2) return 1;
  return values.reduce((sum, value) => sum + (value - average) ** 2, 0) / (values.length - 1);
}

function percentile(values: readonly number[], p: number): number {
  if (values.length === 0) return 0;
  const sorted = [...values].sort((a, b) => a - b);
  const position = (sorted.length - 1) * p;
  const lower = Math.floor(position);
  const upper = Math.ceil(position);
  const fraction = position - lower;
  return sorted[lower] * (1 - fraction) + sorted[upper] * fraction;
}

function saturationVaporPressurePa(tempC: number): number {
  return 610.94 * Math.exp((17.625 * tempC) / (tempC + 243.04));
}

function temperaturePersistence(days: readonly RawClimateDay[]): number {
  if (days.length < 3) return 0.5;
  const values = days.map((day) => (day.minTempC + day.maxTempC) / 2);
  const average = mean(values);
  let numerator = 0;
  let denominator = 0;
  for (let i = 1; i < values.length; i += 1) {
    numerator += (values[i - 1] - average) * (values[i] - average);
    denominator += (values[i - 1] - average) ** 2;
  }
  return Math.max(0.1, Math.min(0.9, denominator > 0 ? numerator / denominator : 0.5));
}

export function deriveClimateBaseline(
  location: ResortWeatherLocation,
  rawDays: readonly RawClimateDay[],
  source: ResortClimateBaseline['source'],
  sourceElevationM: number,
  fetchedAt = new Date().toISOString(),
): ResortClimateBaseline {
  const filtered = rawDays
    .filter((day) => day.year >= 2010 && day.year <= 2019)
    .sort((a, b) => a.year - b.year || a.dayOfYear - b.dayOfYear);
  if (filtered.length < 3_600) {
    throw new Error(`Expected ten years of climate data; received ${filtered.length} days`);
  }

  const bins: SeasonalClimateBin[] = [];
  for (let index = 0; index < 52; index += 1) {
    const start = index * 7 + 1;
    const end = index === 51 ? 366 : start + 6;
    const days = filtered.filter((day) => day.dayOfYear >= start && day.dayOfYear <= end);
    const minTemps = days.map((day) => day.minTempC);
    const maxTemps = days.map((day) => day.maxTempC);
    const wetFlags = days.map((day) => day.precipitationMm >= 0.1);
    const wetAmounts = days.filter((day) => day.precipitationMm >= 0.1).map((day) => day.precipitationMm);
    let wetAfterWet = 0;
    let wetPrevious = 0;
    let wetAfterDry = 0;
    let dryPrevious = 0;
    for (let i = 1; i < days.length; i += 1) {
      if (days[i].year !== days[i - 1].year || days[i].dayOfYear !== days[i - 1].dayOfYear + 1) continue;
      if (wetFlags[i - 1]) {
        wetPrevious += 1;
        if (wetFlags[i]) wetAfterWet += 1;
      } else {
        dryPrevious += 1;
        if (wetFlags[i]) wetAfterDry += 1;
      }
    }
    const precipMean = mean(wetAmounts);
    const precipVariance = variance(wetAmounts, precipMean);
    const shape = precipVariance > 0 ? Math.max(0.25, precipMean ** 2 / precipVariance) : 1;
    const averageTemps = days.map((day) => (day.minTempC + day.maxTempC) / 2);
    const humidities = days.map((day, i) =>
      Math.max(5, Math.min(100, (day.vaporPressurePa / saturationVaporPressurePa(averageTemps[i])) * 100)),
    );
    bins.push({
      index,
      dayOfYearStart: start,
      dayOfYearEnd: end,
      meanMinTempC: mean(minTemps),
      meanMaxTempC: mean(maxTemps),
      minTempStdDevC: Math.sqrt(variance(minTemps)),
      maxTempStdDevC: Math.sqrt(variance(maxTemps)),
      wetDayProbability: wetFlags.filter(Boolean).length / wetFlags.length,
      wetToWetProbability: wetPrevious ? wetAfterWet / wetPrevious : 0.35,
      dryToWetProbability: dryPrevious ? wetAfterDry / dryPrevious : 0.25,
      meanWetDayPrecipitationMm: precipMean,
      precipitationShape: shape,
      precipitationScale: precipMean > 0 ? precipMean / shape : 1,
      precipitationP90Mm: percentile(wetAmounts, 0.9),
      precipitationP98Mm: percentile(wetAmounts, 0.98),
      temperaturePersistence: temperaturePersistence(days),
      meanRelativeHumidityPct: mean(humidities),
      meanDayLengthSeconds: mean(days.map((day) => day.dayLengthSeconds)),
    });
  }

  return {
    schemaVersion: 1,
    source,
    sourcePeriod: { startYear: 2010, endYear: 2019 },
    sourceElevationM,
    location,
    bins,
    attribution: source === 'daymet'
      ? 'NASA ORNL DAAC Daymet V4 R1, 2010–2019'
      : source === 'nasa-power'
        ? 'NASA POWER daily meteorology, 2010–2019'
        : 'Procedural climate estimate',
    fetchedAt,
  };
}

export function binForDate(baseline: ResortClimateBaseline, date: Date): SeasonalClimateBin {
  const start = Date.UTC(date.getUTCFullYear(), 0, 0);
  const day = Math.floor((date.getTime() - start) / 86_400_000);
  return baseline.bins[Math.min(51, Math.floor((Math.max(1, day) - 1) / 7))];
}
