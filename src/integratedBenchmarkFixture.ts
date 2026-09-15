import { buildSkiNetwork } from './network';
import { validateDualCheckpoint } from './dualClock/validation';
import type { IntegratedBenchmarkScenario } from './integratedBenchmarkScenario';
import { validateIntegratedBenchmarkScenario } from './integratedBenchmarkScenario';
import { validateTerrainPackage } from './terrainPackage';
import { weatherTerrainBinding } from './weather/terrainBinding';
import type { GameSave } from './types/gameSave';
import type { TerrainRecord } from './types/terrain';
import type { WeatherDataPackage } from './weather/weatherModel';
import { isWeatherDataPackage } from './weather/weatherModel';
import { prepareResortGeometry } from './dualClock/resortGeometry';
import { JACKSON_INTEGRATED_FIXTURE_ID, JACKSON_INTEGRATED_REPLAY_ID,
  JACKSON_INTEGRATED_SEED, JACKSON_INTEGRATED_TERRAIN_KEY } from './integratedBenchmarkPreparation';

export interface IntegratedBenchmarkArtifact {
  readonly path: string;
  readonly bytes: number;
  readonly sha256: string;
}

export interface IntegratedBenchmarkFixtureManifest {
  readonly contractVersion: 1;
  readonly fixtureId: string;
  readonly terrainKey: string;
  readonly preparedAt: string;
  readonly preparedFrom: string;
  readonly preparationCodeSha256: string;
  readonly acquisition: { readonly sourceTypeRequired: 'live'; readonly syntheticInputsAllowed: false;
    readonly status: 'acquired'; readonly sources: Readonly<Record<string, unknown>> };
  readonly location: {
    readonly name: string;
    readonly state: string;
    readonly country: string;
    readonly center: readonly [number, number];
    readonly bounds: { readonly west: number; readonly south: number; readonly east: number; readonly north: number };
  };
  readonly scenario: {
    readonly dailyDemand: 10000;
    readonly dailyDemandByWeekday: readonly [number, number, number, number, number, number, number];
    readonly amenities: IntegratedBenchmarkScenario['amenities'];
    readonly snow: { readonly width: 512; readonly height: 512; readonly cellSizeM: number };
    readonly minimums: { readonly lifts: 8; readonly trails: 25; readonly interconnectedTrails: 25; readonly amenities: 3 };
  };
  readonly inventory: {
    readonly lifts: number; readonly trails: number; readonly interconnectedTrails: number; readonly amenities: number;
    readonly nodes: number; readonly paths: number; readonly junctions: number; readonly routes: number;
    readonly lanes: number; readonly segments: number; readonly geometryVertices: number;
    readonly elevationMinM: number; readonly elevationMaxM: number; readonly demSpacingM: number;
    readonly coverBytes: number; readonly imageryBytes: number;
  };
  readonly artifacts: {
    readonly terrainRecord: IntegratedBenchmarkArtifact;
    readonly imagery: IntegratedBenchmarkArtifact;
    readonly weatherPackage: IntegratedBenchmarkArtifact;
    readonly checkpointEmpty: IntegratedBenchmarkArtifact;
    readonly checkpoint1000: IntegratedBenchmarkArtifact;
    readonly checkpoint3000: IntegratedBenchmarkArtifact;
  };
  readonly requiredPresentationAssets: readonly (IntegratedBenchmarkArtifact & {
    readonly role: string; readonly sourceUrl?: string; readonly license?: string;
  })[];
}

export interface LoadedIntegratedBenchmarkFixture {
  readonly manifest: IntegratedBenchmarkFixtureManifest;
  readonly terrain: TerrainRecord;
  readonly weatherPackage: WeatherDataPackage;
  readonly saves: Readonly<Record<0 | 1000 | 3000, GameSave>>;
}

const SHA256 = /^[a-f0-9]{64}$/;
function artifact(value: unknown): value is IntegratedBenchmarkArtifact {
  const item = value as Partial<IntegratedBenchmarkArtifact> | null;
  return !!item && typeof item.path === 'string' && !!item.path && !item.path.includes('..')
    && Number.isSafeInteger(item.bytes) && item.bytes! > 0 && typeof item.sha256 === 'string' && SHA256.test(item.sha256);
}
function presentationArtifact(value: unknown): value is IntegratedBenchmarkFixtureManifest['requiredPresentationAssets'][number] {
  return artifact(value) && typeof (value as { role?: unknown }).role === 'string' && !!(value as { role?: string }).role;
}

export function validateIntegratedBenchmarkFixtureManifest(value: unknown): IntegratedBenchmarkFixtureManifest {
  const manifest = value as Partial<IntegratedBenchmarkFixtureManifest> | null;
  if (!manifest || manifest.contractVersion !== 1 || !manifest.fixtureId || !manifest.terrainKey
    || !manifest.preparedAt || !manifest.preparedFrom || !manifest.preparationCodeSha256 || !manifest.acquisition
    || !manifest.location || !manifest.scenario || !manifest.inventory || !manifest.artifacts
    || !artifact(manifest.artifacts.terrainRecord) || !artifact(manifest.artifacts.weatherPackage)
    || !artifact(manifest.artifacts.checkpointEmpty) || !artifact(manifest.artifacts.checkpoint1000)
    || !artifact(manifest.artifacts.checkpoint3000) || !artifact(manifest.artifacts.imagery)
    || !Array.isArray(manifest.requiredPresentationAssets) || manifest.requiredPresentationAssets.length < 3
    || manifest.requiredPresentationAssets.some(item => !presentationArtifact(item))) {
    throw new Error('Integrated benchmark fixture manifest is incomplete or invalid.');
  }
  if (manifest.fixtureId !== JACKSON_INTEGRATED_FIXTURE_ID || manifest.terrainKey !== JACKSON_INTEGRATED_TERRAIN_KEY
    || manifest.acquisition.sourceTypeRequired !== 'live' || manifest.acquisition.syntheticInputsAllowed !== false
    || manifest.acquisition.status !== 'acquired' || !manifest.acquisition.sources) {
    throw new Error('Integrated benchmark terrain identity is not Jackson, New Hampshire.');
  }
  const scenario = manifest.scenario;
  if (scenario.dailyDemand !== 10_000 || scenario.dailyDemandByWeekday?.length !== 7
    || scenario.dailyDemandByWeekday.some(demand => demand !== 10_000)
    || scenario.snow?.width !== 512 || scenario.snow.height !== 512
    || !Number.isFinite(scenario.snow.cellSizeM) || scenario.snow.cellSizeM <= 0
    || scenario.minimums?.lifts !== 8 || scenario.minimums.trails !== 25
    || scenario.minimums.interconnectedTrails !== 25 || scenario.minimums.amenities !== 3) {
    throw new Error('Integrated benchmark scenario requirements do not match the frozen contract.');
  }
  validateIntegratedBenchmarkScenario({ version: 1, tier: 'qualification', workload: 'detailed', fixtureId: manifest.fixtureId, runId: 'manifest-validation',
    seed: 'manifest-validation', replayId: 'manifest-validation',
    saveKey: 'manifest-validation', terrainKey: manifest.terrainKey, checkpointTarget: 1000,
    requestedSpeed: 1,
    dailyDemand: scenario.dailyDemand, dailyDemandByWeekday: scenario.dailyDemandByWeekday,
    amenities: scenario.amenities });
  return manifest as IntegratedBenchmarkFixtureManifest;
}

function reachableNodes(save: GameSave): Set<string> {
  const network = buildSkiNetwork(save.trails, save.lifts, {
    nodes: save.nodes ?? [], paths: save.paths ?? [], junctions: save.junctions ?? [],
  });
  const start = save.dualClock?.portal?.nodeId;
  const reached = new Set<string>(start ? [start] : []), queue = start ? [start] : [];
  while (queue.length) {
    const node = queue.shift()!;
    for (const edge of network.edges) {
      if (edge.from !== node || reached.has(edge.to)) continue;
      reached.add(edge.to); queue.push(edge.to);
    }
  }
  return reached;
}

export function validateLoadedIntegratedBenchmarkFixture(fixture: LoadedIntegratedBenchmarkFixture): void {
  const manifest = validateIntegratedBenchmarkFixtureManifest(fixture.manifest);
  if (fixture.terrain.key !== manifest.terrainKey) throw new Error('Terrain record key does not match fixture manifest.');
  if (!fixture.terrain.localImagery || fixture.terrain.localImagery.length !== manifest.artifacts.imagery.bytes) {
    throw new Error('Required local Jackson imagery is absent or has the wrong byte length.');
  }
  const terrainValidation = validateTerrainPackage(fixture.terrain);
  if (!terrainValidation.ok) throw new Error(`Integrated terrain package is invalid: ${terrainValidation.errors.join(' ')}`);
  const heights = fixture.terrain.sampleHeights;
  let elevationMin = Number.POSITIVE_INFINITY, elevationMax = Number.NEGATIVE_INFINITY, finiteHeights = true;
  for (const height of heights) {
    if (!Number.isFinite(height)) finiteHeights = false;
    elevationMin = Math.min(elevationMin, height); elevationMax = Math.max(elevationMax, height);
  }
  if (fixture.terrain.sampleGridSize !== 2000 || heights.length !== fixture.terrain.sampleGridSize ** 2
    || !finiteHeights || elevationMax <= elevationMin) {
    throw new Error('Integrated terrain DEM is missing, malformed, or flat.');
  }
  if (!isWeatherDataPackage(fixture.weatherPackage) || fixture.weatherPackage.manifest.terrainKey !== manifest.terrainKey
    || fixture.weatherPackage.manifest.terrainBinding !== weatherTerrainBinding(fixture.terrain)) {
    throw new Error('Integrated prepared weather package is invalid or bound to another terrain.');
  }
  const referenceInfrastructure = JSON.stringify({ lifts: fixture.saves[3000]?.lifts,
    trails: fixture.saves[3000]?.trails, nodes: fixture.saves[3000]?.nodes ?? [],
    paths: fixture.saves[3000]?.paths ?? [], junctions: fixture.saves[3000]?.junctions ?? [] });
  for (const target of [0, 1000, 3000] as const) {
    const save = fixture.saves[target];
    if (!save || save.schemaVersion !== 17 || save.terrainKey !== manifest.terrainKey || !save.dualClock) {
      throw new Error(`Integrated ${target} checkpoint save identity is invalid.`);
    }
    validateDualCheckpoint(save.dualClock);
    if (JSON.stringify({ lifts: save.lifts, trails: save.trails, nodes: save.nodes ?? [],
      paths: save.paths ?? [], junctions: save.junctions ?? [] }) !== referenceInfrastructure) {
      throw new Error(`Integrated ${target} save infrastructure differs from the qualification resort.`);
    }
    if (save.dualClock.seed !== JACKSON_INTEGRATED_SEED) {
      throw new Error(`Integrated ${target} checkpoint seed is not bound to the fixture.`);
    }
    const activeRepresentatives = save.dualClock.guests.filter(guest => guest.status !== 'departed').length;
    const expectedLimit = target === 0 ? 1000 : target;
    if (activeRepresentatives !== target || save.dualClock.config.representativeLimit !== expectedLimit
      || save.dualClock.snow?.width !== 512 || save.dualClock.snow.height !== 512) {
      throw new Error(`Integrated ${target} checkpoint does not contain the exact workload.`);
    }
    const network = buildSkiNetwork(save.trails, save.lifts, { nodes: save.nodes ?? [], paths: save.paths ?? [],
      junctions: save.junctions ?? [], includePlanning: false });
    const reached = reachableNodes(save);
    const reachableTrailIds = new Set<string>(), usableLiftIds = new Set<string>();
    for (const edge of network.edges) if (edge.kind === 'trail' && edge.open
      && reached.has(edge.from) && reached.has(edge.to)) reachableTrailIds.add(edge.trailId);
    for (const edge of network.edges) if (edge.kind === 'lift' && edge.open
      && reached.has(edge.from) && reached.has(edge.to)
      && edge.servesTrailIds.some(id => reachableTrailIds.has(id))) usableLiftIds.add(edge.liftId);
    if (network.diagnostics.componentCount !== 1 || usableLiftIds.size < 8 || reachableTrailIds.size < 25) {
      throw new Error('Integrated resort infrastructure is below the minimum workload.');
    }
    const distinctTrailGeometry = new Set(save.trails.map(trail => JSON.stringify(
      trail.parts.map(part => part.centerline))));
    if (distinctTrailGeometry.size < 25) throw new Error('Integrated resort trails are not physically distinct.');
    const reachableAmenities = new Set(manifest.scenario.amenities.filter(amenity => reached.has(amenity.nodeId)).map(amenity => amenity.nodeId));
    if (reachableAmenities.size < 3) throw new Error('Integrated resort has fewer than three reachable amenity destinations.');
    if (target > 0) {
      const queueValues = Object.values(save.dualClock.flow.queues);
      const statusCount = new Set(save.dualClock.guests.filter(guest => guest.status !== 'departed')
        .map(guest => guest.status)).size;
      if (save.dualClock.flow.completedRuns <= 0 || save.dualClock.flow.amenityRevenueCents <= 0
        || Object.values(save.dualClock.flow.trails).reduce((sum, trail) => sum + trail.passages, 0) <= 0
        || queueValues.reduce((sum, queue) => sum + queue.boarded, 0) <= 0
        || queueValues.filter(queue => queue.serviceAvailable === true).length < 8 || statusCount < 2) {
        throw new Error(`Integrated ${target} checkpoint has not exercised the physical ski and amenity workload.`);
      }
    }
    if (target !== 3000) continue;
    const snow = { bounds: save.dualClock.snow!.bounds, width: save.dualClock.snow!.width,
      height: save.dualClock.snow!.height, depthM: Float32Array.from(save.dualClock.snow!.depthM),
      surface: Uint8Array.from(save.dualClock.snow!.surface) };
    const resort = { revision: save.dualClock.resortRevision, edges: network.edges, trails: save.trails,
      portal: save.dualClock.portal, dailyDemand: manifest.scenario.dailyDemand,
      dailyDemandByWeekday: manifest.scenario.dailyDemandByWeekday, ticketPriceCents: save.dualClock.nextTicketPriceCents,
      amenities: manifest.scenario.amenities };
    const geometry = prepareResortGeometry(resort, snow, save.dualClock.transitRoutes);
    const actual = { nodes: network.nodes.length, paths: save.paths?.length ?? 0, junctions: save.junctions?.length ?? 0,
      routes: geometry.routes.size, lanes: [...geometry.routes.values()].reduce((sum, route) => sum + route.lanes.length, 0),
      segments: network.edges.filter(edge => edge.kind === 'trail').length,
      geometryVertices: [...geometry.routes.values()].reduce((sum, route) => sum + route.lanes.reduce((n, lane) => n + lane.length, 0), 0) };
    for (const [key, value] of Object.entries(actual)) if (manifest.inventory[key as keyof typeof actual] !== value) {
      throw new Error(`Integrated manifest inventory ${key} does not match the prepared resort.`);
    }
  }
  if (manifest.inventory.lifts !== 8 || manifest.inventory.trails !== 25
    || manifest.inventory.interconnectedTrails < 25 || manifest.inventory.amenities !== 3) {
    throw new Error('Integrated manifest inventory does not describe the frozen workload.');
  }
  const bounds = fixture.terrain.bounds!;
  const midLat = (bounds.south + bounds.north) * Math.PI / 360;
  const widthM = (bounds.east - bounds.west) * 111_320 * Math.cos(midLat), heightM = (bounds.north - bounds.south) * 111_320;
  const cellSizeM = Math.max(widthM, heightM) / 511;
  const demSpacingM = Math.max(widthM, heightM) / (fixture.terrain.sampleGridSize - 1);
  if (Math.abs(manifest.scenario.snow.cellSizeM - cellSizeM) > .05
    || Math.abs(manifest.inventory.demSpacingM - demSpacingM) > .05
    || Math.abs(manifest.inventory.elevationMinM - elevationMin) > .01
    || Math.abs(manifest.inventory.elevationMaxM - elevationMax) > .01
    || manifest.inventory.coverBytes !== fixture.terrain.coverGrid?.data.length
    || manifest.inventory.imageryBytes !== fixture.terrain.localImagery.length) {
    throw new Error('Integrated manifest terrain inventory does not match the parsed acquired assets.');
  }
}

export function scenarioForIntegratedFixture(
  fixture: LoadedIntegratedBenchmarkFixture,
  target: 0 | 1000 | 3000,
  runId: string,
  requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64 = 1,
): IntegratedBenchmarkScenario {
  const save = fixture.saves[target];
  return validateIntegratedBenchmarkScenario({ version: 1, tier: 'qualification',
    workload: target === 0 ? 'empty' : requestedSpeed >= 8 ? 'aggregate' : 'detailed', fixtureId: fixture.manifest.fixtureId, runId,
    seed: save.dualClock!.seed, replayId: JACKSON_INTEGRATED_REPLAY_ID,
    saveKey: save.key, terrainKey: fixture.terrain.key, checkpointTarget: target, requestedSpeed,
    dailyDemand: target === 0 ? 0 : fixture.manifest.scenario.dailyDemand,
    dailyDemandByWeekday: target === 0 ? [0, 0, 0, 0, 0, 0, 0] : fixture.manifest.scenario.dailyDemandByWeekday,
    amenities: fixture.manifest.scenario.amenities });
}
