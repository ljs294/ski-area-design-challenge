import { DualClockEngine } from './dualClock/engine';
import { DEFAULT_DUAL_CONFIG, type DualAmenity, type DualCheckpoint, type ResortSimulationInput } from './dualClock/model';
import { resortRevision } from './dualClock/revision';
import { unitToLngLat } from './geo';
import { buildSkiNetwork, type SkiNetwork } from './network';
import { encodeSnowGrid, generateSnowBaseline } from './snow';
import type { GameSave, SavedWeatherRun } from './types/gameSave';
import type { SavedLift } from './types/lifts';
import type { TerrainRecord } from './types/terrain';
import type { SavedTrail } from './types/trails';
import { WEATHER_YEAR_CONFIGURATION_VERSION, weatherYearLabel } from './weather/annualWeather';
import { buildPreparedWeather } from './weather/preparedWeather';
import type { PreparedWeatherIdentity } from './weather/preparedWeatherModel';
import type { WeatherDataPackage } from './weather/weatherModel';
import { weatherTerrainBinding } from './weather/terrainBinding';
import { weatherInstantForLocal } from './weather/localTime';

export const JACKSON_INTEGRATED_FIXTURE_ID = 'jackson-black-mountain-live-v1';
export const JACKSON_INTEGRATED_TERRAIN_KEY = 'jackson-black-mountain-benchmark-44.1660_-71.1650';
export const JACKSON_INTEGRATED_REPLAY_ID = 'integrated-standard-v1';
export const JACKSON_INTEGRATED_SEED = `${JACKSON_INTEGRATED_FIXTURE_ID}:domain-v1`;

const TOP_HINTS = [[.45, .06], [.4, .14], [.5, .2], [.7, .2], [.9, .38], [.9, .48], [.72, .34], [.55, .3]] as const;

function heightAt(record: TerrainRecord, point: readonly [number, number]): number {
  const bounds = record.bounds!;
  const u = (point[0] - bounds.west) / (bounds.east - bounds.west);
  const v = (bounds.north - point[1]) / (bounds.north - bounds.south);
  const n = record.sampleGridSize, x = Math.max(0, Math.min(n - 1, u * (n - 1))), y = Math.max(0, Math.min(n - 1, v * (n - 1)));
  const x0 = Math.floor(x), y0 = Math.floor(y), x1 = Math.min(n - 1, x0 + 1), y1 = Math.min(n - 1, y0 + 1);
  const tx = x - x0, ty = y - y0, h = record.sampleHeights;
  return (h[y0 * n + x0] * (1 - tx) + h[y0 * n + x1] * tx) * (1 - ty)
    + (h[y1 * n + x0] * (1 - tx) + h[y1 * n + x1] * tx) * ty;
}

function extreme(record: TerrainRecord, hint: readonly [number, number], high: boolean): [number, number] {
  const n = record.sampleGridSize;
  const cx = Math.round(hint[0] * (n - 1)), cy = Math.round(hint[1] * (n - 1));
  const radius = Math.max(8, Math.round(n * .045));
  let bestX = cx, bestY = cy, best = high ? Number.NEGATIVE_INFINITY : Number.POSITIVE_INFINITY;
  for (let y = Math.max(0, cy - radius); y <= Math.min(n - 1, cy + radius); y += 3) {
    for (let x = Math.max(0, cx - radius); x <= Math.min(n - 1, cx + radius); x += 3) {
      const value = record.sampleHeights[y * n + x];
      if (Number.isFinite(value) && (high ? value > best : value < best)) { best = value; bestX = x; bestY = y; }
    }
  }
  return unitToLngLat(bestX / (n - 1), bestY / (n - 1), record.bounds!);
}

function distanceM(a: readonly [number, number], b: readonly [number, number]): number {
  const midLat = (a[1] + b[1]) * Math.PI / 360;
  return Math.hypot((b[0] - a[0]) * 111_320 * Math.cos(midLat), (b[1] - a[1]) * 111_320);
}

function centerline(top: readonly [number, number], base: readonly [number, number], variant: number): [number, number][] {
  const dx = base[0] - top[0], dy = base[1] - top[1], norm = Math.hypot(dx, dy) || 1;
  const amplitude = ((variant - 12) / 12) * 0.0012;
  return Array.from({ length: 7 }, (_, index) => {
    const t = index / 6, bend = Math.sin(Math.PI * t) * amplitude;
    return [top[0] + dx * t - dy / norm * bend, top[1] + dy * t + dx / norm * bend];
  });
}

function trailPolygon(line: readonly [number, number][], widthM: number): [number, number][][] {
  const midLat = line[Math.floor(line.length / 2)]![1] * Math.PI / 180;
  const dx = line[line.length - 1]![0] - line[0]![0], dy = line[line.length - 1]![1] - line[0]![1];
  const metricX = dx * 111_320 * Math.cos(midLat), metricY = dy * 111_320, norm = Math.hypot(metricX, metricY) || 1;
  const lngOffset = (-metricY / norm) * widthM / 2 / (111_320 * Math.cos(midLat));
  const latOffset = (metricX / norm) * widthM / 2 / 111_320;
  const left = line.map(([lng, lat]) => [lng + lngOffset, lat + latOffset] as [number, number]);
  const right = [...line].reverse().map(([lng, lat]) => [lng - lngOffset, lat - latOffset] as [number, number]);
  return [[...left, ...right, left[0]!]];
}

export interface IntegratedBenchmarkResort {
  readonly lifts: SavedLift[];
  readonly trails: SavedTrail[];
  readonly network: SkiNetwork;
  readonly amenities: DualAmenity[];
  readonly portal: { id: string; nodeId: string; lngLat: readonly [number, number]; capacityPerMinute: number };
}

/** Create the persisted resort through the same topology builder used by gameplay. */
export function createJacksonIntegratedResort(terrain: TerrainRecord): IntegratedBenchmarkResort {
  if (terrain.key !== JACKSON_INTEGRATED_TERRAIN_KEY || !terrain.bounds || terrain.sampleGridSize !== 2000) {
    throw new Error('Checkpoint preparation requires the frozen acquired Jackson terrain.');
  }
  const createdAt = '2026-01-15T13:00:00.000Z';
  const base = extreme(terrain, [.23, .88], false), baseHeight = heightAt(terrain, base);
  const tops = TOP_HINTS.map(hint => extreme(terrain, hint, true));
  if (tops.some(top => heightAt(terrain, top) < baseHeight + 80)) throw new Error('Jackson terrain does not provide the required lift relief.');
  const lifts: SavedLift[] = tops.map((top, index) => ({ id: `benchmark-lift-${index + 1}`, identifier: String(index + 1),
    name: `Jackson Benchmark Lift ${index + 1}`, liftTypeId: 'detachable-quad', points: [base, top],
    endpointElevM: [baseHeight, heightAt(terrain, top)], lengthM: distanceM(base, top),
    verticalM: heightAt(terrain, top) - baseHeight, status: 'complete', createdAt }));
  const trails: SavedTrail[] = Array.from({ length: 25 }, (_, index) => {
    const top = tops[index % tops.length]!, line = centerline(top, base, index);
    const elevations = line.map(point => heightAt(terrain, point));
    const lengthM = line.slice(1).reduce((sum, point, i) => sum + distanceM(line[i]!, point), 0);
    let maxSlopeDeg = 0;
    for (let i = 1; i < line.length; i++) maxSlopeDeg = Math.max(maxSlopeDeg,
      Math.atan2(Math.abs(elevations[i - 1]! - elevations[i]!), distanceM(line[i - 1]!, line[i]!)) * 180 / Math.PI);
    const verticalM = Math.max(...elevations) - Math.min(...elevations), avgSlopeDeg = Math.atan2(verticalM, lengthM) * 180 / Math.PI;
    const difficulty = maxSlopeDeg >= 37 ? 'black' : maxSlopeDeg >= 24 ? 'blue' : 'green';
    const parts = line.slice(1).map((point, partIndex) => {
      const segment: [number, number][] = [line[partIndex]!, point];
      return { centerline: segment, centerlineElevM: [elevations[partIndex]!, elevations[partIndex + 1]!],
        polygon: trailPolygon(segment, 24) };
    });
    return { id: `benchmark-trail-${index + 1}`, name: `Jackson Benchmark Trail ${index + 1}`,
      parts,
      brushWidthM: 24, areaM2: lengthM * 24, lengthM, verticalM, avgSlopeDeg, maxSlopeDeg,
      difficulty, terrainGraded: false, status: 'complete', createdAt } satisfies SavedTrail;
  });
  const network = buildSkiNetwork(trails, lifts, { includePlanning: false });
  if (network.diagnostics.componentCount !== 1 || network.diagnostics.isolatedLiftIds.length
    || network.diagnostics.orphanTrailIds.length || network.liftEdgeIds.size < 8 || network.trailEdgeIds.size < 25) {
    throw new Error(`Generated Jackson topology is not fully connected: ${JSON.stringify(network.diagnostics)}`);
  }
  const liftEdges = lifts.map(lift => network.edgeById.get(network.liftEdgeIds.get(lift.id)!)!)
    .filter(edge => edge.kind === 'lift');
  const portalNodeId = liftEdges[0]!.from;
  const amenityNodes = [portalNodeId, ...new Set(liftEdges.map(edge => edge.to))].slice(0, 3);
  if (amenityNodes.length < 3) throw new Error('Generated Jackson topology lacks three distinct amenity destinations.');
  const amenities: DualAmenity[] = amenityNodes.map((nodeId, index) => ({ id: `benchmark-amenity-${index + 1}`, nodeId,
    priceCents: 1200 + index * 200, capacityPerHour: 1200, inventory: 100_000, opens: 8, closes: 16,
    label: ['Summit Lodge', 'Ridge Cafe', 'Alpine Hut'][index]!, accessSeconds: 30, serviceSeconds: 90,
    need: index === 1 ? 'thirst' : 'hunger', relief: index === 1 ? { thirst: .8, warmth: .2 } : { hunger: .8, warmth: .3 } }));
  return { lifts, trails, network, amenities,
    portal: { id: 'benchmark-portal', nodeId: portalNodeId, lngLat: base, capacityPerMinute: 500 } };
}

function drain(work: Generator<void, void>): void { while (!work.next().done) { /* deterministic offline preparation */ } }

function saveFor(target: 0 | 1000 | 3000, resort: IntegratedBenchmarkResort, terrain: TerrainRecord,
  checkpoint: DualCheckpoint, weatherRun: SavedWeatherRun): GameSave {
  const key = `integrated-${JACKSON_INTEGRATED_FIXTURE_ID}-${target}`;
  return { schemaVersion: 17, key, name: `Jackson integrated benchmark ${target}`, terrainKey: terrain.key,
    center: [(terrain.bounds!.west + terrain.bounds!.east) / 2, (terrain.bounds!.south + terrain.bounds!.north) / 2],
    zoom: 13.4, bearing: 18, pitch: 55, is3D: true,
    site: { bounds: [[terrain.bounds!.west, terrain.bounds!.south], [terrain.bounds!.east, terrain.bounds!.north]],
      widthKm: 6, heightKm: 6, areaKm2: 36 }, lifts: resort.lifts, trails: resort.trails,
    snow: checkpoint.snow ? encodeSnowGrid({ ...checkpoint.snow, depthM: Float32Array.from(checkpoint.snow.depthM),
      surface: Uint8Array.from(checkpoint.snow.surface) }) : undefined,
    weatherRun, dualClock: checkpoint, createdAt: '2026-01-15T13:00:00.000Z', updatedAt: '2026-01-15T14:00:00.000Z' };
}

export async function prepareJacksonIntegratedSaves(terrain: TerrainRecord, weatherPackage: WeatherDataPackage,
  onProgress: (stage: string) => void = () => undefined): Promise<{
  readonly resort: IntegratedBenchmarkResort; readonly saves: Readonly<Record<0 | 1000 | 3000, GameSave>>;
}> {
  const resort = createJacksonIntegratedResort(terrain);
  onProgress('resort-ready');
  const binding = weatherTerrainBinding(terrain);
  if (weatherPackage.manifest.terrainKey !== terrain.key || weatherPackage.manifest.terrainBinding !== binding) {
    throw new Error('Prepared weather package is not bound to the Jackson terrain.');
  }
  const startAt = weatherInstantForLocal({ year: 2026, month: 1, day: 15, hour: 8, minute: 0, second: 0 },
    weatherPackage.manifest.timezone);
  const year = weatherYearLabel(startAt, weatherPackage.manifest.timezone);
  const identity: PreparedWeatherIdentity = { packageContentHash: weatherPackage.manifest.contentHash, terrainBinding: binding,
    seed: JACKSON_INTEGRATED_SEED, year, timezone: weatherPackage.manifest.timezone,
    generatorVersion: weatherPackage.manifest.generatorVersion,
    configurationVersion: WEATHER_YEAR_CONFIGURATION_VERSION, cacheFormatVersion: 1 };
  const prepared = await buildPreparedWeather(weatherPackage, identity);
  onProgress('weather-ready');
  const config = { ...DEFAULT_DUAL_CONFIG, representativeLimit: 3000 };
  const resortInput: ResortSimulationInput = { revision: resortRevision(resort.network.edges, resort.trails),
    edges: resort.network.edges, trails: resort.trails, portal: resort.portal, dailyDemand: 10_000,
    dailyDemandByWeekday: [10_000, 10_000, 10_000, 10_000, 10_000, 10_000, 10_000],
    ticketPriceCents: 12_500, amenities: resort.amenities };
  const snow = generateSnowBaseline(terrain);
  const emptyEngine = new DualClockEngine({ seed: JACKSON_INTEGRATED_SEED, at: startAt,
    timezone: weatherPackage.manifest.timezone, snow, terrain, weather: prepared.resolvedHours, resort: { ...resortInput, dailyDemand: 0,
      dailyDemandByWeekday: [0, 0, 0, 0, 0, 0, 0] }, config: { ...config, representativeLimit: 1000 } });
  const empty = emptyEngine.checkpoint();
  onProgress('empty-ready');
  const engine = new DualClockEngine({ seed: JACKSON_INTEGRATED_SEED, at: startAt,
    timezone: weatherPackage.manifest.timezone, snow, terrain, weather: prepared.resolvedHours, resort: resortInput, config });
  engine.play();
  onProgress('admissions-started');
  // 09:15 leaves the 4x 150-second qualification window ending at 15:55,
  // while giving ordinary need accumulation enough time to exercise amenities.
  drain(engine.advanceTo(Date.parse(startAt) + 4_500_000));
  engine.pause();
  const admittedBase = engine.checkpoint();
  const statuses = Object.fromEntries([...new Set(admittedBase.guests.map(guest => guest.status))]
    .map(status => [status, admittedBase.guests.filter(guest => guest.status === status).length]));
  const boarded = Object.values(admittedBase.flow.queues).reduce((sum, queue) => sum + queue.boarded, 0);
  onProgress(`admissions-ready:${JSON.stringify({ admitted: admittedBase.flow.admitted,
    activePhysical: admittedBase.cohorts.reduce((sum, cohort) => sum + cohort.count, 0),
    completedRuns: admittedBase.flow.completedRuns, amenityRevenueCents: admittedBase.flow.amenityRevenueCents,
    boarded, statuses })}`);
  const checkpointFor = (target: 1000 | 3000) => {
    const candidate = new DualClockEngine({ seed: JACKSON_INTEGRATED_SEED, at: admittedBase.clock.at,
      timezone: weatherPackage.manifest.timezone, snow: null, terrain,
      weather: prepared.resolvedHours, resort: resortInput, checkpoint: admittedBase });
    return candidate.prepareRepresentativeCheckpoint(target);
  };
  const checkpoints = { 0: empty, 1000: checkpointFor(1000), 3000: checkpointFor(3000) } as const;
  onProgress('checkpoints-ready');
  const weatherRun: SavedWeatherRun = { packageContentHash: weatherPackage.manifest.contentHash, terrainBinding: binding,
    seed: JACKSON_INTEGRATED_SEED, generatorVersion: weatherPackage.manifest.generatorVersion,
    configurationVersion: WEATHER_YEAR_CONFIGURATION_VERSION, localStartAt: prepared.session.plan.startsAt,
    cursorHour: Math.floor((Date.parse(checkpoints[3000].clock.at) - Date.parse(prepared.session.plan.startsAt)) / 3_600_000) };
  return { resort, saves: { 0: saveFor(0, resort, terrain, checkpoints[0], weatherRun),
    1000: saveFor(1000, resort, terrain, checkpoints[1000], weatherRun),
    3000: saveFor(3000, resort, terrain, checkpoints[3000], weatherRun) } };
}
