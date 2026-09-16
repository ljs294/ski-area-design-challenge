import { createHash } from 'node:crypto';
import { readFileSync } from 'node:fs';
import path from 'node:path';
import type { Page } from '@playwright/test';
import { validateIntegratedBenchmarkFixtureManifest, type IntegratedBenchmarkArtifact,
  type IntegratedBenchmarkFixtureManifest } from '../../../../src/integratedBenchmarkFixture';
import { INTEGRATED_BENCHMARK_SCENARIO_GLOBAL, type IntegratedBenchmarkScenario } from '../../../../src/integratedBenchmarkScenario';
import { createIntegratedFixtureTransport, readSelectedCheckpoint,
  type IntegratedBenchmarkFixtureSelection } from './integratedFixtureTransport';
export type { IntegratedBenchmarkFixtureSelection } from './integratedFixtureTransport';
import type { WeatherDataPackage } from '../../../../src/weather/weatherModel';
import { createWeatherPackageStorageInstall } from '../../../../src/weatherStorageClient';

export const DEFAULT_INTEGRATED_FIXTURE_ROOT = path.resolve('test-results/integrated-fixtures/jackson');
const JACKSON_FIXTURE_LOCK = path.resolve('tests/e2e/performance/fixtures/jackson.fixture-lock.json');

function json<T>(text: string, label: string): T {
  try { return JSON.parse(text) as T; } catch { throw new Error(`Fixture ${label} is not valid JSON.`); }
}

function checkpointArtifact(manifest: IntegratedBenchmarkFixtureManifest, target: 0 | 1000 | 3000) {
  return manifest.artifacts[target === 0 ? 'checkpointEmpty' : target === 1000 ? 'checkpoint1000' : 'checkpoint3000'];
}

export async function loadIntegratedBenchmarkFixture(root = path.resolve(
  process.env.INTEGRATED_BENCHMARK_FIXTURE_DIR ?? DEFAULT_INTEGRATED_FIXTURE_ROOT),
target: 0 | 1000 | 3000 = 3000): Promise<IntegratedBenchmarkFixtureSelection> {
  const manifestText = readFileSync(path.join(root, 'fixture-manifest.json'), 'utf8');
  const manifest = validateIntegratedBenchmarkFixtureManifest(json<IntegratedBenchmarkFixtureManifest>(manifestText, 'manifest'));
  const lock = json<{ fixtureId: string; terrainKey: string; manifestSha256: string;
    artifacts: IntegratedBenchmarkFixtureManifest['artifacts']; presentationAssets: IntegratedBenchmarkArtifact[] }>(
    readFileSync(JACKSON_FIXTURE_LOCK, 'utf8'), 'tracked lock');
  const manifestSha256 = createHash('sha256').update(manifestText).digest('hex');
  if (lock.fixtureId !== manifest.fixtureId || lock.terrainKey !== manifest.terrainKey
    || lock.manifestSha256 !== manifestSha256 || JSON.stringify(lock.artifacts) !== JSON.stringify(manifest.artifacts)
    || JSON.stringify(lock.presentationAssets.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })))
      !== JSON.stringify(manifest.requiredPresentationAssets.map(({ path, bytes, sha256 }) => ({ path, bytes, sha256 })))) {
    throw new Error('Local integrated fixture does not match the tracked Jackson fixture lock.');
  }
  const save = await readSelectedCheckpoint(root, checkpointArtifact(manifest, target));
  return { root, manifest, target, saves: { [target]: save } };
}

export function scenarioForSelectedFixture(fixture: IntegratedBenchmarkFixtureSelection, runId: string,
  requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64 = 1,
  tier: 'diagnostic' | 'qualification' = process.env.INTEGRATED_TIER === 'diagnostic' ? 'diagnostic' : 'qualification'):
IntegratedBenchmarkScenario {
  const save = fixture.saves[fixture.target]!;
  return { version: 1, tier, workload: fixture.target === 0 ? 'empty'
    : requestedSpeed >= 8 ? 'aggregate' : 'detailed', fixtureId: fixture.manifest.fixtureId, runId,
  seed: save.dualClock!.seed, replayId: 'integrated-standard-v1', saveKey: save.key,
  terrainKey: fixture.manifest.terrainKey, checkpointTarget: fixture.target, requestedSpeed,
  dailyDemand: fixture.target === 0 ? 0 : fixture.manifest.scenario.dailyDemand,
  dailyDemandByWeekday: fixture.target === 0 ? [0, 0, 0, 0, 0, 0, 0] : fixture.manifest.scenario.dailyDemandByWeekday,
  amenities: fixture.manifest.scenario.amenities };
}

export async function installIntegratedBenchmarkScenario(page: Page, fixture: IntegratedBenchmarkFixtureSelection,
  target: 0 | 1000 | 3000, runId: string, requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64 = 1): Promise<void> {
  if (target !== fixture.target) throw new Error('Loaded fixture checkpoint does not match the requested scenario target.');
  const scenario = scenarioForSelectedFixture(fixture, runId, requestedSpeed);
  await page.addInitScript(({ key, value }) => {
    (globalThis as typeof globalThis & Record<string, unknown>)[key] = value;
  }, { key: INTEGRATED_BENCHMARK_SCENARIO_GLOBAL, value: scenario });
}

export async function persistIntegratedBenchmarkFixtureLegacy(page: Page, fixture: IntegratedBenchmarkFixtureSelection,
  target: 0 | 1000 | 3000): Promise<void> {
  if (target !== fixture.target) throw new Error('Loaded fixture checkpoint does not match persistence target.');
  const save = fixture.saves[target]!, transport = await createIntegratedFixtureTransport(fixture);
  try {
    await page.route(`${transport.origin}/**`, route => route.continue());
    await page.evaluate(async ({ urls, save, terrainKey }) => {
      const terrain = await fetch(urls.terrain).then(response => response.json());
      if (urls.imagery) terrain.localImagery = Array.from(new Uint8Array(
        await fetch(urls.imagery).then(response => response.arrayBuffer()) as ArrayBuffer));
      const weather = await fetch(urls.weather).then(response => response.json()); weather.manifest.terrainKey = terrainKey;
      const sourceChunks = weather.chunks.map((chunk: { descriptor: unknown }) => chunk.descriptor);
      const identity = new TextEncoder().encode(JSON.stringify({ sourceManifest: weather.manifest, sourceChunks }));
      const payloadHash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', identity))]
        .map(value => value.toString(16).padStart(2, '0')).join('');
      const decode = (value: string) => { const binary = atob(value), bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index); return bytes; };
      const installChunks = weather.chunks.map((chunk: { descriptor: { id: string; byteLength: number;
        checksumSha256: string }; dataBase64: string }) => ({ key: chunk.descriptor.id, encoding: 'binary',
      byteLength: chunk.descriptor.byteLength, checksum: chunk.descriptor.checksumSha256, data: decode(chunk.dataBase64) }));
      const installManifest = { storageSchemaVersion: 2, contentHash: weather.manifest.contentHash, terrainKey,
        terrainBinding: weather.manifest.terrainBinding, payloadHash, payloadFormat: 'weather-package-chunks-v1',
        sourceManifest: weather.manifest, sourceChunks,
        chunks: installChunks.map((chunk: typeof installChunks[number]) => {
          const { data, ...descriptor } = chunk; void data; return descriptor;
        }), complete: true,
        createdAt: weather.manifest.createdAt };
      const put = (databaseName: string, version: number, storeName: string, value: unknown,
        stores: readonly { name: string; keyPath: string }[]) => new Promise<void>((resolve, reject) => {
        const request = indexedDB.open(databaseName, version);
        request.onupgradeneeded = () => { for (const store of stores) if (!request.result.objectStoreNames.contains(store.name)) {
          const created = request.result.createObjectStore(store.name, { keyPath: store.keyPath });
          if (store.name === 'chunks') created.createIndex('contentHash', 'contentHash', { unique: false });
        } };
        request.onerror = () => reject(request.error);
        request.onsuccess = () => { const database = request.result, transaction = database.transaction(storeName, 'readwrite');
          transaction.objectStore(storeName).put(value); transaction.onerror = () => reject(transaction.error);
          transaction.oncomplete = () => { database.close(); resolve(); }; };
      });
      localStorage.clear(); localStorage.setItem('gamesave-index', JSON.stringify([{ key: save.key, name: save.name,
        terrainKey: save.terrainKey, createdAt: save.createdAt, updatedAt: save.updatedAt }]));
      await put('mountain-planner-terrain', 2, 'terrains', terrain,
        ['terrains', 'terrain-summaries', 'terrain-metadata', 'terrain-assets'].map(name => ({ name, keyPath: 'key' })));
      await put('mountain-planner-weather', 3, 'packages', weather, [{ name: 'packages', keyPath: 'terrainKey' },
        { name: 'manifests', keyPath: 'contentHash' }, { name: 'chunks', keyPath: 'id' }, { name: 'active', keyPath: 'terrainKey' }]);
      const weatherStores = [{ name: 'packages', keyPath: 'terrainKey' }, { name: 'manifests', keyPath: 'contentHash' },
        { name: 'chunks', keyPath: 'id' }, { name: 'active', keyPath: 'terrainKey' }];
      for (const chunk of installChunks) await put('mountain-planner-weather', 3, 'chunks', {
        id: `${installManifest.contentHash}\u0000${chunk.key}`, contentHash: installManifest.contentHash,
        key: chunk.key, data: chunk.data }, weatherStores);
      await put('mountain-planner-weather', 3, 'manifests', installManifest, weatherStores);
      await put('mountain-planner-weather', 3, 'active', { terrainKey,
        terrainBinding: installManifest.terrainBinding, contentHash: installManifest.contentHash,
        updatedAt: new Date().toISOString() }, weatherStores);
      await put('mountain-planner-dual-saves', 1, 'games', save, [{ name: 'games', keyPath: 'key' }]);
      const bridge = (globalThis as typeof globalThis & { desktop?: { terrain?: { save(v: unknown): Promise<unknown> };
        weather?: { save(v: unknown): Promise<unknown>; install(v: unknown): Promise<unknown> };
        games?: { save(v: unknown): Promise<unknown> } } }).desktop;
      if (bridge?.terrain?.save) await bridge.terrain.save(terrain);
      if (bridge?.weather?.install) await bridge.weather.install({ manifest: installManifest, chunks: installChunks });
      else if (bridge?.weather?.save) await bridge.weather.save(weather);
      if (bridge?.games?.save) await bridge.games.save(save);
    }, { urls: transport.urls, save, terrainKey: fixture.manifest.terrainKey });
  } finally { await page.unroute(`${transport.origin}/**`); await transport.close(); }
}

export async function persistIntegratedBenchmarkFixture(page: Page, fixture: IntegratedBenchmarkFixtureSelection,
  target: 0 | 1000 | 3000): Promise<{ terrainMs: number; weatherMs: number; saveMs: number }> {
  if (target !== fixture.target) throw new Error('Loaded fixture checkpoint does not match persistence target.');
  const save = fixture.saves[target]!, transport = await createIntegratedFixtureTransport(fixture);
  await page.route(`${transport.origin}/**`, route => route.continue());
  try {
    const terrainMs = await page.evaluate(async urls => {
      const started = performance.now(), response = await fetch(urls.terrain);
      if (!response.ok) throw new Error(`Terrain transport returned ${response.status}.`);
      const terrain = await response.json();
      if (urls.imagery) { const imagery = await fetch(urls.imagery); if (!imagery.ok) throw new Error(`Imagery transport returned ${imagery.status}.`);
        terrain.localImagery = Array.from(new Uint8Array(await imagery.arrayBuffer())); }
      const bridge = (globalThis as typeof globalThis & { desktop?: { terrain?: { save(v: unknown): Promise<unknown> } } }).desktop;
      if (bridge?.terrain?.save) await bridge.terrain.save(terrain);
      else await new Promise<void>((resolve, reject) => { const request = indexedDB.open('mountain-planner-terrain', 2);
        request.onupgradeneeded = () => { for (const name of ['terrains', 'terrain-summaries', 'terrain-metadata', 'terrain-assets']) {
          if (!request.result.objectStoreNames.contains(name)) request.result.createObjectStore(name, { keyPath: 'key' }); } };
        request.onerror = () => reject(request.error); request.onsuccess = () => { const database = request.result,
          transaction = database.transaction('terrains', 'readwrite'); transaction.objectStore('terrains').put(terrain);
          transaction.onerror = () => reject(transaction.error); transaction.oncomplete = () => { database.close(); resolve(); }; }; });
      return performance.now() - started;
    }, transport.urls);
    const weatherMs = await page.evaluate(async ({ url, terrainKey }) => {
      const started = performance.now(), response = await fetch(url); if (!response.ok) throw new Error(`Weather transport returned ${response.status}.`);
      const weather = await response.json(); weather.manifest.terrainKey = terrainKey;
      const sourceChunks = weather.chunks.map((chunk: { descriptor: unknown }) => chunk.descriptor);
      const identity = new TextEncoder().encode(JSON.stringify({ sourceManifest: weather.manifest, sourceChunks }));
      const payloadHash = [...new Uint8Array(await crypto.subtle.digest('SHA-256', identity))]
        .map(value => value.toString(16).padStart(2, '0')).join('');
      const decode = (value: string) => { const binary = atob(value), bytes = new Uint8Array(binary.length);
        for (let index = 0; index < binary.length; index++) bytes[index] = binary.charCodeAt(index); return bytes; };
      const chunks = weather.chunks.map((chunk: { descriptor: { id: string; byteLength: number; checksumSha256: string };
        dataBase64: string }) => ({ key: chunk.descriptor.id, encoding: 'binary', byteLength: chunk.descriptor.byteLength,
      checksum: chunk.descriptor.checksumSha256, data: decode(chunk.dataBase64) }));
      const manifest = { storageSchemaVersion: 2, contentHash: weather.manifest.contentHash, terrainKey,
        terrainBinding: weather.manifest.terrainBinding, payloadHash, payloadFormat: 'weather-package-chunks-v1',
        sourceManifest: weather.manifest, sourceChunks,
        chunks: chunks.map((chunk: typeof chunks[number]) => {
          const { data, ...descriptor } = chunk; void data; return descriptor;
        }),
        complete: true, createdAt: weather.manifest.createdAt };
      const bridge = (globalThis as typeof globalThis & { desktop?: { weather?: { install(v: unknown): Promise<unknown> } } }).desktop;
      if (bridge?.weather?.install) await bridge.weather.install({ manifest, chunks });
      else {
        const put = (storeName: string, value: unknown) => new Promise<void>((resolve, reject) => {
          const request = indexedDB.open('mountain-planner-weather', 3); request.onupgradeneeded = () => {
            for (const [name, keyPath] of [['packages', 'terrainKey'], ['manifests', 'contentHash'], ['chunks', 'id'],
              ['active', 'terrainKey']] as const) if (!request.result.objectStoreNames.contains(name)) {
              const store = request.result.createObjectStore(name, { keyPath });
              if (name === 'chunks') store.createIndex('contentHash', 'contentHash', { unique: false }); } };
          request.onerror = () => reject(request.error); request.onsuccess = () => { const database = request.result,
            transaction = database.transaction(storeName, 'readwrite'); transaction.objectStore(storeName).put(value);
            transaction.onerror = () => reject(transaction.error); transaction.oncomplete = () => { database.close(); resolve(); }; }; });
        await put('packages', weather);
        for (const chunk of chunks) await put('chunks', { id: `${manifest.contentHash}\u0000${chunk.key}`,
          contentHash: manifest.contentHash, key: chunk.key, data: chunk.data });
        await put('manifests', manifest); await put('active', { terrainKey, terrainBinding: manifest.terrainBinding,
          contentHash: manifest.contentHash, updatedAt: new Date().toISOString() });
      }
      return performance.now() - started;
    }, { url: transport.urls.weather, terrainKey: fixture.manifest.terrainKey });
    const saveMs = await page.evaluate(async saveValue => {
      const started = performance.now(); localStorage.clear(); localStorage.setItem('gamesave-index', JSON.stringify([{
        key: saveValue.key, name: saveValue.name, terrainKey: saveValue.terrainKey,
        createdAt: saveValue.createdAt, updatedAt: saveValue.updatedAt }]));
      const bridge = (globalThis as typeof globalThis & { desktop?: { games?: { save(v: unknown): Promise<unknown> } } }).desktop;
      if (bridge?.games?.save) await bridge.games.save(saveValue);
      else await new Promise<void>((resolve, reject) => { const request = indexedDB.open('mountain-planner-dual-saves', 1);
        request.onupgradeneeded = () => { if (!request.result.objectStoreNames.contains('games')) {
          request.result.createObjectStore('games', { keyPath: 'key' }); } }; request.onerror = () => reject(request.error);
        request.onsuccess = () => { const database = request.result, transaction = database.transaction('games', 'readwrite');
          transaction.objectStore('games').put(saveValue); transaction.onerror = () => reject(transaction.error);
          transaction.oncomplete = () => { database.close(); resolve(); }; }; });
      return performance.now() - started;
    }, save);
    return { terrainMs, weatherMs, saveMs };
  } finally { await page.unroute(`${transport.origin}/**`); await transport.close(); }
}

export async function persistIntegratedBenchmarkWeather(page: Page, weather: WeatherDataPackage) {
  const install = await createWeatherPackageStorageInstall(weather);
  const serializable = { manifest: install.manifest,
    chunks: install.chunks.map(chunk => ({ ...chunk, data: Array.from(chunk.data) })) };
  await page.evaluate(async weatherInstall => {
    const request = indexedDB.open('mountain-planner-weather', 3);
    await new Promise<void>((resolve, reject) => { request.onupgradeneeded = () => {
      for (const [name, keyPath] of [['packages', 'terrainKey'], ['manifests', 'contentHash'], ['chunks', 'id'],
        ['active', 'terrainKey']] as const) if (!request.result.objectStoreNames.contains(name)) {
        const store = request.result.createObjectStore(name, { keyPath });
        if (name === 'chunks') store.createIndex('contentHash', 'contentHash', { unique: false });
      } }; request.onerror = () => reject(request.error); request.onsuccess = () => resolve(); });
    const database = request.result;
    const put = (store: string, value: unknown) => new Promise<void>((resolve, reject) => {
      const transaction = database.transaction(store, 'readwrite'); transaction.objectStore(store).put(value);
      transaction.oncomplete = () => resolve(); transaction.onerror = () => reject(transaction.error); });
    for (const chunk of weatherInstall.chunks) await put('chunks', { id: `${weatherInstall.manifest.contentHash}\u0000${chunk.key}`,
      contentHash: weatherInstall.manifest.contentHash, key: chunk.key, data: new Uint8Array(chunk.data) });
    await put('manifests', weatherInstall.manifest); await put('active', { terrainKey: weatherInstall.manifest.terrainKey,
      terrainBinding: weatherInstall.manifest.terrainBinding, contentHash: weatherInstall.manifest.contentHash,
      updatedAt: new Date().toISOString() }); database.close();
  }, serializable);
  return install;
}
