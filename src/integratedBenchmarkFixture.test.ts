import { beforeAll, describe, expect, it, vi } from 'vitest';

vi.mock('./dualClock/resortGeometry', async importOriginal => {
  const actual = await importOriginal<typeof import('./dualClock/resortGeometry')>();
  return { ...actual, prepareResortGeometry: (...args: Parameters<typeof actual.prepareResortGeometry>) =>
    (args[1]?.width ?? 0) < 512 ? actual.prepareResortGeometry(...args) : ({ routes: new Map([['test-route', {
      lanes: [Array.from({ length: 438 }, (_, index) => [index, index] as [number, number])],
    }]]) } as unknown as ReturnType<typeof actual.prepareResortGeometry>) };
});
import { DualClockEngine } from './dualClock/engine';
import { dualFixture } from './dualClock/fixtures';
import { prepareResortGeometry } from './dualClock/resortGeometry';
import { createJacksonIntegratedResort, JACKSON_INTEGRATED_FIXTURE_ID, JACKSON_INTEGRATED_SEED,
  JACKSON_INTEGRATED_TERRAIN_KEY } from './integratedBenchmarkPreparation';
import { validateLoadedIntegratedBenchmarkFixture, type IntegratedBenchmarkFixtureManifest,
  type LoadedIntegratedBenchmarkFixture } from './integratedBenchmarkFixture';
import { checksumBytes, float32Bytes, imageryMetadataOf } from './terrainPackage';
import type { GameSave } from './types/gameSave';
import type { TerrainRecord } from './types/terrain';
import type { WeatherDataPackage } from './weather/weatherModel';
import { weatherTerrainBinding } from './weather/terrainBinding';

const bounds = { west: -71.20255, south: 44.13905, east: -71.12745, north: 44.19295 };
const createdAt = '2026-01-15T14:15:00.000Z';

function terrainFixture(): TerrainRecord {
  const heights = new Float32Array(2000 * 2000);
  for (let y = 0; y < 2000; y += 1) heights.fill(2000 - y / 2, y * 2000, (y + 1) * 2000);
  const heightBytes = float32Bytes(heights);
  const coverGrid = { bounds, width: 2, height: 2, cellSizeM: 3000, data: [10, 10, 20, 30],
    complete: true, nodataCount: 0, source: 'esa-worldcover-2021-v200' as const, vintage: '2021' as const };
  const coverMetadata = { bounds, width: 2, height: 2, cellSizeM: 3000, complete: true, nodataCount: 0,
    source: 'esa-worldcover-2021-v200' as const, vintage: '2021' as const, byteLength: 4,
    checksum: checksumBytes(coverGrid.data) };
  const coverBoundarySegments = [0, 0, 1, 0, 10], contourSegments = [0, 0, 1, 1, 1000];
  const coverGeometryMetadata = { segmentCount: 1, byteLength: 20,
    checksum: checksumBytes(float32Bytes(coverBoundarySegments)) };
  const contourMetadata = { intervalM: 10, segmentCount: 1, gridSize: 2000, byteLength: 20,
    checksum: checksumBytes(float32Bytes(contourSegments)) };
  const coverDisplayGeometry = [10, 1, 4, 0, 0, 1, 0, 1, 1, 0, 0];
  const coverDisplayMetadata = { polygonCount: 1, ringCount: 1, vertexCount: 4, smoothingM: 24,
    simplifyM: 10, minFeatureM2: 600, byteLength: 44,
    checksum: checksumBytes(float32Bytes(coverDisplayGeometry)) };
  const imagery = [1, 2, 3];
  const imageryBase = { bounds, width: 1, height: 1, mimeType: 'image/jpeg' as const, acquisitionYear: 2023,
    sceneIds: [1], attribution: 'USDA/USGS NAIP public domain' };
  return { schemaVersion: 5, key: JACKSON_INTEGRATED_TERRAIN_KEY, mountainName: 'Jackson test fixture',
    latitude: 44.166, longitude: -71.165, areaSizeMeters: 6000, bounds, sampleGridSize: 2000,
    sampleHeights: heights as unknown as number[], coverGrid, coverMetadata, coverBoundarySegments,
    coverGeometryMetadata, coverDisplayGeometry, coverDisplayMetadata, contourSegments, contourMetadata,
    localImagery: imagery, localImageryMetadata: imageryMetadataOf(imagery, imageryBase), climate: { monthly: [] },
    sourceType: 'live', createdAt, updatedAt: createdAt,
    packageManifest: { schemaVersion: 2, terrainKey: JACKSON_INTEGRATED_TERRAIN_KEY, complete: true,
      elevationByteLength: heightBytes.byteLength, elevationChecksum: checksumBytes(heightBytes), cover: coverMetadata,
      coverGeometry: coverGeometryMetadata, coverDisplay: coverDisplayMetadata, contours: contourMetadata,
      assets: { elevation: 'height.bin', cover: 'cover.bin', coverGeometry: 'geometry.bin',
        coverDisplay: 'display.bin', contours: 'contours.bin' }, preparedAt: createdAt } };
}

function checkpoint(target: 0 | 1000 | 3000, resort: ReturnType<typeof createJacksonIntegratedResort>) {
  const value = new DualClockEngine(dualFixture(0, 2)).checkpoint();
  const guests = Array.from({ length: target }, (_, index) => ({ id: `guest-${index}`, ordinal: index,
    groupId: 'group', status: index % 2 ? 'resting' as const : 'lift-ride' as const, nextPlan: 'ski', thought: 'ski',
    satisfaction: .8, runs: 1, spendingCents: 0, trackingBeganAt: createdAt, history: [],
    needs: { hunger: .2, thirst: .2, warmth: .2, restroom: .2, fatigue: .2 }, needsSecond: 0,
    personalBudgetCents: 5000, edgeId: null, route: [], routeIndex: 0, nodeId: resort.portal.nodeId,
    started: 0, due: 1, admissionDay: '2026-01-15', ability: .5 }));
  const queues = Object.fromEntries(resort.lifts.map(lift => [lift.id,
    { guests: 1, waitSeconds: 1, boarded: 1, riders: 1, serviceAvailable: true }]));
  const trails = Object.fromEntries(resort.trails.map(trail => [trail.id, { passages: 1, guests: 1 }]));
  return { ...value, seed: JACKSON_INTEGRATED_SEED, config: { ...value.config,
    representativeLimit: target === 0 ? 1000 : target }, resortRevision: 1, portal: resort.portal,
    snow: { bounds, width: 512, height: 512, depthM: Array(512 * 512).fill(1),
      surface: Array(512 * 512).fill(1), exposure: Array(512 * 512).fill(0) },
    flow: { admitted: target, active: target, departed: 0, turnedAway: 0, ticketRevenueCents: target * 100,
      amenityRevenueCents: target ? 100 : 0, completedRuns: target ? 1 : 0, queues, trails },
    cohorts: target ? [{ id: 1, admissionId: 'admission', count: target, nodeId: resort.portal.nodeId, edgeId: null,
      due: 1, started: 0, status: 'travel' as const, leaveAt: 10, ability: .5, runs: 1, budgetCents: 5000 }] : [],
    guests, nextGuestId: target + 1 };
}

function buildFixture(): LoadedIntegratedBenchmarkFixture {
  const terrain = terrainFixture(), resort = createJacksonIntegratedResort(terrain);
  const saves = Object.fromEntries(([0, 1000, 3000] as const).map(target => {
    const dualClock = checkpoint(target, resort);
    return [target, { schemaVersion: 17, key: `save-${target}`, name: `Save ${target}`,
      terrainKey: terrain.key, center: [terrain.longitude, terrain.latitude], zoom: 13, bearing: 0, pitch: 45,
      is3D: true, site: { bounds: [[bounds.west, bounds.south], [bounds.east, bounds.north]], widthKm: 6,
        heightKm: 6, areaKm2: 36 }, lifts: resort.lifts, trails: resort.trails,
      dualClock, createdAt, updatedAt: createdAt } satisfies GameSave];
  })) as unknown as Record<0 | 1000 | 3000, GameSave>;
  const network = resort.network, geometry = prepareResortGeometry({ revision: 1, edges: network.edges,
    trails: resort.trails, portal: resort.portal, dailyDemand: 10_000,
    dailyDemandByWeekday: [10_000, 10_000, 10_000, 10_000, 10_000, 10_000, 10_000],
    ticketPriceCents: 12_500, amenities: resort.amenities }, {
    ...saves[3000].dualClock!.snow!,
    depthM: Float32Array.from(saves[3000].dualClock!.snow!.depthM),
    surface: Uint8Array.from(saves[3000].dualClock!.snow!.surface),
  }, []);
  const midLat = (bounds.south + bounds.north) * Math.PI / 360;
  const widthM = (bounds.east - bounds.west) * 111_320 * Math.cos(midLat);
  const heightM = (bounds.north - bounds.south) * 111_320;
  const artifact = (path: string, bytes = 1) => ({ path, bytes, sha256: 'a'.repeat(64) });
  const manifest: IntegratedBenchmarkFixtureManifest = { contractVersion: 1, fixtureId: JACKSON_INTEGRATED_FIXTURE_ID,
    terrainKey: JACKSON_INTEGRATED_TERRAIN_KEY, preparedAt: createdAt, preparedFrom: 'test',
    preparationCodeSha256: 'b'.repeat(64), acquisition: { sourceTypeRequired: 'live', syntheticInputsAllowed: false,
      status: 'acquired', sources: { elevation: 'USGS' } }, location: { name: 'Jackson', state: 'New Hampshire',
      country: 'USA', center: [terrain.longitude, terrain.latitude], bounds }, scenario: { dailyDemand: 10_000,
      dailyDemandByWeekday: [10_000, 10_000, 10_000, 10_000, 10_000, 10_000, 10_000], amenities: resort.amenities,
      snow: { width: 512, height: 512, cellSizeM: Math.max(widthM, heightM) / 511 },
      minimums: { lifts: 8, trails: 25, interconnectedTrails: 25, amenities: 3 } },
    inventory: { lifts: 8, trails: 25, interconnectedTrails: 25, amenities: 3, nodes: network.nodes.length,
      paths: 0, junctions: 0, routes: geometry.routes.size,
      lanes: [...geometry.routes.values()].reduce((sum, route) => sum + route.lanes.length, 0),
      segments: network.edges.filter(edge => edge.kind === 'trail').length,
      geometryVertices: [...geometry.routes.values()].reduce((sum, route) => sum +
        route.lanes.reduce((count, lane) => count + lane.length, 0), 0), elevationMinM: 1000.5,
      elevationMaxM: 2000, demSpacingM: Math.max(widthM, heightM) / 1999,
      coverBytes: terrain.coverGrid!.data.length, imageryBytes: terrain.localImagery!.length },
    artifacts: { terrainRecord: artifact('terrain.json'), imagery: artifact('imagery.jpg', 3),
      weatherPackage: artifact('weather.json'), checkpointEmpty: artifact('save-0.json'),
      checkpoint1000: artifact('save-1000.json'), checkpoint3000: artifact('save-3000.json') },
    requiredPresentationAssets: [artifact('0.pbf'), artifact('8192.pbf'), artifact('OFL.txt')]
      .map((item, index) => ({ ...item, role: `asset-${index}` })) };
  const weatherPackage: WeatherDataPackage = { manifest: { schemaVersion: 1, terrainKey: terrain.key,
    terrainBinding: weatherTerrainBinding(terrain), timezone: 'America/New_York', historicalStartYear: 2026,
    historicalEndYear: 2026, quality: 'estimated', sourceSummary: 'test', sourceVersion: 'test', generatorVersion: 1,
    contentHash: 'test', complete: true, createdAt }, historicalYears: [] };
  return { manifest, terrain, weatherPackage, saves };
}

function withSaves(base: LoadedIntegratedBenchmarkFixture, mutate: (save: GameSave) => GameSave) {
  return { ...base, saves: Object.fromEntries(([0, 1000, 3000] as const).map(target =>
    [target, mutate(base.saves[target])])) as unknown as LoadedIntegratedBenchmarkFixture['saves'] };
}

function withHeights(terrain: TerrainRecord, sampleGridSize: number, sampleHeights: number[] | Float32Array): TerrainRecord {
  const bytes = float32Bytes(sampleHeights);
  return { ...terrain, sampleGridSize, sampleHeights: sampleHeights as unknown as number[],
    packageManifest: { ...terrain.packageManifest!, elevationByteLength: bytes.byteLength,
      elevationChecksum: checksumBytes(bytes) } };
}

describe('integrated benchmark loaded-fixture rejection controls', () => {
  let valid: LoadedIntegratedBenchmarkFixture;
  beforeAll(() => { valid = buildFixture(); });

  it('accepts the compact canonical validator fixture', () => expect(() => validateLoadedIntegratedBenchmarkFixture(valid)).not.toThrow());
  it('rejects missing weather and required asset declarations', () => {
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid, weatherPackage: null as unknown as WeatherDataPackage })).toThrow(/weather/);
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid, manifest: { ...valid.manifest,
      artifacts: { ...valid.manifest.artifacts, weatherPackage: undefined } as unknown as typeof valid.manifest.artifacts } })).toThrow(/manifest/);
  });
  it('rejects missing imagery', () => expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid,
    terrain: { ...valid.terrain, localImagery: undefined } })).toThrow(/imagery/));
  it('rejects missing, flat, and wrong-size DEMs', () => {
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid,
      terrain: withHeights(valid.terrain, 2000, []) })).toThrow(/DEM/);
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid,
      terrain: withHeights(valid.terrain, 2000, new Float32Array(2000 * 2000).fill(1000)) })).toThrow(/flat/);
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid,
      terrain: withHeights(valid.terrain, 1024, new Float32Array(1024 * 1024)) })).toThrow(/2000|DEM/);
  });
  it('rejects an insufficient representative checkpoint', () => {
    const save = valid.saves[1000];
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid, saves: { ...valid.saves,
      1000: { ...save, dualClock: { ...save.dualClock!, guests: save.dualClock!.guests.slice(1) } } } })).toThrow(/exact workload/);
  });
  it('rejects disconnected or insufficient usable lift topology', () => {
    expect(() => validateLoadedIntegratedBenchmarkFixture(withSaves(valid, save => ({ ...save,
      lifts: save.lifts.slice(0, 7) })))).toThrow(/minimum workload/);
    const isolated = { ...valid.saves[3000].lifts[0]!, id: 'isolated',
      points: [[-72, 45], [-72, 45.01]] as [[number, number], [number, number]] };
    expect(() => validateLoadedIntegratedBenchmarkFixture(withSaves(valid, save => ({ ...save,
      lifts: [...save.lifts, isolated] })))).toThrow(/minimum workload/);
  });
  it('rejects duplicate trail geometry and unreachable amenities', () => {
    expect(() => validateLoadedIntegratedBenchmarkFixture(withSaves(valid, save => ({ ...save, trails: save.trails.map((trail, index) =>
      index === 24 ? { ...trail, parts: save.trails[0]!.parts } : trail) })))).toThrow(/physically distinct|minimum workload/);
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid, manifest: { ...valid.manifest,
      scenario: { ...valid.manifest.scenario, amenities: valid.manifest.scenario.amenities.map((amenity, index) =>
        index === 2 ? { ...amenity, nodeId: 'unreachable-node' } : amenity) } } })).toThrow(/reachable amenity/);
  });
  it('rejects checkpoints without exercised queues, trails, amenities, runs, and status diversity', () => {
    const save = valid.saves[1000], flow = save.dualClock!.flow;
    const guests = save.dualClock!.guests.map(guest => ({ ...guest, status: 'resting' as const }));
    expect(() => validateLoadedIntegratedBenchmarkFixture({ ...valid, saves: { ...valid.saves, 1000: { ...save,
      dualClock: { ...save.dualClock!, guests, flow: { ...flow, completedRuns: 0, amenityRevenueCents: 0,
        queues: Object.fromEntries(Object.entries(flow.queues).map(([key, queue]) => [key, { ...queue, boarded: 0 }])),
        trails: Object.fromEntries(Object.entries(flow.trails).map(([key, trail]) => [key, { ...trail, passages: 0 }])) } } } } })).toThrow(/physical ski/);
  });
});
