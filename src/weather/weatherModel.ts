/**
 * Dependency-neutral weather domain contracts.
 *
 * A weather package is an immutable, offline artifact. Provider adapters may
 * use their own source shapes, but must normalize into these contracts before
 * a package is installed. Nothing in this module performs network or storage
 * work.
 */

export const WEATHER_PACKAGE_SCHEMA_VERSION = 2 as const;
export const WEATHER_CHUNK_FORMAT = 'weather-hour-v2' as const;
const WEATHER_CHUNK_HEADER_BYTES = 16;
const WEATHER_CHUNK_RECORD_BYTES = 96;

export type WeatherQuality = 'verified' | 'estimated' | 'limited';
export type WeatherEventType = 'storm' | 'freeze-thaw' | 'cold-snap' | 'warm-up';
export type StormStyle =
  | 'pacific-system' | 'atmospheric-river' | 'nor-easter' | 'clipper'
  | 'lake-effect' | 'upslope' | 'frontal' | 'tropical-remnant' | 'convective';
export type PrecipitationType = 'none' | 'rain' | 'mixed' | 'snow' | 'freezing-rain';

/** `ghcnh` remains decoder-only compatibility for already-installed packages. */
export type WeatherSourceProvider = 'daymet' | 'merra-2' | 'ghcnh' | 'derived' | 'legacy';
export type WeatherSourceCorrection = 'none' | 'daymet-constrained' | 'station-corrected' | 'derived';

/** Every value the package can expose to a future snow-cover engine. */
export type WeatherHourlyField =
  | 'airTemperatureC'
  | 'wetBulbC'
  | 'relativeHumidityPct'
  | 'surfacePressureHpa'
  | 'windUms'
  | 'windVms'
  | 'windGustKph'
  | 'precipitationMm'
  | 'precipitationType'
  | 'snowfallCm'
  | 'snowWaterEquivalentMm'
  | 'cloudCoverPct'
  | 'cloudTransmissionPct'
  | 'visibilityKm'
  | 'globalHorizontalIrradianceWm2'
  | 'directNormalIrradianceWm2'
  | 'diffuseHorizontalIrradianceWm2'
  | 'solarElevationDeg'
  | 'solarAzimuthDeg';

/**
 * Provenance is deliberately field-level: MERRA-2 supplies hourly atmosphere
 * while Daymet constrains daily temperature, precipitation, and radiation.
 */
export interface WeatherFieldProvenance {
  provider: WeatherSourceProvider;
  quality: WeatherQuality;
  sourceVersion: string;
  sourceId?: string;
  correction: WeatherSourceCorrection;
}

export type WeatherFieldProvenanceMap = Readonly<Partial<Record<WeatherHourlyField, WeatherFieldProvenance>>>;

/** A compact per-hour flag stored in binary chunks, plus optional expanded metadata. */
export interface WeatherHourProvenance {
  /** Bitset defined by the package builder; retained through decoding. */
  fieldFlags: number;
  fields?: WeatherFieldProvenanceMap;
}

/**
 * Compatibility record consumed by the existing Lab. The original fields stay
 * required. V2 fields are optional at this boundary because legacy v1 packages
 * can still be opened; WeatherSession resolves them before simulation output.
 */
export interface WeatherReferenceHour {
  at: string;
  temperatureC: number;
  wetBulbC: number;
  humidityPct: number;
  precipitationMm: number;
  precipitationType: PrecipitationType;
  snowfallCm: number;
  windSpeedKph: number;
  windGustKph: number;
  windDirectionDeg: number;
  cloudCoverPct: number;
  visibilityKm: number;
  pressureHpa: number;
  /** Legacy alias for global horizontal radiation. */
  radiationWm2: number;

  windUms?: number;
  windVms?: number;
  snowWaterEquivalentMm?: number;
  globalRadiationWm2?: number;
  directRadiationWm2?: number;
  diffuseRadiationWm2?: number;
  cloudTransmissionPct?: number;
  solarElevationDeg?: number;
  solarAzimuthDeg?: number;
  provenance?: WeatherHourProvenance;
}

/** Fully resolved hourly atmosphere returned by WeatherSession. */
export interface ResolvedWeatherHour extends WeatherReferenceHour {
  windUms: number;
  windVms: number;
  snowWaterEquivalentMm: number;
  globalRadiationWm2: number;
  directRadiationWm2: number;
  diffuseRadiationWm2: number;
  cloudTransmissionPct: number;
  solarElevationDeg: number;
  solarAzimuthDeg: number;
  provenance: WeatherHourProvenance;
}

export interface HistoricalWeatherYear {
  year: number;
  hours: readonly WeatherReferenceHour[];
}

export interface WeatherCoordinates {
  latitude: number;
  longitude: number;
}

export interface WeatherBounds {
  west: number;
  south: number;
  east: number;
  north: number;
}

export interface WeatherPackageRequest {
  /** Schema 1 is accepted by the transitional local builder. */
  schemaVersion: 1 | typeof WEATHER_PACKAGE_SCHEMA_VERSION;
  terrainKey: string;
  terrainBinding: string;
  latitude: number;
  longitude: number;
  bounds?: WeatherBounds;
  areaSizeMeters?: number;
  /** 'auto' is allowed only on the builder request; a manifest must have IANA. */
  timezone: string;
  historicalStartYear: number;
  historicalEndYear: number;
  sourcePolicyVersion: string;
}

export interface WeatherSourceDescriptor {
  provider: WeatherSourceProvider;
  version: string;
  /** Provider grid cell, station id, or other immutable source identifier. */
  sourceId?: string;
  citation?: string;
  quality: WeatherQuality;
}

/** Auditable service-time coverage; runtime never uses it to make a network request. */
export interface WeatherPackageCoverage {
  localCalendar: boolean;
  historicalStartYear: number;
  historicalEndYear: number;
  /** Adjacent MERRA UTC year used to finish the final local calendar day. */
  merraBoundaryEndYear?: number;
}

export interface WeatherSourceGrid {
  id: string;
  resolutionMeters?: number;
  resolutionDegrees?: number;
  route?: string;
}

export interface WeatherSourceYearDetail {
  year: number;
  daymet: { provider: string; version: string; grid?: WeatherSourceGrid };
  merra2: { provider: string; version: string; grid?: WeatherSourceGrid; localBoundaryYear?: number };
  /** Legacy package compatibility; new packages never include station corrections. */
  ghcnh?: {
    provider: string;
    version: string;
    stations?: readonly Readonly<Record<string, unknown>>[];
    applied?: boolean;
    quality?: WeatherQuality;
  };
  /** SHA-256 of each normalized service-time source subset. */
  sourceHashes?: { daymet: string; merra2: string; ghcnh?: string };
  flags?: { precipitationTiming?: boolean; radiationTiming?: boolean; daymetCalendarAdjusted?: boolean };
}

/** Manifest metadata for one compressed year of normalized hourly values. */
export interface WeatherChunkDescriptor {
  id: string;
  year: number;
  startsAt: string;
  endsAt: string;
  encoding: 'gzip' | 'identity';
  format: typeof WEATHER_CHUNK_FORMAT;
  /** SHA-256 of the encoded bytes, represented as lower-case hexadecimal. */
  checksumSha256: string;
  byteLength: number;
  uncompressedByteLength: number;
  recordCount: number;
  fieldProvenance: WeatherFieldProvenanceMap;
}

/** Portable transport form. Storage adapters may retain the base64 as a Blob instead. */
export interface WeatherChunk {
  descriptor: WeatherChunkDescriptor;
  dataBase64: string;
}

/**
 * Schema v1 was the temporary JSON package used by the initial Lab. Schema v2
 * is the durable manifest for content-addressed binary chunks. Keeping the
 * union avoids making old prepared maps unreadable during the migration.
 */
export interface WeatherPackageManifest {
  schemaVersion: 1 | typeof WEATHER_PACKAGE_SCHEMA_VERSION;
  terrainKey: string;
  terrainBinding: string;
  timezone: string;
  historicalStartYear: number;
  historicalEndYear: number;
  quality: WeatherQuality;
  sourceSummary: string;
  sourceVersion: string;
  generatorVersion: number;
  contentHash: string;
  complete: boolean;
  createdAt: string;

  /** V2-only metadata. */
  sourcePolicyVersion?: string;
  midpoint?: WeatherCoordinates;
  sources?: readonly WeatherSourceDescriptor[];
  chunks?: readonly WeatherChunkDescriptor[];
  immutable?: boolean;
  timezoneResolution?: string;
  coverage?: WeatherPackageCoverage;
  sourceDetails?: readonly WeatherSourceYearDetail[];
}

/**
 * The durable payload is `manifest + chunks`. `historicalYears` is retained as
 * a decoded compatibility cache for the first Weather Lab. New storage code
 * must persist binary chunks rather than this cache.
 */
export interface WeatherDataPackage {
  manifest: WeatherPackageManifest;
  chunks?: readonly WeatherChunk[];
  /**
   * Decoded in-memory compatibility cache. V2 installs intentionally omit it;
   * use loadWeatherSession to checksum/decode chunks before simulation.
   */
  historicalYears?: readonly HistoricalWeatherYear[];
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
  timezone: string;
  packageContentHash: string;
  generatorVersion: number;
  hours: readonly WeatherReferenceHour[];
  events: readonly WeatherEvent[];
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

/**
 * Terrain-resolved atmosphere for snow-cover consumers. Phase is a compact
 * typed field so a 512² map does not allocate hundreds of thousands of
 * repeated strings: 0 none, 1 rain, 2 mixed, 3 snow, 4 freezing rain.
 */
export interface TerrainWeatherField extends TemperatureField {
  wetBulbC: Float32Array;
  precipitationPhase: Uint8Array;
  /** Snow-to-liquid ratio; zero where the resolved phase contains no snow. */
  snowRatio: Float32Array;
}

export function precipitationPhaseCode(type: PrecipitationType): number {
  switch (type) {
    case 'rain': return 1;
    case 'mixed': return 2;
    case 'snow': return 3;
    case 'freezing-rain': return 4;
    default: return 0;
  }
}

export function isWeatherQuality(value: unknown): value is WeatherQuality {
  return value === 'verified' || value === 'estimated' || value === 'limited';
}

export function isWeatherFieldProvenance(value: unknown): value is WeatherFieldProvenance {
  if (!value || typeof value !== 'object') return false;
  const provenance = value as Partial<WeatherFieldProvenance>;
  return (provenance.provider === 'daymet' || provenance.provider === 'merra-2' || provenance.provider === 'ghcnh' ||
    provenance.provider === 'derived' || provenance.provider === 'legacy') &&
    isWeatherQuality(provenance.quality) && typeof provenance.sourceVersion === 'string' && provenance.sourceVersion.length > 0 &&
    (provenance.correction === 'none' || provenance.correction === 'daymet-constrained' ||
      provenance.correction === 'station-corrected' || provenance.correction === 'derived') &&
    (provenance.sourceId === undefined || typeof provenance.sourceId === 'string');
}

export function isWeatherFieldProvenanceMap(value: unknown): value is WeatherFieldProvenanceMap {
  return !!value && typeof value === 'object' && !Array.isArray(value) &&
    Object.values(value).every(isWeatherFieldProvenance);
}

export function isWeatherPackageRequest(value: unknown): value is WeatherPackageRequest {
  if (!value || typeof value !== 'object') return false;
  const request = value as Partial<WeatherPackageRequest>;
  return (request.schemaVersion === 1 || request.schemaVersion === WEATHER_PACKAGE_SCHEMA_VERSION) &&
    typeof request.terrainKey === 'string' && request.terrainKey.length > 0 &&
    typeof request.terrainBinding === 'string' && request.terrainBinding.length > 0 &&
    Number.isFinite(request.latitude) && Number.isFinite(request.longitude) &&
    typeof request.timezone === 'string' && request.timezone.length > 0 &&
    Number.isInteger(request.historicalStartYear) && Number.isInteger(request.historicalEndYear) &&
    request.historicalStartYear! <= request.historicalEndYear! &&
    typeof request.sourcePolicyVersion === 'string' && request.sourcePolicyVersion.length > 0;
}

export function isWeatherChunkDescriptor(value: unknown): value is WeatherChunkDescriptor {
  if (!value || typeof value !== 'object') return false;
  const chunk = value as Partial<WeatherChunkDescriptor>;
  const byteLength = chunk.byteLength;
  const uncompressedByteLength = chunk.uncompressedByteLength;
  const recordCount = chunk.recordCount;
  const startsAt = typeof chunk.startsAt === 'string' ? Date.parse(chunk.startsAt) : Number.NaN;
  const endsAt = typeof chunk.endsAt === 'string' ? Date.parse(chunk.endsAt) : Number.NaN;
  return typeof chunk.id === 'string' && chunk.id.length > 0 && Number.isInteger(chunk.year) &&
    Number.isFinite(startsAt) && Number.isFinite(endsAt) && startsAt <= endsAt &&
    (chunk.encoding === 'gzip' || chunk.encoding === 'identity') && chunk.format === WEATHER_CHUNK_FORMAT &&
    typeof chunk.checksumSha256 === 'string' && /^[a-f0-9]{64}$/.test(chunk.checksumSha256) &&
    typeof byteLength === 'number' && Number.isInteger(byteLength) && byteLength > 0 &&
    typeof uncompressedByteLength === 'number' && Number.isInteger(uncompressedByteLength) &&
    uncompressedByteLength === WEATHER_CHUNK_HEADER_BYTES + WEATHER_CHUNK_RECORD_BYTES * recordCount! &&
    typeof recordCount === 'number' && Number.isInteger(recordCount) && recordCount > 0 &&
    isWeatherFieldProvenanceMap(chunk.fieldProvenance);
}

function isSourceGrid(value: unknown): value is WeatherSourceGrid {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const grid = value as Partial<WeatherSourceGrid>;
  return typeof grid.id === 'string' && grid.id.length > 0 &&
    (grid.resolutionMeters === undefined || Number.isFinite(grid.resolutionMeters)) &&
    (grid.resolutionDegrees === undefined || Number.isFinite(grid.resolutionDegrees)) &&
    (grid.route === undefined || typeof grid.route === 'string');
}

function isWeatherPackageCoverage(value: unknown): value is WeatherPackageCoverage {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const coverage = value as Partial<WeatherPackageCoverage>;
  return coverage.localCalendar === true && Number.isInteger(coverage.historicalStartYear) &&
    Number.isInteger(coverage.historicalEndYear) &&
    (coverage.merraBoundaryEndYear === undefined || Number.isInteger(coverage.merraBoundaryEndYear));
}

function isSourceDetailPart(value: unknown): value is { provider: string; version: string; grid?: WeatherSourceGrid } {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const source = value as { provider?: unknown; version?: unknown; grid?: unknown };
  return typeof source.provider === 'string' && source.provider.length > 0 &&
    typeof source.version === 'string' && source.version.length > 0 &&
    (source.grid === undefined || isSourceGrid(source.grid));
}

function isWeatherSourceYearDetail(value: unknown): value is WeatherSourceYearDetail {
  if (!value || typeof value !== 'object' || Array.isArray(value)) return false;
  const detail = value as Partial<WeatherSourceYearDetail>;
  if (!Number.isInteger(detail.year) || !isSourceDetailPart(detail.daymet) || !isSourceDetailPart(detail.merra2) ||
    (detail.ghcnh !== undefined && !isSourceDetailPart(detail.ghcnh))) return false;
  const merra2 = detail.merra2 as { localBoundaryYear?: unknown };
  if (merra2.localBoundaryYear !== undefined && !Number.isInteger(merra2.localBoundaryYear)) return false;
  const ghcnh = detail.ghcnh as { stations?: unknown; applied?: unknown; quality?: unknown } | undefined;
  if (ghcnh?.stations !== undefined && !Array.isArray(ghcnh.stations)) return false;
  if (ghcnh?.applied !== undefined && typeof ghcnh.applied !== 'boolean') return false;
  if (ghcnh?.quality !== undefined && !isWeatherQuality(ghcnh.quality)) return false;
  const hashes = detail.sourceHashes;
  if (hashes && (!/^[a-f0-9]{64}$/.test(hashes.daymet) || !/^[a-f0-9]{64}$/.test(hashes.merra2) ||
    (hashes.ghcnh !== undefined && !/^[a-f0-9]{64}$/.test(hashes.ghcnh)))) return false;
  return true;
}

function hasExactChunkYearCoverage(
  chunks: readonly WeatherChunkDescriptor[],
  historicalStartYear: number,
  historicalEndYear: number,
): boolean {
  const expectedCount = historicalEndYear - historicalStartYear + 1;
  if (!Number.isSafeInteger(expectedCount) || expectedCount <= 0 || chunks.length !== expectedCount) return false;
  const ids = new Set<string>();
  const years = new Set<number>();
  for (const chunk of chunks) {
    if (ids.has(chunk.id) || years.has(chunk.year) || chunk.year < historicalStartYear || chunk.year > historicalEndYear) return false;
    ids.add(chunk.id);
    years.add(chunk.year);
  }
  return true;
}

function hasExactSourceDetailCoverage(
  details: readonly WeatherSourceYearDetail[],
  historicalStartYear: number,
  historicalEndYear: number,
): boolean {
  const expectedCount = historicalEndYear - historicalStartYear + 1;
  if (details.length !== expectedCount) return false;
  const years = new Set<number>();
  for (const detail of details) {
    if (years.has(detail.year) || detail.year < historicalStartYear || detail.year > historicalEndYear) return false;
    years.add(detail.year);
  }
  return true;
}

function fieldProvenanceMatches(left: WeatherFieldProvenanceMap, right: WeatherFieldProvenanceMap): boolean {
  const leftEntries = Object.entries(left).sort(([a], [b]) => a.localeCompare(b));
  const rightEntries = Object.entries(right).sort(([a], [b]) => a.localeCompare(b));
  return leftEntries.length === rightEntries.length && leftEntries.every(([key, value], index) => {
    const candidate = rightEntries[index];
    return candidate?.[0] === key && candidate[1].provider === value.provider && candidate[1].quality === value.quality &&
      candidate[1].sourceVersion === value.sourceVersion && candidate[1].sourceId === value.sourceId &&
      candidate[1].correction === value.correction;
  });
}

function chunkDescriptorMatches(left: WeatherChunkDescriptor, right: WeatherChunkDescriptor): boolean {
  return left.id === right.id && left.year === right.year && left.startsAt === right.startsAt && left.endsAt === right.endsAt &&
    left.encoding === right.encoding && left.format === right.format && left.checksumSha256 === right.checksumSha256 &&
    left.byteLength === right.byteLength && left.uncompressedByteLength === right.uncompressedByteLength &&
    left.recordCount === right.recordCount && fieldProvenanceMatches(left.fieldProvenance, right.fieldProvenance);
}

export function isWeatherPackageManifest(value: unknown): value is WeatherPackageManifest {
  if (!value || typeof value !== 'object') return false;
  const manifest = value as Partial<WeatherPackageManifest>;
  const common = (manifest.schemaVersion === 1 || manifest.schemaVersion === WEATHER_PACKAGE_SCHEMA_VERSION) &&
    manifest.complete === true && typeof manifest.terrainKey === 'string' && manifest.terrainKey.length > 0 &&
    typeof manifest.terrainBinding === 'string' && manifest.terrainBinding.length > 0 &&
    typeof manifest.timezone === 'string' && manifest.timezone.length > 0 &&
    Number.isInteger(manifest.historicalStartYear) && Number.isInteger(manifest.historicalEndYear) &&
    manifest.historicalStartYear! <= manifest.historicalEndYear! &&
    isWeatherQuality(manifest.quality) &&
    typeof manifest.sourceSummary === 'string' && typeof manifest.sourceVersion === 'string' &&
    Number.isInteger(manifest.generatorVersion) && typeof manifest.contentHash === 'string' &&
    manifest.contentHash.length > 0 && typeof manifest.createdAt === 'string';
  if (!common) return false;
  if (manifest.schemaVersion === 1) return true;
  const coverageMatchesManifest = manifest.coverage === undefined ||
    (isWeatherPackageCoverage(manifest.coverage) && manifest.coverage.historicalStartYear === manifest.historicalStartYear &&
      manifest.coverage.historicalEndYear === manifest.historicalEndYear &&
      (manifest.coverage.merraBoundaryEndYear === undefined || manifest.coverage.merraBoundaryEndYear >= manifest.historicalEndYear));
  const detailsMatchManifest = manifest.sourceDetails === undefined ||
    (Array.isArray(manifest.sourceDetails) && manifest.sourceDetails.every(isWeatherSourceYearDetail) &&
      hasExactSourceDetailCoverage(manifest.sourceDetails, manifest.historicalStartYear!, manifest.historicalEndYear!));
  return typeof manifest.sourcePolicyVersion === 'string' && manifest.sourcePolicyVersion.length > 0 &&
    Array.isArray(manifest.chunks) && manifest.chunks.every(isWeatherChunkDescriptor) &&
    hasExactChunkYearCoverage(manifest.chunks, manifest.historicalStartYear!, manifest.historicalEndYear!) &&
    manifest.immutable === true && /^[a-f0-9]{64}$/.test(manifest.contentHash!) && coverageMatchesManifest && detailsMatchManifest;
}

export function isWeatherDataPackage(value: unknown): value is WeatherDataPackage {
  if (!value || typeof value !== 'object') return false;
  const weatherPackage = value as Partial<WeatherDataPackage>;
  if (!isWeatherPackageManifest(weatherPackage.manifest)) return false;
  if (weatherPackage.manifest.schemaVersion === 1) {
    return Array.isArray(weatherPackage.historicalYears) && weatherPackage.historicalYears.every(isHistoricalWeatherYear);
  }
  const declaredChunks = weatherPackage.manifest.chunks!;
  if (!Array.isArray(weatherPackage.chunks) || weatherPackage.chunks.length !== declaredChunks.length) {
    return false;
  }
  const payloadChunkIds = new Set<string>();
  for (const chunk of weatherPackage.chunks) {
    if (!isWeatherChunkDescriptor(chunk?.descriptor) || typeof chunk.dataBase64 !== 'string' || chunk.dataBase64.length === 0 ||
      payloadChunkIds.has(chunk.descriptor.id)) return false;
    payloadChunkIds.add(chunk.descriptor.id);
    const declared = declaredChunks.find((candidate) => candidate.id === chunk.descriptor.id);
    if (!declared || !chunkDescriptorMatches(declared, chunk.descriptor)) return false;
  }
  return payloadChunkIds.size === declaredChunks.length;
}

/**
 * Freeze package containers at the installation boundary. Typed payload bytes
 * remain encoded strings, so no mutable ArrayBuffer is exposed from this model.
 */
export function immutableWeatherDataPackage(weatherPackage: WeatherDataPackage): WeatherDataPackage {
  if (!isWeatherDataPackage(weatherPackage)) throw new Error('Cannot freeze an invalid offline weather package.');
  const manifest = Object.freeze({
    ...weatherPackage.manifest,
    ...(weatherPackage.manifest.chunks ? { chunks: Object.freeze([...weatherPackage.manifest.chunks]) } : {}),
    ...(weatherPackage.manifest.sources ? { sources: Object.freeze([...weatherPackage.manifest.sources]) } : {}),
  });
  return Object.freeze({
    ...weatherPackage,
    manifest,
    ...(weatherPackage.chunks ? { chunks: Object.freeze([...weatherPackage.chunks]) } : {}),
    ...(weatherPackage.historicalYears ? { historicalYears: Object.freeze([...weatherPackage.historicalYears]) } : {}),
  });
}

function isHistoricalWeatherYear(value: unknown): value is HistoricalWeatherYear {
  if (!value || typeof value !== 'object') return false;
  const year = value as Partial<HistoricalWeatherYear>;
  return Number.isInteger(year.year) && Array.isArray(year.hours) && year.hours.every(isWeatherReferenceHour);
}

export function isWeatherReferenceHour(value: unknown): value is WeatherReferenceHour {
  if (!value || typeof value !== 'object') return false;
  const hour = value as Partial<WeatherReferenceHour>;
  return typeof hour.at === 'string' && !Number.isNaN(Date.parse(hour.at)) &&
    Number.isFinite(hour.temperatureC) && Number.isFinite(hour.wetBulbC) && Number.isFinite(hour.humidityPct) &&
    Number.isFinite(hour.precipitationMm) && isPrecipitationType(hour.precipitationType) &&
    Number.isFinite(hour.snowfallCm) && Number.isFinite(hour.windSpeedKph) && Number.isFinite(hour.windGustKph) &&
    Number.isFinite(hour.windDirectionDeg) && Number.isFinite(hour.cloudCoverPct) &&
    Number.isFinite(hour.visibilityKm) && Number.isFinite(hour.pressureHpa) && Number.isFinite(hour.radiationWm2);
}

export function isPrecipitationType(value: unknown): value is PrecipitationType {
  return value === 'none' || value === 'rain' || value === 'mixed' || value === 'snow' || value === 'freezing-rain';
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

function round(value: number, digits = 1): number {
  const factor = 10 ** digits;
  return Math.round(value * factor) / factor;
}

function cloneHour(hour: WeatherReferenceHour, at: string, anomaly: number): WeatherReferenceHour {
  const temperatureC = round(hour.temperatureC + anomaly);
  const wetBulbC = round(hour.wetBulbC + anomaly);
  const precipitationType = precipitationTypeFor(temperatureC, wetBulbC, hour.precipitationMm);
  return {
    ...hour,
    at,
    temperatureC,
    wetBulbC,
    precipitationType,
    snowfallCm: precipitationType === 'snow' ? hour.snowfallCm : precipitationType === 'mixed' ? round(hour.snowfallCm * 0.5, 2) : 0,
  };
}

export function precipitationTypeFor(temperatureC: number, wetBulbC: number, precipitationMm: number): PrecipitationType {
  if (precipitationMm <= 0.001) return 'none';
  if (wetBulbC <= -1) return 'snow';
  if (wetBulbC < 1) return 'mixed';
  if (temperatureC < 0 && wetBulbC < 2) return 'freezing-rain';
  return 'rain';
}

function stormStyleFor(hours: readonly WeatherReferenceHour[]): StormStyle {
  const duration = hours.length;
  const totalPrecipitation = hours.reduce((sum, hour) => sum + Math.max(0, hour.precipitationMm), 0);
  const meanWind = hours.reduce((sum, hour) => sum + Math.max(0, hour.windSpeedKph), 0) / Math.max(1, duration);
  const meanGust = hours.reduce((sum, hour) => sum + Math.max(0, hour.windGustKph), 0) / Math.max(1, duration);
  const meanDirection = hours.reduce((sum, hour) => sum + hour.windDirectionDeg, 0) / Math.max(1, duration);
  // Storm style deliberately does not inspect air temperature or precipitation phase.
  if (duration <= 10 && meanGust >= 55) return 'convective';
  if (duration >= 24 && totalPrecipitation >= 45) return 'atmospheric-river';
  if (duration >= 18 && meanWind >= 35 && meanDirection >= 20 && meanDirection <= 130) return 'nor-easter';
  if (duration <= 24 && meanWind >= 35) return 'clipper';
  if (meanWind >= 45) return 'frontal';
  if (duration >= 30 && meanWind < 20) return 'upslope';
  return 'pacific-system';
}

function severityFor(durationHours: number, magnitude: number): WeatherEvent['severity'] {
  if (durationHours >= 36 || magnitude >= 45) return 'major';
  if (durationHours >= 12 || magnitude >= 12) return 'notable';
  return 'minor';
}

/**
 * Event rules are pure and shared by historical and synthetic timelines. They
 * use duration and anomaly prerequisites so a single cold hour is not called a
 * cold snap and a brief shower is not called a storm.
 */
export function detectWeatherEvents(hours: readonly WeatherReferenceHour[]): WeatherEvent[] {
  const events: WeatherEvent[] = [];
  const pushRun = (type: WeatherEventType, start: number, endExclusive: number, style?: StormStyle) => {
    const run = hours.slice(start, endExclusive);
    if (run.length === 0) return;
    const magnitude = type === 'storm'
      ? run.reduce((sum, hour) => sum + Math.max(0, hour.precipitationMm), 0)
      : Math.abs(run.reduce((sum, hour) => sum + hour.temperatureC, 0) / run.length);
    events.push({
      id: `${type}-${hours[start].at}-${hours[endExclusive - 1].at}`,
      type,
      startsAt: hours[start].at,
      endsAt: hours[endExclusive - 1].at,
      severity: severityFor(run.length, magnitude),
      ...(style ? { stormStyle: style } : {}),
    });
  };

  let runStart = -1;
  for (let index = 0; index <= hours.length; index += 1) {
    const active = index < hours.length && (hours[index].precipitationMm >= 0.2 ||
      (hours[index].cloudCoverPct >= 90 && hours[index].windSpeedKph >= 30));
    if (active && runStart < 0) runStart = index;
    if (!active && runStart >= 0) {
      if (index - runStart >= 3) pushRun('storm', runStart, index, stormStyleFor(hours.slice(runStart, index)));
      runStart = -1;
    }
  }

  const trailingMean = (index: number): number => {
    const start = Math.max(0, index - 168);
    const samples = hours.slice(start, index);
    return samples.length ? samples.reduce((sum, hour) => sum + hour.temperatureC, 0) / samples.length : hours[index].temperatureC;
  };
  for (const [type, direction] of [['cold-snap', -1], ['warm-up', 1]] as const) {
    runStart = -1;
    for (let index = 0; index <= hours.length; index += 1) {
      const hour = hours[index];
      const anomaly = hour ? hour.temperatureC - trailingMean(index) : 0;
      const active = !!hour && direction * anomaly >= 5;
      if (active && runStart < 0) runStart = index;
      if (!active && runStart >= 0) {
        if (index - runStart >= 18) pushRun(type, runStart, index);
        runStart = -1;
      }
    }
  }

  let cycleStart = -1;
  let sawFreeze = false;
  let sawThaw = false;
  for (let index = 0; index <= hours.length; index += 1) {
    const hour = hours[index];
    if (hour && cycleStart < 0) cycleStart = index;
    if (hour) {
      sawFreeze ||= hour.temperatureC <= -1;
      sawThaw ||= hour.temperatureC >= 1;
    }
    const duration = cycleStart < 0 ? 0 : index - cycleStart;
    if ((sawFreeze && sawThaw && duration >= 6) || duration >= 48 || !hour) {
      if (cycleStart >= 0 && sawFreeze && sawThaw && index > cycleStart) pushRun('freeze-thaw', cycleStart, index);
      cycleStart = hour ? index : -1;
      sawFreeze = hour?.temperatureC <= -1;
      sawThaw = hour?.temperatureC >= 1;
    }
  }
  return events.sort((left, right) => left.startsAt.localeCompare(right.startsAt) || left.type.localeCompare(right.type));
}

interface SourceDay {
  key: string;
  hoursByClock: ReadonlyMap<number, WeatherReferenceHour>;
}

function utcCalendarKey(at: string): { dayKey: string; clock: number } {
  const date = new Date(at);
  return {
    dayKey: `${date.getUTCFullYear()}-${date.getUTCMonth() + 1}-${date.getUTCDate()}`,
    clock: date.getUTCHours(),
  };
}

function sourceDays(weatherPackage: WeatherDataPackage): readonly SourceDay[] {
  const days = new Map<string, Map<number, WeatherReferenceHour>>();
  for (const historicalYear of weatherPackage.historicalYears ?? []) for (const hour of historicalYear.hours) {
    const { dayKey, clock } = utcCalendarKey(hour.at);
    const day = days.get(dayKey) ?? new Map<number, WeatherReferenceHour>();
    day.set(clock, hour);
    days.set(dayKey, day);
  }
  return [...days.entries()]
    .sort(([left], [right]) => left.localeCompare(right))
    .map(([key, hoursByClock]) => ({ key, hoursByClock }));
}

/**
 * Uses multi-day historical analog blocks. Package bytes are never mutated;
 * seed changes only select analog blocks and bounded temperature anomalies.
 * UTC grouping is retained for legacy packages; WeatherSession supplies the
 * timezone-aware calendar and playback layer for v2 simulation.
 */
export function generateSyntheticWeather(
  weatherPackage: WeatherDataPackage,
  startsAt: string,
  seed: string,
  days = 366,
): SyntheticWeatherPlan {
  if (!weatherPackage.manifest.complete || !weatherPackage.historicalYears || weatherPackage.historicalYears.length === 0) {
    throw new Error('A complete offline weather package with decoded hourly history is required.');
  }
  const source = sourceDays(weatherPackage);
  if (source.length === 0) throw new Error('Weather package has no hourly history.');
  const start = new Date(startsAt);
  if (Number.isNaN(start.getTime())) throw new Error('Synthetic weather start time is invalid.');
  const random = seeded(seed);
  const hours: WeatherReferenceHour[] = [];
  let sourceIndex = Math.floor(random() * source.length);
  let blockDaysRemaining = 0;
  let anomaly = 0;
  for (let day = 0; day < days; day += 1) {
    if (blockDaysRemaining <= 0) {
      sourceIndex = Math.floor(random() * source.length);
      blockDaysRemaining = 2 + Math.floor(random() * 4);
      anomaly = round((random() - 0.5) * 2, 1);
    }
    const sourceDay = source[sourceIndex % source.length];
    for (let clock = 0; clock < 24; clock += 1) {
      const at = new Date(start.getTime() + (day * 24 + clock) * 3_600_000).toISOString();
      const sourceHour = sourceDay.hoursByClock.get(clock) ?? sourceDay.hoursByClock.values().next().value as WeatherReferenceHour | undefined;
      if (!sourceHour) throw new Error(`Weather package source day ${sourceDay.key} has no hourly records.`);
      hours.push(cloneHour(sourceHour, at, anomaly));
    }
    sourceIndex += 1;
    blockDaysRemaining -= 1;
  }
  return {
    seed,
    startsAt: start.toISOString(),
    endsAt: hours.at(-1)?.at ?? start.toISOString(),
    timezone: weatherPackage.manifest.timezone,
    packageContentHash: weatherPackage.manifest.contentHash,
    generatorVersion: weatherPackage.manifest.generatorVersion,
    hours,
    events: detectWeatherEvents(hours),
  };
}
