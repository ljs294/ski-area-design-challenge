import { WEATHER_PACKAGE_SCHEMA_VERSION, packageRequestFingerprint, sha256, stableJson } from './contract.mjs';
import { encodeWeatherHours } from './codec.mjs';
import { WeatherServiceError, invariant } from './errors.mjs';
import { weatherMath } from './providers.mjs';

const { saturationVaporPressureHpa, relativeHumidityForVaporPressure, wetBulbC, precipitationType, solarGeometry } = weatherMath;

function clamp(value, minimum, maximum) {
  return Math.max(minimum, Math.min(maximum, value));
}

function average(values) {
  return values.length === 0 ? 0 : values.reduce((sum, value) => sum + value, 0) / values.length;
}

function previousUtcDate(date) {
  const current = new Date(`${date}T00:00:00.000Z`);
  return new Date(current.getTime() - 86_400_000).toISOString().slice(0, 10);
}

function windDirectionDeg(uWindMps, vWindMps) {
  return (Math.atan2(-uWindMps, -vWindMps) * 180 / Math.PI + 360) % 360;
}

function phaseAndSnowfall(precipitationMm, wetBulb) {
  if (precipitationMm <= 0.001) return { precipitationType: 'none', snowfallCm: 0 };
  const phase = precipitationType(wetBulb);
  const snowFraction = phase === 'snow' ? 1 : phase === 'mixed' ? 0.5 : 0;
  const snowRatio = clamp(8 + (-wetBulb) * 0.8, 8, 18);
  return { precipitationType: phase, snowfallCm: precipitationMm * snowFraction * snowRatio / 10 };
}

function ensureSortedContinuousHours(hours) {
  const sorted = [...hours].sort((left, right) => left.at.localeCompare(right.at));
  invariant(sorted.length > 0, 'PROVIDER_RESPONSE_INVALID', 'MERRA-2 returned no hourly records.');
  for (let index = 0; index < sorted.length; index += 1) {
    const timestamp = new Date(sorted[index].at).getTime();
    invariant(Number.isFinite(timestamp) && timestamp % 3_600_000 === 0, 'PROVIDER_RESPONSE_INVALID',
      'MERRA-2 contains an invalid or non-hourly timestamp.', { details: { actual: sorted[index].at } });
    if (index > 0) {
      const previous = new Date(sorted[index - 1].at).getTime();
      invariant(timestamp - previous === 3_600_000, 'PROVIDER_RESPONSE_INVALID',
        'MERRA-2 has a missing, duplicate, or non-hourly timestamp.', {
          details: { previous: sorted[index - 1].at, actual: sorted[index].at },
        });
    }
  }
  return sorted;
}

function localDateFormatter(timezone) {
  return new Intl.DateTimeFormat('en-US', {
    timeZone: timezone,
    year: 'numeric', month: '2-digit', day: '2-digit',
  });
}

function localDate(formatter, at) {
  const values = Object.fromEntries(formatter.formatToParts(new Date(at))
    .filter((part) => part.type !== 'literal').map((part) => [part.type, part.value]));
  return `${values.year}-${values.month}-${values.day}`;
}

/**
 * Daymet daily values are local calendar-day constraints.  MERRA-2 is UTC,
 * so a normalized package must include the UTC boundary hours from adjacent
 * MERRA years and group them in the map's explicit IANA timezone.  This is
 * what keeps a late December 31st in the western US from disappearing.
 */
function localYearDayGroups(hours, year, timezone) {
  const formatter = localDateFormatter(timezone);
  const prefix = `${year}-`;
  const groups = new Map();
  for (const hour of hours) {
    const date = localDate(formatter, hour.at);
    if (!date.startsWith(prefix)) continue;
    const group = groups.get(date) ?? [];
    group.push(hour);
    groups.set(date, group);
  }
  return [...groups.entries()].sort(([left], [right]) => left.localeCompare(right));
}

function anchorForDate(anchors, date) {
  const direct = anchors.get(date);
  if (direct) return { anchor: direct, adjustedCalendar: false };
  // Daymet's documented 365-day leap-year convention can omit Dec 31. Carry
  // its final available daily constraint forward rather than making up a new
  // daily climate value, and record this in provenance.
  const previous = anchors.get(previousUtcDate(date));
  if (previous) return { anchor: previous, adjustedCalendar: true };
  throw new WeatherServiceError('PROVIDER_RESPONSE_INVALID', `Daymet has no daily constraint for ${date}.`);
}

function normalizedTemperature(dayHours, anchor) {
  const rawTemperatures = dayHours.map((hour) => hour.temperatureC);
  const rawMin = Math.min(...rawTemperatures);
  const rawMax = Math.max(...rawTemperatures);
  const targetRange = anchor.tmaxC - anchor.tminC;
  const rawRange = rawMax - rawMin;
  if (rawRange < 0.05) {
    return dayHours.map(() => (anchor.tminC + anchor.tmaxC) / 2);
  }
  return rawTemperatures.map((temperature) => anchor.tminC + (temperature - rawMin) / rawRange * targetRange);
}

function normalizedHumidity(dayHours, temperatures, targetVaporPressurePa) {
  const rawVaporHpa = dayHours.map((hour, index) => saturationVaporPressureHpa(temperatures[index]) * hour.relativeHumidityPct / 100);
  const desiredHpa = targetVaporPressurePa / 100;
  const factor = desiredHpa / Math.max(0.01, average(rawVaporHpa));
  return rawVaporHpa.map((vaporHpa, index) => relativeHumidityForVaporPressure(temperatures[index], vaporHpa * factor * 100));
}

function normalizedPrecipitation(dayHours, targetPrecipitationMm) {
  const rawTotal = dayHours.reduce((sum, hour) => sum + Math.max(0, hour.precipitationMm), 0);
  if (targetPrecipitationMm <= 0.0001) return { values: dayHours.map(() => 0), constrainedTiming: false };
  if (rawTotal > 0.0001) {
    const scale = targetPrecipitationMm / rawTotal;
    return { values: dayHours.map((hour) => Math.max(0, hour.precipitationMm) * scale), constrainedTiming: false };
  }
  // A real Daymet wet day can fall between coarse MERRA precipitation grid
  // events. Allocate only across the observed cloudiest hourly windows, and
  // identify this field as a Daymet-constrained inference in provenance.
  const ranked = dayHours.map((hour, index) => ({ index, cloud: hour.cloudCoverPct })).sort((left, right) => right.cloud - left.cloud).slice(0, 3);
  const weights = ranked.map((entry) => 1 + entry.cloud / 100);
  const totalWeight = weights.reduce((sum, value) => sum + value, 0);
  const values = dayHours.map(() => 0);
  ranked.forEach((entry, index) => { values[entry.index] = targetPrecipitationMm * weights[index] / totalWeight; });
  return { values, constrainedTiming: true };
}

function normalizedRadiation(dayHours, anchor, latitude, longitude) {
  // Daymet srad is average daytime shortwave; converting to hourly energy is
  // srad * daylight seconds. MERRA retains the timing/sky shape.
  const targetHourlyWm2Sum = Math.max(0, anchor.shortwaveWm2 * anchor.daylightSeconds / 3_600);
  const rawTotal = dayHours.reduce((sum, hour) => sum + Math.max(0, hour.shortwaveWm2), 0);
  let global;
  let constrainedTiming = false;
  if (targetHourlyWm2Sum <= 0.001) {
    global = dayHours.map(() => 0);
  } else if (rawTotal > 0.001) {
    const scale = targetHourlyWm2Sum / rawTotal;
    global = dayHours.map((hour) => Math.max(0, hour.shortwaveWm2) * scale);
  } else {
    constrainedTiming = true;
    const clear = dayHours.map((hour) => solarGeometry(latitude, longitude, new Date(hour.at)).clearSkyWm2);
    const clearTotal = clear.reduce((sum, value) => sum + value, 0);
    global = clearTotal > 0 ? clear.map((value) => targetHourlyWm2Sum * value / clearTotal) : dayHours.map(() => 0);
  }
  return { global, constrainedTiming };
}

function fieldProvenance(providerSet, flags) {
  const fixture = providerSet.mode === 'fixture';
  const quality = fixture ? 'limited' : 'estimated';
  const daymet = { provider: fixture ? 'legacy' : 'daymet', quality, sourceVersion: providerSet.daymet.version, correction: 'daymet-constrained' };
  const merra2 = { provider: fixture ? 'legacy' : 'merra-2', quality, sourceVersion: providerSet.merra2.version, correction: 'none' };
  const derived = { provider: 'derived', quality, sourceVersion: 'weather-builder-v2', correction: 'derived' };
  return {
    airTemperatureC: daymet, wetBulbC: derived, relativeHumidityPct: daymet, surfacePressureHpa: merra2,
    windUms: merra2, windVms: merra2, windGustKph: merra2, precipitationMm: daymet,
    precipitationType: derived, snowfallCm: derived, snowWaterEquivalentMm: daymet,
    cloudCoverPct: merra2, cloudTransmissionPct: derived, visibilityKm: derived,
    globalHorizontalIrradianceWm2: daymet, directNormalIrradianceWm2: derived,
    diffuseHorizontalIrradianceWm2: derived, solarElevationDeg: derived, solarAzimuthDeg: derived,
  };
}

export function normalizeWeatherYear({ request, year, daily, hourly, providerSet }) {
  const sourceHours = ensureSortedContinuousHours(hourly);
  const anchors = new Map(daily.map((anchor) => [anchor.date, anchor]));
  const output = [];
  let precipitationTiming = false;
  let radiationTiming = false;
  let daymetCalendarAdjusted = false;
  const groups = localYearDayGroups(sourceHours, year, request.timezone);
  invariant(groups.length >= 365, 'PROVIDER_RESPONSE_INVALID', `MERRA-2 does not cover every local day in ${year}.`, {
    details: { year, timezone: request.timezone, localDays: groups.length },
  });
  for (const [date, dayHours] of groups) {
    invariant(dayHours.length >= 23 && dayHours.length <= 25, 'PROVIDER_RESPONSE_INVALID',
      `MERRA-2 does not cover every local hour on ${date}.`, {
        details: { date, timezone: request.timezone, localHours: dayHours.length },
      });
    const result = anchorForDate(anchors, date);
    const anchor = result.anchor;
    daymetCalendarAdjusted ||= result.adjustedCalendar;
    const temperatures = normalizedTemperature(dayHours, anchor);
    const humidity = normalizedHumidity(dayHours, temperatures, anchor.vaporPressurePa);
    const precipitation = normalizedPrecipitation(dayHours, anchor.precipitationMm);
    const radiation = normalizedRadiation(dayHours, anchor, request.latitude, request.longitude);
    precipitationTiming ||= precipitation.constrainedTiming;
    radiationTiming ||= radiation.constrainedTiming;
    for (let index = 0; index < dayHours.length; index += 1) {
      const source = dayHours[index];
      const temperatureC = temperatures[index];
      const humidityPct = humidity[index];
      const wetBulb = wetBulbC(temperatureC, humidityPct, source.pressureHpa);
      const windSpeedKph = Math.hypot(source.uWindMps, source.vWindMps) * 3.6;
      const cloudCoverPct = clamp(source.cloudCoverPct, 0, 100);
      const geometry = solarGeometry(request.latitude, request.longitude, new Date(source.at));
      const global = radiation.global[index];
      const cloudTransmissionPct = geometry.clearSkyWm2 > 1 ? clamp(global / geometry.clearSkyWm2 * 100, 0, 100) : 0;
      const directFraction = clamp(0.76 - cloudCoverPct / 145 + Math.max(0, geometry.elevationDeg) / 300, 0.12, 0.88);
      const directHorizontalWm2 = global * directFraction;
      const sineElevation = Math.sin(Math.max(0, geometry.elevationDeg) * Math.PI / 180);
      // Future slope/aspect physics needs direct-normal irradiance (DNI), not
      // merely the direct horizontal component of GHI.
      const directNormalWm2 = sineElevation > 0.03 ? directHorizontalWm2 / sineElevation : 0;
      const phase = phaseAndSnowfall(precipitation.values[index], wetBulb);
      const fieldFlags = (providerSet.mode === 'fixture' ? 1 : 0)
        | (precipitation.constrainedTiming ? 2 : 0) | (radiation.constrainedTiming ? 4 : 0)
        | (result.adjustedCalendar ? 8 : 0);
      output.push({
        at: source.at, temperatureC, wetBulbC: wetBulb, humidityPct, precipitationMm: precipitation.values[index], ...phase,
        windSpeedKph, windGustKph: windSpeedKph * (1.12 + cloudCoverPct / 300),
        windDirectionDeg: windDirectionDeg(source.uWindMps, source.vWindMps), cloudCoverPct,
        visibilityKm: clamp(35 - cloudCoverPct * 0.16 - precipitation.values[index] * 2.2, 0.2, 50),
        pressureHpa: source.pressureHpa, radiationWm2: global, windUms: source.uWindMps,
        windVms: source.vWindMps, snowWaterEquivalentMm: anchor.snowWaterEquivalentMm,
        globalRadiationWm2: global, directRadiationWm2: directNormalWm2, diffuseRadiationWm2: global - directHorizontalWm2,
        cloudTransmissionPct, solarElevationDeg: geometry.elevationDeg, solarAzimuthDeg: geometry.azimuthDeg,
        provenance: { fieldFlags },
      });
    }
  }
  return {
    hours: output,
    provenance: fieldProvenance(providerSet, { precipitationTiming, radiationTiming }),
    flags: { precipitationTiming, radiationTiming, daymetCalendarAdjusted },
  };
}

function manifestContentHash(request, providerSet, chunks, { sources, sourceDetails, coverage }) {
  return sha256(stableJson({
    schemaVersion: WEATHER_PACKAGE_SCHEMA_VERSION, generatorVersion: 2,
    terrainKey: request.terrainKey, terrainBinding: request.terrainBinding, timezone: request.timezone,
    timezoneResolution: request.timezoneResolution, midpoint: { latitude: request.latitude, longitude: request.longitude },
    sourcePolicyVersion: request.sourcePolicyVersion, sourceVersion: providerSet.sourceVersion, sourceSummary: providerSet.sourceSummary,
    quality: providerSet.quality,
    chunks: chunks.map(({ descriptor }) => descriptor), sources, sourceDetails, coverage,
  }));
}

function sourceDescriptors(providerSet, sourceDetails) {
  const fixture = providerSet.mode === 'fixture';
  const first = sourceDetails[0] ?? {};
  const source = (provider, version, sourceId, citation, quality) => ({ provider, version, ...(sourceId ? { sourceId } : {}), citation, quality });
  if (fixture) {
    return [source('legacy', 'fixture-v1', 'deterministic-development-fixture', undefined, 'limited')];
  }
  return [
    source('daymet', providerSet.daymet.version, first.daymet?.grid?.id, 'https://daac.ornl.gov/DAYMET/guides/Daymet_Daily_V4R1.html', 'estimated'),
    source('merra-2', providerSet.merra2.version, first.merra2?.grid?.id, 'https://gmao.gsfc.nasa.gov/gmao-products/merra-2/', 'estimated'),
  ];
}

function sourcePayloadHashes(daymet, merra2) {
  // These hashes are of normalized provider subsets, not their bulky raw
  // downloads. They let a package audit prove exactly which service-time
  // inputs produced each year without retaining provider files on a map.
  return {
    daymet: sha256(stableJson({ provider: daymet.provider, version: daymet.version, grid: daymet.sourceGrid, days: daymet.days })),
    merra2: sha256(stableJson({ provider: merra2.provider, version: merra2.version, grid: merra2.sourceGrid, hours: merra2.hours })),
  };
}

export class WeatherPackageBuilder {
  constructor({ providerSet, artifactStore, now = () => new Date() }) {
    this.providerSet = providerSet;
    this.artifactStore = artifactStore;
    this.now = now;
  }

  async build(request, { signal, onProgress = () => undefined } = {}) {
    const fingerprint = packageRequestFingerprint(request);
    const cached = await this.artifactStore.findByRequestFingerprint(fingerprint);
    if (cached) {
      onProgress({ stage: 'cache', completed: 1, total: 1, message: 'Reused a verified local weather package.' });
      const packageArtifact = await this.artifactStore.packageWithChunks(cached.contentHash);
      return { ...packageArtifact, contentHash: cached.contentHash, cacheHit: true };
    }
    const years = [];
    for (let year = request.historicalStartYear; year <= request.historicalEndYear; year += 1) years.push(year);
    const chunks = [];
    const sourceDetails = [];
    // A local Dec 31 can extend into UTC Jan 1. Carry the next MERRA year
    // forward through the loop so each historical chunk is a complete local
    // calendar year while source subsets remain centrally cached by grid cell.
    let prefetchedMerra2 = null;
    const context = {
      signal,
      throwIfAborted: () => {
        if (signal?.aborted) throw new WeatherServiceError('BUILD_CANCELLED', 'Weather package preparation was cancelled.', { status: 409 });
      },
    };
    for (let index = 0; index < years.length; index += 1) {
      const year = years[index];
      context.throwIfAborted();
      const base = index * 3;
      onProgress({ stage: 'daymet', completed: base, total: years.length * 3, message: `Fetching Daymet daily constraints for ${year}.`, year });
      const daymet = await this.providerSet.daymet.getDaily(request, year, context);
      onProgress({ stage: 'merra2', completed: base + 1, total: years.length * 3, message: `Fetching MERRA-2 hourly atmosphere and local-day boundary for ${year}.`, year });
      const merra2 = prefetchedMerra2 ?? await this.providerSet.merra2.getHourly(request, year, context);
      const nextMerra2 = await this.providerSet.merra2.getHourly(request, year + 1, context);
      prefetchedMerra2 = nextMerra2;
      onProgress({ stage: 'normalizing', completed: base + 2, total: years.length * 3, message: `Normalizing hourly weather fields for ${year}.`, year });
      const normalized = normalizeWeatherYear({ request, year, daily: daymet.days, hourly: [...merra2.hours, ...nextMerra2.hours], providerSet: this.providerSet });
      chunks.push(encodeWeatherHours(normalized.hours, year, normalized.provenance));
      sourceDetails.push({ year, daymet: { provider: daymet.provider, version: daymet.version, grid: daymet.sourceGrid },
        merra2: { provider: merra2.provider, version: merra2.version, grid: merra2.sourceGrid, localBoundaryYear: year + 1 },
        sourceHashes: sourcePayloadHashes(daymet, merra2), flags: normalized.flags });
    }
    onProgress({ stage: 'packing', completed: years.length * 3, total: years.length * 3, message: 'Validating and compressing immutable weather chunks.' });
    const sources = sourceDescriptors(this.providerSet, sourceDetails);
    const coverage = {
      localCalendar: true,
      historicalStartYear: request.historicalStartYear,
      historicalEndYear: request.historicalEndYear,
      // Every local year is normalized with the first UTC hours of the next
      // MERRA year. This keeps all 50 U.S. time zones complete at year-end.
      merraBoundaryEndYear: request.historicalEndYear + 1,
    };
    const contentHash = manifestContentHash(request, this.providerSet, chunks, { sources, sourceDetails, coverage });
    const manifest = {
      schemaVersion: WEATHER_PACKAGE_SCHEMA_VERSION, terrainKey: request.terrainKey, terrainBinding: request.terrainBinding,
      timezone: request.timezone, historicalStartYear: request.historicalStartYear, historicalEndYear: request.historicalEndYear,
      quality: this.providerSet.quality, sourceSummary: this.providerSet.sourceSummary, sourceVersion: this.providerSet.sourceVersion,
      generatorVersion: 2, contentHash, complete: true, immutable: true, createdAt: this.now().toISOString(),
      sourcePolicyVersion: request.sourcePolicyVersion, midpoint: { latitude: request.latitude, longitude: request.longitude },
      chunks: chunks.map((chunk) => chunk.descriptor),
      sources, timezoneResolution: request.timezoneResolution, coverage, sourceDetails,
    };
    onProgress({ stage: 'installing', completed: years.length * 4, total: years.length * 4, message: 'Installing validated offline weather package.' });
    await this.artifactStore.install(fingerprint, { manifest, chunks });
    onProgress({ stage: 'complete', completed: years.length * 4, total: years.length * 4, message: 'Offline weather package is ready.' });
    return { manifest, chunks: chunks.map((chunk) => ({ descriptor: chunk.descriptor, dataBase64: chunk.data.toString('base64') })), historicalYears: [], contentHash, cacheHit: false };
  }
}
