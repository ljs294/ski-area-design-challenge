import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

import { validateLoadedIntegratedBenchmarkFixture, type IntegratedBenchmarkArtifact,
  type IntegratedBenchmarkFixtureManifest } from '../src/integratedBenchmarkFixture';
import { JACKSON_INTEGRATED_FIXTURE_ID, JACKSON_INTEGRATED_TERRAIN_KEY,
  prepareJacksonIntegratedSaves } from '../src/integratedBenchmarkPreparation';
import { prepareResortGeometry } from '../src/dualClock/resortGeometry';
import type { TerrainRecord } from '../src/types/terrain';
import type { WeatherDataPackage } from '../src/weather/weatherModel';

const rootArgument = process.argv.slice(2).find(argument => !argument.startsWith('--'));
const writeLock = process.argv.includes('--write-lock');
const root = path.resolve(rootArgument ?? 'test-results/integrated-fixtures/jackson');
const sourceManifest = JSON.parse(readFileSync(path.join(root, 'fixture-manifest.json'), 'utf8')) as Record<string, unknown>;
const sourceArtifacts = sourceManifest.artifacts as Record<string, { path: string }>;

function resolveSource(descriptor: { path: string }): string {
  const direct = path.resolve(descriptor.path);
  return direct.startsWith(root + path.sep) ? direct : path.resolve(root, descriptor.path);
}
function readJson<T>(file: string): T { return JSON.parse(readFileSync(file, 'utf8')) as T; }
function digest(bytes: Uint8Array | string): string { return createHash('sha256').update(bytes).digest('hex'); }
function descriptor(relativePath: string): IntegratedBenchmarkArtifact {
  const bytes = readFileSync(path.join(root, relativePath));
  return { path: relativePath.replaceAll('\\', '/'), bytes: bytes.byteLength, sha256: digest(bytes) };
}

const terrainFile = resolveSource(sourceArtifacts.terrainRecord!);
const imageryFile = resolveSource(sourceArtifacts.imagery!);
const weatherFile = resolveSource(sourceArtifacts.weatherPackage!);
const terrain = readJson<TerrainRecord>(terrainFile);
terrain.localImagery = Array.from(readFileSync(imageryFile));
const weatherPackage = readJson<WeatherDataPackage>(weatherFile);
const startedAt = performance.now();
const prepared = await prepareJacksonIntegratedSaves(terrain, weatherPackage, stage => {
  console.log(JSON.stringify({ stage, elapsedMs: Math.round(performance.now() - startedAt) }));
});
for (const target of [0, 1000, 3000] as const) {
  writeFileSync(path.join(root, `save-${target}.json`), JSON.stringify(prepared.saves[target]));
}

const checkpoint = prepared.saves[3000].dualClock!;
const snow = { bounds: checkpoint.snow!.bounds, width: checkpoint.snow!.width, height: checkpoint.snow!.height,
  depthM: Float32Array.from(checkpoint.snow!.depthM), surface: Uint8Array.from(checkpoint.snow!.surface) };
const resortInput = { revision: checkpoint.resortRevision, edges: prepared.resort.network.edges,
  trails: prepared.resort.trails, portal: prepared.resort.portal, dailyDemand: 10_000,
  dailyDemandByWeekday: [10_000, 10_000, 10_000, 10_000, 10_000, 10_000, 10_000], ticketPriceCents: 12_500,
  amenities: prepared.resort.amenities } as const;
const geometry = prepareResortGeometry(resortInput, snow, checkpoint.transitRoutes);
let elevationMinM = Number.POSITIVE_INFINITY, elevationMaxM = Number.NEGATIVE_INFINITY;
for (const value of terrain.sampleHeights) { elevationMinM = Math.min(elevationMinM, value); elevationMaxM = Math.max(elevationMaxM, value); }
const bounds = terrain.bounds!, midLat = (bounds.south + bounds.north) * Math.PI / 360;
const widthM = (bounds.east - bounds.west) * 111_320 * Math.cos(midLat), heightM = (bounds.north - bounds.south) * 111_320;
const requiredPresentationAssets = (sourceManifest.requiredPresentationAssets as Array<Record<string, unknown>>).map(source => {
  const original = String(source.path);
  const marker = `${path.sep}fonts${path.sep}`;
  const normalized = path.resolve(original), index = normalized.lastIndexOf(marker);
  const relative = index >= 0 ? normalized.slice(index + 1) : path.join('fonts', path.basename(original));
  return { ...descriptor(relative), role: String(source.role), sourceUrl: String(source.sourceUrl ?? ''),
    license: String(source.license ?? '') };
});
const preparationSources = ['src/integratedBenchmarkPreparation.ts', 'src/integratedBenchmarkFixture.ts',
  'src/dualClock/engine.ts', 'scripts/prepareIntegratedBenchmarkFixture.ts'];
const preparationCodeSha256 = digest(preparationSources.map(file => readFileSync(path.resolve(file))).reduce<Buffer>(
  (combined, value) => Buffer.concat([combined, Buffer.from(value)]), Buffer.alloc(0)));
const acquisition = sourceManifest.acquisition as IntegratedBenchmarkFixtureManifest['acquisition'];
const manifest: IntegratedBenchmarkFixtureManifest = {
  contractVersion: 1, fixtureId: JACKSON_INTEGRATED_FIXTURE_ID, terrainKey: JACKSON_INTEGRATED_TERRAIN_KEY,
  preparedAt: new Date().toISOString(), preparedFrom: 'scripts/acquireIntegratedTerrain.mjs', preparationCodeSha256,
  acquisition,
  location: { name: 'Jackson / Black Mountain, White Mountains', state: 'New Hampshire', country: 'USA',
    center: [(bounds.west + bounds.east) / 2, (bounds.south + bounds.north) / 2], bounds },
  scenario: { dailyDemand: 10_000, dailyDemandByWeekday: [10_000, 10_000, 10_000, 10_000, 10_000, 10_000, 10_000],
    amenities: prepared.resort.amenities, snow: { width: 512, height: 512, cellSizeM: Math.max(widthM, heightM) / 511 },
    minimums: { lifts: 8, trails: 25, interconnectedTrails: 25, amenities: 3 } },
  inventory: { lifts: prepared.resort.lifts.length, trails: prepared.resort.trails.length,
    interconnectedTrails: prepared.resort.network.trailEdgeIds.size, amenities: prepared.resort.amenities.length,
    nodes: prepared.resort.network.nodes.length, paths: 0, junctions: 0, routes: geometry.routes.size,
    lanes: [...geometry.routes.values()].reduce((sum, route) => sum + route.lanes.length, 0),
    segments: prepared.resort.network.edges.filter(edge => edge.kind === 'trail').length,
    geometryVertices: [...geometry.routes.values()].reduce((sum, route) => sum + route.lanes.reduce((n, lane) => n + lane.length, 0), 0),
    elevationMinM, elevationMaxM, demSpacingM: Math.max(widthM, heightM) / 1999,
    coverBytes: terrain.coverGrid?.data.length ?? 0, imageryBytes: terrain.localImagery.length },
  artifacts: { terrainRecord: descriptor(path.relative(root, terrainFile)), imagery: descriptor(path.relative(root, imageryFile)),
    weatherPackage: descriptor(path.relative(root, weatherFile)), checkpointEmpty: descriptor('save-0.json'),
    checkpoint1000: descriptor('save-1000.json'), checkpoint3000: descriptor('save-3000.json') },
  requiredPresentationAssets,
};
validateLoadedIntegratedBenchmarkFixture({ manifest, terrain, weatherPackage, saves: prepared.saves });
const manifestText = `${JSON.stringify(manifest, null, 2)}\n`;
writeFileSync(path.join(root, 'fixture-manifest.json'), manifestText);
if (writeLock) {
  const weatherSources = acquisition.weather.sources;
  const lock = { contractVersion: 1, fixtureId: manifest.fixtureId, terrainKey: manifest.terrainKey,
    manifestSha256: digest(manifestText), preparation: { entrypoint: manifest.preparedFrom,
      codeSha256: manifest.preparationCodeSha256, syntheticInputsAllowed: acquisition.syntheticInputsAllowed },
    location: { name: manifest.location.name, bounds: [bounds.west, bounds.south, bounds.east, bounds.north] },
    sources: { elevation: { id: acquisition.sources.elevation, license: 'US public domain' },
      imagery: { id: `USDA/USGS NAIP scene IDs ${terrain.imagery?.sceneIds.join(',')}`, license: 'US public domain' },
      cover: { id: acquisition.sources.worldCover.id, license: acquisition.sources.worldCover.license },
      weatherDaily: { id: `${weatherSources[0]!.provider} ${weatherSources[0]!.version} ${weatherSources[0]!.sourceId}`,
        license: 'NASA Earth data policy' },
      weatherHourly: { id: `NASA POWER hourly v2 ${weatherSources[1]!.sourceId}`, license: 'NASA open data policy' } },
    artifacts: manifest.artifacts,
    presentationAssets: manifest.requiredPresentationAssets.map(({ role, path: assetPath, bytes, sha256, license }) =>
      ({ role, path: assetPath, bytes, sha256, license })),
    inventory: { lifts: manifest.inventory.lifts, trails: manifest.inventory.trails,
      interconnectedTrails: manifest.inventory.interconnectedTrails, amenities: manifest.inventory.amenities,
      nodes: manifest.inventory.nodes, routes: manifest.inventory.routes, lanes: manifest.inventory.lanes,
      segments: manifest.inventory.segments, geometryVertices: manifest.inventory.geometryVertices,
      snow: manifest.scenario.snow, terrain: { sampleGridSize: terrain.sampleGridSize,
        demSpacingM: manifest.inventory.demSpacingM, elevationMinM: manifest.inventory.elevationMinM,
        elevationMaxM: manifest.inventory.elevationMaxM } } };
  writeFileSync(path.resolve('tests/e2e/performance/fixtures/jackson.fixture-lock.json'), `${JSON.stringify(lock, null, 2)}\n`);
}
console.log(JSON.stringify({ ok: true, root, fixtureId: manifest.fixtureId, terrainKey: manifest.terrainKey,
  saves: Object.fromEntries(([0, 1000, 3000] as const).map(target => [target, manifest.artifacts[target === 0 ? 'checkpointEmpty' : target === 1000 ? 'checkpoint1000' : 'checkpoint3000']])),
  inventory: manifest.inventory }, null, 2));

// Keep Vite's SSR entry from being tree-shaken as a side-effect-free module.
export const integratedFixturePreparedBy = fileURLToPath(import.meta.url);
