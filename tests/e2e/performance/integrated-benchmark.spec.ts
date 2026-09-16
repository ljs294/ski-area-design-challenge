import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, test, type Page } from '@playwright/test';
import { startIntegratedDiagnosticProfile } from '../../../scripts/integratedDiagnosticProfiler.mjs';
import { DualClockEngine } from '../../../src/dualClock/engine';
import { DEFAULT_DUAL_CONFIG } from '../../../src/dualClock/model';
import { fixtureHour } from '../../../src/dualClock/fixtures';
import { resolveIntegratedBenchmarkConfiguration } from '../../../scripts/integratedBenchmarkConfiguration.mjs';
import { buildSkiNetwork } from '../../../src/network';
import { encodeSnowGrid, generateBareSnowGrid } from '../../../src/snow';
import { sanitizeTrails } from '../../../src/trails';
import type { SavedLift } from '../../../src/types/lifts';
import type { SavedTrail } from '../../../src/types/trails';
import type { TerrainRecord } from '../../../src/types/terrain';
import type { WeatherDataPackage } from '../../../src/weather/weatherModel';
import { weatherTerrainBinding } from '../../../src/weather/terrainBinding';
import { validateIntegratedBenchmarkScenario } from '../../../src/integratedBenchmarkScenario';
import { validateIntegratedBenchmarkFixtureManifest } from '../../../src/integratedBenchmarkFixture';
import { preparedTerrainFixture } from '../support/preparedResort';
import { installWorkerProbe, workerEntries, type WorkerProbePublication } from '../support/workerProbe';
import { installIntegratedBenchmarkScenario, loadIntegratedBenchmarkFixture,
  persistIntegratedBenchmarkFixture, persistIntegratedBenchmarkWeather } from './support/integratedFixture';
import { retainIntegratedFailure, restoreSelectedCheckpoint, runIntegratedStage, waitForMapSourceReadiness,
  type IntegratedFailureStage } from './support/integratedRuntime';

const telemetryConfig = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__';
const telemetryState = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY_STATE__';
const scenarioGlobal = '__MOUNTAIN_PLANNER_INTEGRATED_BENCHMARK__';
let failureStage: IntegratedFailureStage = 'launch';
test.afterEach(async ({ page }, testInfo) => {
  if (testInfo.status === testInfo.expectedStatus) return;
  let observations: Record<string, unknown> = { title: testInfo.title, status: testInfo.status };
  try { observations = { ...observations, url: page.url(), body: (await page.locator('body').innerText()).slice(0, 8_192) }; }
  catch (error) { observations.observationError = error instanceof Error ? error.message : String(error); }
  retainIntegratedFailure(path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark'),
    `${process.env.INTEGRATED_RUN_ID ?? 'browser-spec'}-attempt-${Date.now()}-r${testInfo.retry}`, failureStage,
    testInfo.error ?? new Error(`Playwright ${testInfo.status}`), observations);
});
const runConfiguration = resolveIntegratedBenchmarkConfiguration();
const qualification = runConfiguration.isQualification;
const jacksonFixture = runConfiguration.fixture === 'jackson';
const runId = process.env.INTEGRATED_RUN_ID ?? `diagnostic-${Date.now()}`;
const scenarioName = runConfiguration.scenarioName;
type ScenarioContract = { checkpointTarget: 0 | 1000 | 3000; requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64;
  workload: 'empty' | 'detailed' | 'aggregate'; workflow: string; profile: 'Standard' | 'High';
  cssViewport: { width: number; height: number }; canvas: { width: number; height: number }; devicePixelRatio: number };
const scenarioMatrix = JSON.parse(readFileSync(path.resolve('scripts/integratedBenchmarkScenarios.json'), 'utf8')) as
  Record<string, ScenarioContract>;
const selectedScenario = (scenarioMatrix as Record<string, ScenarioContract>)[scenarioName];
if (!selectedScenario) throw new Error(`Unknown integrated benchmark scenario: ${scenarioName}`);
const target = selectedScenario.checkpointTarget as 0 | 32 | 1000 | 3000;
const requestedSpeed = selectedScenario.requestedSpeed;
const workload = selectedScenario.workload;
const workflow = selectedScenario.workflow;
const appQuery = process.env.INTEGRATED_APP_QUERY ?? '?dev-console';
if (new URLSearchParams(appQuery.startsWith('?') ? appQuery.slice(1) : appQuery).has('flat')) {
  throw new Error('Integrated benchmark cannot disable terrain with the flat query.');
}
const warmupMs = Number(process.env.INTEGRATED_WARMUP_MS ?? (qualification ? 30_000 : 750));
const measurementMs = Number(process.env.INTEGRATED_MEASURE_MS ?? (qualification ? 120_000 : 2_000));
const snowDemandDiagnostic = process.env.INTEGRATED_SNOW_DEMAND_DIAGNOSTIC === '1';

function heavyDiagnosticRequested(): boolean {
  if (process.env.INTEGRATED_PROFILING !== 'heavy') return false;
  if (!process.env.INTEGRATED_RUN_IDENTITY_PATH) throw new Error('Heavy profiling requires runner-supplied diagnostic identity.');
  const identity = JSON.parse(readFileSync(process.env.INTEGRATED_RUN_IDENTITY_PATH, 'utf8')) as { tier?: string };
  if (identity.tier !== 'diagnostic') throw new Error('Heavy profiling is restricted to diagnostic trials.');
  return true;
}

function snowAffectedCells(text: string): number {
  return Number((/across ([\d,]+) terrain cells/.exec(text)?.[1] ?? '0').replaceAll(',', ''));
}

type TelemetryEntry = { stage: string; at: number; generation?: number; publicationSequence?: number;
  requestId?: number; operationGeneration?: number; committedRevision?: number; count?: number; terrainKey?: string; detail?: string };

function diagnosticPackage(targetGuests: 0 | 32 = 32) {
  const terrain = preparedTerrainFixture() as unknown as TerrainRecord;
  const base: [number, number] = [-121.495, 46.902];
  const tops: [number, number][] = [[-121.498, 46.908], [-121.495, 46.909], [-121.492, 46.908]];
  const lifts: SavedLift[] = tops.map((top, index) => ({ id: `diag-lift-${index}`, identifier: String(index + 1),
    name: `Diagnostic Lift ${index + 1}`, liftTypeId: 'fixed-grip-quad', points: [base, top],
    endpointElevM: [1000, 1250 + index * 20], lengthM: 750, verticalM: 250 + index * 20,
    status: 'complete', createdAt: '2026-01-01T00:00:00.000Z' }));
  const trails: SavedTrail[] = sanitizeTrails(tops.map((top, index) => ({ id: `diag-trail-${index}`,
    name: `Diagnostic Trail ${index + 1}`, brushWidthM: 30, areaM2: 22_500, lengthM: 750,
    verticalM: 250 + index * 20, avgSlopeDeg: 20, maxSlopeDeg: 25, difficulty: 'blue' as const,
    status: 'complete' as const, createdAt: '2026-01-01T00:00:00.000Z', parts: [{
      polygon: [[[top[0] - .0002, top[1]], [top[0] + .0002, top[1]], [base[0] + .0002, base[1]],
        [base[0] - .0002, base[1]], [top[0] - .0002, top[1]]]],
      centerline: [top, base], centerlineElevM: [1250 + index * 20, 1000],
    }] })));
  const network = buildSkiNetwork(trails, lifts);
  const liftEdges = network.edges.filter(edge => edge.kind === 'lift');
  const amenities = liftEdges.map((edge, index) => ({ id: `diag-amenity-${index}`, nodeId: edge.to,
    label: `Diagnostic lodge ${index}`, priceCents: 1000, capacityPerHour: 1000, inventory: 10_000,
    opens: 8, closes: 16, accessSeconds: 10, serviceSeconds: 30,
    need: (index === 1 ? 'thirst' : 'hunger') as 'thirst' | 'hunger', relief: { hunger: .8, thirst: .8 } }));
  const at = '2026-01-15T13:00:00.000Z';
  const snow = generateBareSnowGrid(terrain); snow.depthM.fill(1); snow.surface.fill(1);
  const weather = Array.from({ length: 96 }, (_, index) => fixtureHour(
    new Date(Date.parse(at) + index * 3_600_000).toISOString()));
  const dailyDemand = targetGuests === 0 ? 0 : 10_000;
  const resort = { revision: 1, edges: network.edges, trails, portal: {
    id: 'diag-portal', nodeId: liftEdges[0]!.from, lngLat: base, capacityPerMinute: 1000 },
    dailyDemand, dailyDemandByWeekday: [dailyDemand, dailyDemand, dailyDemand, dailyDemand, dailyDemand, dailyDemand, dailyDemand],
    ticketPriceCents: 12_500, amenities } as const;
  const preparing = new DualClockEngine({ seed: 'integrated-diagnostic-v1', at, timezone: 'UTC', snow, terrain,
    weather, resort, config: { ...DEFAULT_DUAL_CONFIG, representativeLimit: 32 } });
  if (targetGuests > 0) {
    preparing.play(); const work = preparing.advanceTo(Date.parse(at) + 3_600_000);
    while (!work.next().done) { /* deterministic checkpoint preparation */ }
    preparing.pause();
  }
  const checkpoint = targetGuests === 0 ? preparing.checkpoint() : preparing.prepareRepresentativeCheckpoint(targetGuests);
  const save = { schemaVersion: 17, key: targetGuests === 0 ? 'integrated-diagnostic-empty-save' : 'integrated-diagnostic-save',
    name: targetGuests === 0 ? 'Integrated empty diagnostic resort' : 'Integrated diagnostic resort',
    terrainKey: terrain.key, center: base, zoom: 13, bearing: 0, pitch: 35, is3D: true,
    site: { bounds: [[terrain.bounds!.west, terrain.bounds!.south], [terrain.bounds!.east, terrain.bounds!.north]],
      widthKm: .76, heightKm: 1.11, areaKm2: .84 }, lifts, trails, snow: encodeSnowGrid(snow), dualClock: checkpoint,
    createdAt: at, updatedAt: at };
  const weatherPackage: WeatherDataPackage = { manifest: { schemaVersion: 1, terrainKey: terrain.key,
    terrainBinding: weatherTerrainBinding(terrain), timezone: 'UTC', historicalStartYear: 2026,
    historicalEndYear: 2026, quality: 'limited', sourceSummary: 'deterministic diagnostic',
    sourceVersion: 'diagnostic-v1', generatorVersion: 2, contentHash: 'integrated-diagnostic-weather',
    complete: true, createdAt: at }, historicalYears: [{ year: 2026, hours: weather }] };
  const scenario = { version: 1 as const, tier: 'diagnostic' as const,
    workload: (targetGuests === 0 ? 'empty' : 'detailed') as 'empty' | 'detailed',
    fixtureId: 'integrated-diagnostic-v1', runId, seed: checkpoint.seed, replayId: 'diagnostic-v1', requestedSpeed: 1,
    saveKey: save.key, terrainKey: terrain.key, checkpointTarget: targetGuests, dailyDemand,
    dailyDemandByWeekday: resort.dailyDemandByWeekday, amenities };
  validateIntegratedBenchmarkScenario(scenario);
  return { terrain, save, weatherPackage, scenario };
}

async function seedDiagnostic(page: Page, data: ReturnType<typeof diagnosticPackage>): Promise<void> {
  await persistIntegratedBenchmarkWeather(page, data.weatherPackage);
  await page.evaluate(async ({ terrain, save }) => {
    localStorage.clear();
    localStorage.setItem('gamesave-index', JSON.stringify([{ key: save.key, name: save.name,
      terrainKey: save.terrainKey, createdAt: save.createdAt, updatedAt: save.updatedAt }]));
    const put = (name: string, version: number, store: string, value: unknown,
      stores: readonly { name: string; keyPath: string }[]) => new Promise<void>((resolve, reject) => {
      const request = indexedDB.open(name, version);
      request.onupgradeneeded = () => { for (const item of stores) if (!request.result.objectStoreNames.contains(item.name))
        request.result.createObjectStore(item.name, { keyPath: item.keyPath }); };
      request.onerror = () => reject(request.error);
      request.onsuccess = () => { const database = request.result, transaction = database.transaction(store, 'readwrite');
        transaction.objectStore(store).put(value); transaction.onerror = () => reject(transaction.error);
        transaction.oncomplete = () => { database.close(); resolve(); }; };
    });
    await put('mountain-planner-terrain', 2, 'terrains', terrain,
      ['terrains', 'terrain-summaries', 'terrain-metadata', 'terrain-assets'].map(name => ({ name, keyPath: 'key' })));
    await put('mountain-planner-dual-saves', 1, 'games', save, [{ name: 'games', keyPath: 'key' }]);
  }, data);
}

async function installInstrumentation(page: Page, enabled: boolean): Promise<void> {
  await installWorkerProbe(page);
  await page.addInitScript(({ configKey, stateKey, enabled: active }) => {
    (globalThis as typeof globalThis & Record<string, unknown>)[configKey] = { enabled: active, maxEntries: 20_000 };
    delete (globalThis as typeof globalThis & Record<string, unknown>)[stateKey];
    (globalThis as typeof globalThis & { integratedFrameProbe?: unknown }).integratedFrameProbe = {
      raf: [] as number[], renders: [] as number[], longTasks: [] as number[], sourceEvents: [] as string[],
      resources: [] as { at: number; rendererHeapBytes?: number; domNodes: number; resourceEntries: number;
        mapSources: number; mapLayers: number; drawnGuests: number; workers: number }[],
      startedAt: 0, lastRaf: 0, lastRender: 0, stop: null as null | (() => void),
    };
    let currentMap: { on(type: string, listener: (event: { sourceId?: string; isSourceLoaded?: boolean }) => void): void } | undefined;
    Object.defineProperty(globalThis, 'appMap', { configurable: true, enumerable: true,
      get: () => currentMap, set: value => { currentMap = value as typeof currentMap;
        currentMap?.on('sourcedata', event => { if (event.sourceId && event.isSourceLoaded) {
          ((globalThis as typeof globalThis & { integratedFrameProbe: { sourceEvents: string[] } }).integratedFrameProbe)
            .sourceEvents.push(event.sourceId);
        } }); } });
  }, { configKey: telemetryConfig, stateKey: telemetryState, enabled });
}

async function startFrameProbe(page: Page): Promise<void> {
  await page.evaluate(() => {
    const holder = globalThis as typeof globalThis & { integratedFrameProbe: {
      raf: number[]; renders: number[]; longTasks: number[]; sourceEvents: string[]; startedAt: number;
      resources: { at: number; rendererHeapBytes?: number; domNodes: number; resourceEntries: number;
        mapSources: number; mapLayers: number; drawnGuests: number; workers: number }[];
      lastRaf: number; lastRender: number; stop: null | (() => void) };
      appMap: { on(type: string, listener: (event: { sourceId?: string; isSourceLoaded?: boolean }) => void): void;
        off(type: string, listener: (event: { sourceId?: string; isSourceLoaded?: boolean }) => void): void; triggerRepaint(): void;
        getStyle(): { sources?: Record<string, unknown>; layers?: unknown[] };
        getLayer(id: string): { implementation?: Record<string, unknown> } | undefined } };
    const probe = holder.integratedFrameProbe; probe.startedAt = performance.now();
    let frame = 0, prior = probe.startedAt;
    const raf = (at: number) => { probe.raf.push(at - prior); prior = at; probe.lastRaf = at; frame = requestAnimationFrame(raf); };
    const render = () => { const at = performance.now(); probe.renders.push(probe.lastRender ? at - probe.lastRender : 0);
      probe.lastRender = at; };
    const source = (event: { sourceId?: string; isSourceLoaded?: boolean }) => {
      if (event.sourceId && event.isSourceLoaded) probe.sourceEvents.push(event.sourceId);
    };
    const observer = typeof PerformanceObserver === 'undefined' ? null : new PerformanceObserver(list => {
      for (const entry of list.getEntries()) probe.longTasks.push(entry.duration);
    });
    try { observer?.observe({ type: 'longtask', buffered: true }); } catch { /* API unavailable */ }
    const sampleResources = () => { const style = holder.appMap.getStyle();
      const memory = (performance as Performance & { memory?: { usedJSHeapSize?: number } }).memory;
      probe.resources.push({ at: performance.now(), ...(Number.isFinite(memory?.usedJSHeapSize)
        ? { rendererHeapBytes: memory!.usedJSHeapSize } : {}), domNodes: document.getElementsByTagName('*').length,
      resourceEntries: performance.getEntriesByType('resource').length, mapSources: Object.keys(style.sources ?? {}).length,
      mapLayers: style.layers?.length ?? 0,
      drawnGuests: Number(holder.appMap.getLayer('guest-simulation-dots')?.implementation?.count ?? 0),
      workers: ((globalThis as typeof globalThis & { appWorkerProbe?: { terminationCount: number }[] }).appWorkerProbe ?? [])
        .filter(entry => entry.terminationCount === 0).length }); };
    sampleResources(); const resourceTimer = setInterval(sampleResources, 30_000);
    holder.appMap.on('render', render); holder.appMap.on('sourcedata', source); frame = requestAnimationFrame(raf);
    holder.appMap.triggerRepaint();
    probe.stop = () => { cancelAnimationFrame(frame); clearInterval(resourceTimer); sampleResources();
      observer?.disconnect(); holder.appMap.off('render', render);
      holder.appMap.off('sourcedata', source); };
  });
}

async function finishFrameProbe(page: Page, windowMs: number) {
  await page.waitForTimeout(windowMs);
  return page.evaluate(() => {
    const probe = (globalThis as typeof globalThis & { integratedFrameProbe: {
      raf: number[]; renders: number[]; longTasks: number[]; sourceEvents: string[]; startedAt: number;
      resources: { at: number; rendererHeapBytes?: number; domNodes: number; resourceEntries: number;
        mapSources: number; mapLayers: number; drawnGuests: number; workers: number }[];
      lastRaf: number; lastRender: number; stop: null | (() => void) } }).integratedFrameProbe;
    probe.stop?.(); const endedAt = performance.now();
    const rafTrailingGapMs = endedAt - probe.lastRaf, renderTrailingGapMs = endedAt - probe.lastRender;
    probe.raf.push(rafTrailingGapMs); probe.renders.push(renderTrailingGapMs);
    return { startedAt: probe.startedAt, windowDurationMs: endedAt - probe.startedAt, rafIntervals: probe.raf, renderIntervals: probe.renders,
      longTasks: probe.longTasks, sourceEvents: probe.sourceEvents, resourceSamples: probe.resources,
      rafTrailingGapMs, renderTrailingGapMs };
  });
}

async function startSimulationEvidence(page: Page): Promise<void> {
  await page.evaluate(() => {
    const samples: unknown[] = [];
    let lastKey = '';
    const sample = () => {
      const entries = (globalThis as typeof globalThis & { appWorkerProbe?: { publications?: Record<string, unknown>[] }[] })
        .appWorkerProbe ?? [];
      const latest = entries.flatMap(entry => entry.publications ?? []).reverse().find(publication =>
        Number.isFinite(publication.macroSecond) && Number.isFinite(publication.microSecond)
        && Number.isFinite((publication.flow as { active?: number } | undefined)?.active)
        && Number.isFinite(publication.movementCount));
      if (!latest) return;
      const key = `${latest.generation}:${latest.id}:${latest.committedRevision}:${latest.macroSecond}:${latest.microSecond}`;
      if (key !== lastKey) { lastKey = key; samples.push(structuredClone(latest)); }
    };
    sample(); const timer = setInterval(sample, 1_000);
    (globalThis as typeof globalThis & { integratedSimulationEvidence?: unknown }).integratedSimulationEvidence = {
      samples, count: () => samples.length, stop: () => { clearInterval(timer); sample(); },
    };
  });
}

const simulationEvidenceCount = (page: Page) => page.evaluate(() =>
  (globalThis as typeof globalThis & { integratedSimulationEvidence: { count(): number } })
    .integratedSimulationEvidence.count());

async function finishSimulationEvidence(page: Page) {
  return page.evaluate(() => {
    const probe = (globalThis as typeof globalThis & { integratedSimulationEvidence: {
      samples: { id: number; type: string; generation: number; operationGeneration: number; committedRevision: number;
        macroSecond?: number; microSecond?: number; at?: string; requestType?: string; requestedTargetMs?: number;
        busy?: boolean; movementCount?: number; flow?: unknown }[]; stop(): void } }).integratedSimulationEvidence;
    probe.stop(); return probe.samples;
  });
}

function percentile(values: number[], probability: number): number {
  const sorted = [...values].sort((a, b) => a - b), position = (sorted.length - 1) * probability;
  const low = Math.floor(position), fraction = position - low;
  return sorted[low]! + (sorted[Math.min(low + 1, sorted.length - 1)]! - sorted[low]!) * fraction;
}

function telemetry(page: Page): Promise<{ entries: TelemetryEntry[]; dropped: number }> {
  return page.evaluate(({ stateKey }) => {
    const state = (globalThis as typeof globalThis & Record<string, unknown>)[stateKey] as {
      entries?: (TelemetryEntry | undefined)[]; maxEntries?: number; size?: number; writeIndex?: number; dropped?: number } | undefined;
    if (!state?.entries || !state.maxEntries || !state.size) return { entries: [], dropped: state?.dropped ?? 0 };
    const first = state.size === state.maxEntries ? state.writeIndex ?? 0 : 0, entries: TelemetryEntry[] = [];
    for (let index = 0; index < state.size; index++) { const entry = state.entries[(first + index) % state.maxEntries];
      if (entry) entries.push(entry); }
    return { entries, dropped: state.dropped ?? 0 };
  }, { stateKey: telemetryState });
}

async function snowDemandSnapshot(page: Page) {
  return page.evaluate(({ stateKey }) => {
    const holder = globalThis as typeof globalThis & Record<string, unknown>;
    const map = holder.appMap as {
      getCenter(): { lng: number; lat: number }; getZoom(): number; getBearing(): number; getPitch(): number;
      getBounds(): { toArray(): [[number, number], [number, number]] }; getLayoutProperty(id: string, name: string): unknown;
      getSource(id: string): { serialize?(): unknown } | undefined;
      getLayer(id: string): { source?: string } | undefined;
      isSourceLoaded(id: string): boolean; isStyleLoaded(): boolean; isMoving(): boolean; loaded(): boolean;
    };
    const layer = map.getLayer('snow');
    const telemetrySnapshot = holder[stateKey] as { entries?: unknown[]; maxEntries?: number; size?: number; writeIndex?: number;
      dropped?: number } | undefined;
    const entries = telemetrySnapshot?.entries ?? [], size = telemetrySnapshot?.size ?? 0, maxEntries = telemetrySnapshot?.maxEntries ?? 0;
    const first = size === maxEntries ? telemetrySnapshot?.writeIndex ?? 0 : 0;
    const protocolTelemetry = Array.from({ length: size }, (_, index) => entries[(first + index) % maxEntries]).filter(Boolean);
    const sourceEvents = ((holder.integratedFrameProbe as { sourceEvents?: unknown[] } | undefined)?.sourceEvents ?? []).slice();
    return {
      compiledLayer: { publicVisibility: map.getLayoutProperty('snow', 'visibility') ?? null,
        source: layer?.source ?? null },
      zoom: map.getZoom(), styleLoaded: map.isStyleLoaded(), mapLoaded: map.loaded(), isMoving: map.isMoving(),
      camera: { center: map.getCenter(), bearing: map.getBearing(), pitch: map.getPitch(), bounds: map.getBounds().toArray() },
      sourceLoaded: map.isSourceLoaded('snow'), sourceEvents, protocolTelemetry,
      source: map.getSource('snow')?.serialize?.() ?? null,
    };
  }, { stateKey: telemetryState });
}

async function runSnowDemandDiagnostic(page: Page): Promise<void> {
  const evidenceDir = path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark');
  mkdirSync(evidenceDir, { recursive: true });
  const evidencePath = path.join(evidenceDir, `${runId}.snow-demand-diagnostic.json`);
  const snowToggle = page.getByRole('checkbox', { name: 'Snow', exact: true });
  await expect.poll(() => page.evaluate(() => !!(globalThis as typeof globalThis & {
    __MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__?: unknown }).__MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__), {
    message: 'snow demand diagnostics require telemetry-enabled condition B', timeout: 10_000,
  }).toBe(true);
  const play = page.getByRole('button', { name: 'Play game clock', exact: true });
  if (await play.isVisible()) { await expect(play).toBeEnabled({ timeout: 30_000 }); await play.click(); }
  const pause = page.getByRole('button', { name: 'Pause game clock', exact: true });
  await pause.click(); await expect(play).toBeVisible();
  if (!await snowToggle.isVisible()) await page.getByRole('button', { name: 'Layers', exact: true }).click();
  await snowToggle.check();
  await expect.poll(() => page.evaluate(() => (globalThis as typeof globalThis & { appMap: {
    getLayoutProperty(id: string, property: string): unknown } }).appMap.getLayoutProperty('snow', 'visibility')))
    .toBe('visible');
  let initial: Awaited<ReturnType<typeof snowDemandSnapshot>>;
  try {
    await page.evaluate(() => new Promise<void>((resolve, reject) => {
      const map = (globalThis as typeof globalThis & { appMap: { once(type: string, callback: () => void): void;
        triggerRepaint(): void } }).appMap;
      const timeout = window.setTimeout(() => reject(new Error('Snow visibility did not produce a render.')), 10_000);
      map.once('render', () => { window.clearTimeout(timeout); resolve(); });
      map.triggerRepaint();
    }));
  } finally {
    initial = await snowDemandSnapshot(page);
    writeFileSync(evidencePath,
      `${JSON.stringify({ initial }, null, 2)}\n`);
  }
  const initialRevision = Number(new URL((initial.source as { tiles?: string[] })?.tiles?.[0] ?? 'https://invalid/')
    .searchParams.get('rev') ?? -1);
  await expect.poll(async () => (await telemetry(page)).entries.some(entry => entry.stage === 'snow-tile-generated'
    && entry.detail && Number(new URL(entry.detail).searchParams.get('rev')) === initialRevision), {
    timeout: 30_000, message: 'telemetry-enabled snow diagnostic must generate its initial revision',
  }).toBe(true);
  await expect.poll(() => page.evaluate(() => (globalThis as typeof globalThis & { appMap: {
    isSourceLoaded(id: string): boolean } }).appMap.isSourceLoaded('snow')), { timeout: 30_000 }).toBe(true);
  await page.evaluate(() => {
    const holder = globalThis as typeof globalThis & { integratedFrameProbe?: { sourceEvents: unknown[] } };
    if (holder.integratedFrameProbe) holder.integratedFrameProbe.sourceEvents.length = 0;
  });
  const before = await snowDemandSnapshot(page);
  const beforePixels = await stableSnowPixelRegions(page);
  const beforeProtocol = await page.evaluate(async () => {
    const diagnostic = (globalThis as typeof globalThis & { __MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__?: {
      tilePixel(lng: number, lat: number): Promise<unknown> } }).__MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__;
    return diagnostic?.tilePixel(-121.495, 46.904) ?? null;
  });
  await page.getByRole('button', { name: 'Open developer console' }).click();
  await page.getByLabel('Developer command').fill('snow add 50cm at -121.495,46.902 radius 250m');
  await page.getByLabel('Developer command').press('Enter');
  await expect(page.getByText(/Added 50 cm of fresh snow within 250 m .* across [\d,]+ terrain cells\./).last()).toBeVisible();
  const revision = await page.evaluate(() => {
    const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize?(): { tiles?: string[] } } | undefined } })
      .appMap.getSource('snow');
    return Number(new URL(source?.serialize?.().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
  });
  let after = await snowDemandSnapshot(page);
  let afterPixelsBeforeSourceReady: Record<string, unknown> = {};
  await expect.poll(async () => {
    afterPixelsBeforeSourceReady = await snowPixelRegions(page);
    writeFileSync(evidencePath, `${JSON.stringify({ before, after,
      beforePixels, afterPixelsBeforeSourceReady }, null, 2)}\n`);
    return (afterPixelsBeforeSourceReady.patch as { hash: string }).hash !== (beforePixels.patch as { hash: string }).hash
      && (afterPixelsBeforeSourceReady.control as { hash: string }).hash === (beforePixels.control as { hash: string }).hash;
  }, { timeout: 10_000, message: 'localized snow must change the known patch while preserving the control framebuffer' }).toBe(true);
  const afterProtocol = await page.evaluate(async () => {
    const diagnostic = (globalThis as typeof globalThis & { __MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__?: {
      tilePixel(lng: number, lat: number): Promise<unknown> } }).__MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__;
    return diagnostic?.tilePixel(-121.495, 46.904) ?? null;
  });
  writeFileSync(evidencePath,
    `${JSON.stringify({ before, after, beforePixels, afterPixelsBeforeSourceReady, beforeProtocol, afterProtocol }, null, 2)}\n`);
  await expect.poll(async () => (await telemetry(page)).entries.some(entry => entry.stage === 'snow-tile-generated'
    && entry.detail && Number(new URL(entry.detail).searchParams.get('rev')) === revision
    && snowTileContains(entry.detail, -121.495, 46.904)), {
    timeout: 30_000,
    message: 'a visible snow layer must demand a tile for its current revision',
  }).toBe(true);
  const generatedEntry = (await telemetry(page)).entries.find(entry => entry.stage === 'snow-tile-generated'
    && entry.detail && Number(new URL(entry.detail).searchParams.get('rev')) === revision
    && snowTileContains(entry.detail, -121.495, 46.904));
  expect(generatedEntry, 'visibility marker requires a generated revised tile covering the known patch').toBeTruthy();
  const generatedAt = generatedEntry!.at;
  const renderedAt = await page.evaluate(() => new Promise<number>(resolve => {
    const map = (globalThis as typeof globalThis & { appMap: { once(type: string, callback: () => void): void; triggerRepaint(): void } }).appMap;
    map.once('render', () => resolve(performance.now())); map.triggerRepaint();
  }));
  expect(renderedAt, 'the revised snow tile must be drawn after generation').toBeGreaterThanOrEqual(generatedAt);
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const map = (globalThis as typeof globalThis & { appMap: { loaded(): boolean;
      once(type: string, callback: () => void): void } }).appMap;
    if (map.loaded()) { resolve(); return; }
    const timeout = window.setTimeout(() => reject(new Error('revised snow source idle deadline')), 10_000);
    map.once('idle', () => { window.clearTimeout(timeout); resolve(); });
  }));
  after = await snowDemandSnapshot(page);
  const afterPixels = await snowPixelRegions(page);
  const visibleMarker = { sourceRevision: revision, generatedAt, framebufferRenderedAt:
    (afterPixels as { renderedAt: number }).renderedAt, patch: afterPixels.patch, control: afterPixels.control };
  expect(visibleMarker.framebufferRenderedAt).toBeGreaterThanOrEqual(visibleMarker.generatedAt);
  writeFileSync(evidencePath,
    `${JSON.stringify({ before, after, beforePixels, afterPixelsBeforeSourceReady,
      afterPixels, beforeProtocol, afterProtocol, visibleMarker }, null, 2)}\n`);
  expect(after.sourceLoaded).toBe(true);
  expect((afterPixelsBeforeSourceReady.patch as { hash: string }).hash).not.toBe((beforePixels.patch as { hash: string }).hash);
  expect((afterPixelsBeforeSourceReady.control as { hash: string }).hash).toBe((beforePixels.control as { hash: string }).hash);
  expect((afterProtocol as { rgba: number[] }).rgba).not.toEqual((beforeProtocol as { rgba: number[] }).rgba);
  await page.keyboard.press('Escape');
  const preStyleOwner = { checked: await snowToggle.isChecked(), visibility: await page.evaluate(() =>
    (globalThis as typeof globalThis & { appMap: { getLayoutProperty(id: string, property: string): unknown } })
      .appMap.getLayoutProperty('snow', 'visibility')) };
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const map = (globalThis as typeof globalThis & { appMap: { getStyle(): unknown;
      setStyle(style: unknown, options: { diff: boolean }): void; once(type: string, callback: () => void): void } }).appMap;
    const timeout = window.setTimeout(() => reject(new Error('style reload event deadline')), 30_000);
    map.once('style.load', () => { window.clearTimeout(timeout); resolve(); });
    const style = map.getStyle() as { glyphs?: string };
    map.setStyle({ version: 8, glyphs: style.glyphs, sources: {},
      layers: [{ id: 'mp-paper', type: 'background', paint: { 'background-color': '#e8e5dc' } }] }, { diff: false });
  }));
  let styleState: Record<string, unknown> = {};
  await expect.poll(async () => {
    styleState = await page.evaluate(() => {
      const map = (globalThis as typeof globalThis & { appMap: { getSource(id: string): unknown; getLayer(id: string): unknown;
        getLayoutProperty(id: string, property: string): unknown; isSourceLoaded(id: string): boolean } }).appMap;
      return { terrain: !!map.getSource('terrain-dem'), terrainLoaded: map.isSourceLoaded('terrain-dem'),
        snow: !!map.getSource('snow'), snowLoaded: map.isSourceLoaded('snow'), layer: !!map.getLayer('snow'),
        visibility: map.getLayer('snow') ? map.getLayoutProperty('snow', 'visibility') : null };
    });
    writeFileSync(evidencePath, `${JSON.stringify({ before, after, beforePixels, afterPixels,
      beforeProtocol, afterProtocol, styleState, ownerChecked: await snowToggle.isChecked() }, null, 2)}\n`);
    return styleState;
  }, { timeout: 30_000 }).toMatchObject({ terrain: true, terrainLoaded: true, snow: true,
    snowLoaded: true, layer: true, visibility: 'visible' });
  const stylePixels = await snowPixelRegions(page);
  const styleProtocol = await page.evaluate(async () => {
    const diagnostic = (globalThis as typeof globalThis & { __MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__?: {
      tilePixel(lng: number, lat: number): Promise<unknown> } }).__MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__;
    return diagnostic?.tilePixel(-121.495, 46.904) ?? null;
  });
  await snowToggle.uncheck();
  await expect.poll(() => page.evaluate(() => (globalThis as typeof globalThis & { appMap: {
    getLayoutProperty(id: string, property: string): unknown } }).appMap.getLayoutProperty('snow', 'visibility'))).toBe('none');
  await page.evaluate(() => new Promise<void>(resolve => {
    const map = (globalThis as typeof globalThis & { appMap: { once(type: string, callback: () => void): void;
      triggerRepaint(): void } }).appMap; map.once('render', resolve); map.triggerRepaint();
  }));
  const styleSnowOffPixels = await snowPixelRegions(page);
  await snowToggle.check();
  await expect.poll(() => page.evaluate(() => (globalThis as typeof globalThis & { appMap: {
    getLayoutProperty(id: string, property: string): unknown } }).appMap.getLayoutProperty('snow', 'visibility'))).toBe('visible');
  await page.evaluate(() => new Promise<void>(resolve => {
    const map = (globalThis as typeof globalThis & { appMap: { once(type: string, callback: () => void): void;
      triggerRepaint(): void } }).appMap; map.once('render', resolve); map.triggerRepaint();
  }));
  const styleSnowRestoredPixels = await snowPixelRegions(page);
  writeFileSync(evidencePath, `${JSON.stringify({ before, after, beforePixels, afterPixelsBeforeSourceReady, afterPixels,
    beforeProtocol, afterProtocol, visibleMarker, preStyleOwner, styleState, stylePixels, styleProtocol, styleSnowOffPixels,
    styleSnowRestoredPixels, ownerChecked: await snowToggle.isChecked() }, null, 2)}\n`);
  expect(stylePixels.snowSample).toEqual(afterPixels.snowSample);
  expect(styleProtocol).toEqual(afterProtocol);
  expect((styleSnowOffPixels.patch as { hash: string }).hash).not.toBe((stylePixels.patch as { hash: string }).hash);
  const rgbDistance = (left: Record<string, unknown>, right: Record<string, unknown>, region: 'patch' | 'control') => {
    const a = left[region] as { red: number; green: number; blue: number };
    const b = right[region] as { red: number; green: number; blue: number };
    return Math.abs(a.red - b.red) + Math.abs(a.green - b.green) + Math.abs(a.blue - b.blue);
  };
  expect(rgbDistance(styleSnowRestoredPixels, stylePixels, 'patch'))
    .toBeLessThan(rgbDistance(styleSnowOffPixels, stylePixels, 'patch'));
  expect(rgbDistance(styleSnowRestoredPixels, stylePixels, 'control'))
    .toBeLessThan(rgbDistance(styleSnowOffPixels, stylePixels, 'control'));
  if (process.env.INTEGRATED_POST_GRADE_DIAGNOSTIC === '1') {
    await exerciseGrading(page);
    await page.evaluate(() => (globalThis as typeof globalThis & { appMap: { jumpTo(options: unknown): void } })
      .appMap.jumpTo({ center: [-121.495, 46.904], zoom: 13, pitch: 0, bearing: 0 }));
    const postGradePath = path.join(evidenceDir, `${runId}.post-grade-readiness.json`);
    let lastObservation: Record<string, unknown> = {};
    try {
      await expect.poll(async () => {
        const sources = await page.evaluate(() => {
          const map = (globalThis as typeof globalThis & { appMap: { getSource(id: string): unknown;
            isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
          return { terrain: !!map.getSource('terrain-dem'), terrainLoaded: map.isSourceLoaded('terrain-dem'),
            snow: !!map.getSource('snow'), snowLoaded: map.isSourceLoaded('snow'), mapLoaded: map.loaded(),
            observedAt: performance.now() };
        });
        const entries = (await telemetry(page)).entries.filter(entry => /terrain|snow|source|map/.test(entry.stage));
        const framebuffer = await snowPixelRegions(page);
        lastObservation = { sources, framebuffer, telemetry: entries.slice(-100) };
        writeFileSync(postGradePath, `${JSON.stringify(lastObservation, null, 2)}\n`);
        return sources.terrain && sources.terrainLoaded && sources.snow && sources.snowLoaded && sources.mapLoaded;
      }, { timeout: 30_000, message: 'post-grade diagnostic readiness' }).toBe(true);
    } finally {
      writeFileSync(postGradePath, `${JSON.stringify(lastObservation, null, 2)}\n`);
    }
  }
}

function glBackend(renderer: string): string | null {
  const match = renderer.match(/(D3D12|D3D11|Vulkan|Metal|OpenGL|SwiftShader)/i);
  return match?.[1] ?? null;
}

function snowTileContains(url: string, lng: number, lat: number): boolean {
  const match = url.match(/\/snow\/(\d+)\/(\d+)\/(\d+)/);
  if (!match) return false;
  const z = Number(match[1]), x = Number(match[2]), y = Number(match[3]), scale = 2 ** z;
  const expectedX = Math.floor((lng + 180) / 360 * scale);
  const expectedY = Math.floor((1 - Math.asinh(Math.tan(lat * Math.PI / 180)) / Math.PI) / 2 * scale);
  return x === expectedX && y === expectedY;
}

async function exerciseGrading(page: Page): Promise<number> {
  const center: [number, number] = jacksonFixture ? [-71.165, 44.166] : [-121.495, 46.902];
  const west: [number, number] = jacksonFixture ? [-71.166, 44.166] : [-121.496, 46.902];
  const east: [number, number] = jacksonFixture ? [-71.164, 44.166] : [-121.494, 46.902];
  const points = await page.evaluate(({ center, west, east }) => {
    const map = (globalThis as typeof globalThis & { appMap: { jumpTo(options: unknown): void;
      project(value: [number, number]): { x: number; y: number }; getCanvas(): HTMLCanvasElement } }).appMap;
    map.jumpTo({ center, zoom: 16, pitch: 0, bearing: 0 });
    const rect = map.getCanvas().getBoundingClientRect(), project = (value: [number, number]) => {
      const point = map.project(value); return { x: rect.left + point.x, y: rect.top + point.y };
    };
    return { west: project(west), east: project(east) };
  }, { center, west, east });
  await page.getByRole('button', { name: 'Toolbox', exact: true }).click({ timeout: 10_000 });
  await page.getByRole('tab', { name: 'Infrastructure', exact: true }).click({ timeout: 10_000 });
  await page.getByRole('button', { name: /Build road/ }).click({ timeout: 10_000 });
  await page.mouse.click(points.west.x, points.west.y); await page.mouse.click(points.east.x, points.east.y);
  await page.getByRole('button', { name: 'Finish route', exact: true }).click();
  await expect(page.getByText('Review road', { exact: true })).toBeVisible();
  await page.locator('.infrastructure-panel .lift-name-input').fill('Integrated benchmark grade');
  const started = Date.now(), build = page.getByRole('button', { name: 'Build road', exact: true });
  await expect(build).toBeEnabled({ timeout: 30_000 }); await build.click();
  await expect(page.locator('.road-detail')).toContainText('Integrated benchmark grade', { timeout: 30_000 });
  const durationMs = Date.now() - started;
  await page.locator('.road-detail').getByRole('button', { name: 'Close', exact: true }).click();
  return durationMs;
}

async function snowPixelRegions(page: Page): Promise<Record<string, unknown>> {
  const patchCoordinate: [number, number] = jacksonFixture ? [-71.165, 44.166] : [-121.495, 46.904];
  const controlCoordinate: [number, number] = jacksonFixture ? [-71.161, 44.166] : [-121.499, 46.905];
  return page.evaluate(async ({ patchCoordinate, controlCoordinate }) => {
    const map = (globalThis as typeof globalThis & { appMap: { getCanvas(): HTMLCanvasElement;
      project(value: [number, number]): { x: number; y: number }; getCenter(): { lng: number; lat: number };
      getZoom(): number; getPitch(): number; getLayoutProperty(id: string, property: string): unknown;
      queryTerrainElevation(value: { lng: number; lat: number }): number | null;
      once(type: string, callback: () => void): void; triggerRepaint(): void } }).appMap;
    const canvas = map.getCanvas(), rect = canvas.getBoundingClientRect(), dpr = canvas.width / rect.width;
    const regions = await new Promise<{ patch: Record<string, unknown>; control: Record<string, unknown>; renderedAt: number }>((resolve, reject) => {
    const capture = (position: { x: number; y: number }, size: number) => {
      const radius = Math.floor(size / 2);
      const x0 = Math.round(position.x) - radius, y0 = Math.round(position.y) - radius;
      if (x0 < 0 || y0 < 0 || x0 + size > rect.width || y0 + size > rect.height) {
        throw new Error(`Snow evidence point is outside the physical map canvas: ${JSON.stringify(position)}`);
      }
      const width = Math.round(size * dpr), height = Math.round(size * dpr);
      const pixels = new Uint8Array(width * height * 4);
      const gl = canvas.getContext('webgl2') ?? canvas.getContext('webgl');
      if (!gl) throw new Error('Map WebGL context is unavailable during render.');
      gl.readPixels(Math.round(x0 * dpr), canvas.height - Math.round((y0 + size) * dpr), width, height,
        gl.RGBA, gl.UNSIGNED_BYTE, pixels);
      let hash = 0x811c9dc5, red = 0, green = 0, blue = 0;
      for (let index = 0; index < pixels.length; index++) {
        hash ^= pixels[index]!; hash = Math.imul(hash, 0x01000193) >>> 0;
        if (index % 4 === 0) red += pixels[index]!;
        else if (index % 4 === 1) green += pixels[index]!;
        else if (index % 4 === 2) blue += pixels[index]!;
      }
      return { x: x0, y: y0, width, height,
        hash: hash.toString(16).padStart(8, '0'), red, green, blue };
    };
    const patch = map.project(patchCoordinate), control = map.project(controlCoordinate);
      map.once('render', () => {
        try { resolve({ patch: capture(patch, 201), control: capture(control, 101), renderedAt: performance.now() }); }
        catch (error) { reject(error); }
      });
      map.triggerRepaint();
    });
    const center = map.getCenter();
    const snowSample = (globalThis as typeof globalThis & { __MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__?: {
      sample(lng: number, lat: number): unknown } }).__MOUNTAIN_PLANNER_SNOW_DIAGNOSTIC__?.sample(
        patchCoordinate[0], patchCoordinate[1]) ?? null;
    return { ...regions, snowSample, projected: { patch: map.project(patchCoordinate), control: map.project(controlCoordinate) }, geometry: {
      width: canvas.width, height: canvas.height, center, zoom: map.getZoom(),
      pitch: map.getPitch(), elevation: map.queryTerrainElevation(center),
      snowVisibility: map.getLayoutProperty('snow', 'visibility') } };
  }, { patchCoordinate, controlCoordinate });
}

async function stableSnowPixelRegions(page: Page): Promise<Record<string, unknown>> {
  let previous: Record<string, unknown> | null = null;
  let stableFrames = 0;
  for (let attempt = 0; attempt < 100; attempt++) {
    const current = await snowPixelRegions(page);
    const hashes = (value: Record<string, unknown>) => [
      (value.patch as { hash: string }).hash, (value.control as { hash: string }).hash];
    stableFrames = previous && hashes(current).every((hash, index) => hash === hashes(previous!)[index])
      ? stableFrames + 1 : 0;
    if (stableFrames >= 2) return current;
    previous = current;
    await page.waitForTimeout(100);
  }
  throw new Error('Map framebuffer did not produce two consecutive stable snow/control samples.');
}

async function exerciseInteractions(page: Page): Promise<{ pauseMs: number; cancelMs: number; snowMs: number;
  snowAffectedCells: number; snowSourceRevision: number; snowPixels: Record<string, unknown> }> {
  const play = page.getByRole('button', { name: 'Play game clock', exact: true });
  if (await play.isVisible()) { await expect(play).toBeEnabled({ timeout: 30_000 }); await play.click(); }
  const pause = page.getByRole('button', { name: 'Pause game clock', exact: true });
  const pauseStarted = Date.now(); await pause.click(); await expect(play).toBeVisible(); const pauseMs = Date.now() - pauseStarted;
  const snowToggle = page.getByRole('checkbox', { name: 'Snow', exact: true });
  if (!await snowToggle.isVisible()) await page.getByRole('button', { name: 'Layers', exact: true }).click();
  await snowToggle.check();
  const snowRevisionBefore = await page.evaluate(() => {
    const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
      tiles?: string[] } } | undefined } }).appMap.getSource('snow');
    return Number(new URL(source?.serialize().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
  });
  const patchCoordinate: readonly [number, number] = jacksonFixture ? [-71.165, 44.166] : [-121.495, 46.904];
  if (!qualification && process.env.INTEGRATED_TELEMETRY !== '0') await expect.poll(async () =>
    (await telemetry(page)).entries.some(entry => entry.stage === 'snow-tile-generated' && entry.detail
      && Number(new URL(entry.detail).searchParams.get('rev')) === snowRevisionBefore
      && snowTileContains(entry.detail, ...patchCoordinate)), {
    timeout: 30_000, message: 'initial current-revision snow tile covering the patch must generate before baseline capture',
  }).toBe(true);
  await expect.poll(() => page.evaluate(() => (globalThis as typeof globalThis & { appMap: {
    isSourceLoaded(id: string): boolean } }).appMap.isSourceLoaded('snow')), { timeout: 30_000 }).toBe(true);
  const evidenceDir = path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark');
  mkdirSync(evidenceDir, { recursive: true });
  const evidencePath = path.join(evidenceDir, `${runId}.snow-pixels.json`);
  const beforePixels = qualification ? {} : await stableSnowPixelRegions(page);
  writeFileSync(evidencePath, `${JSON.stringify({ revisionBefore: snowRevisionBefore, before: beforePixels }, null, 2)}\n`);
  const snowCells = await page.evaluate(() => Number((globalThis as typeof globalThis & {
    appSaveState?: { snowCells?: number } }).appSaveState?.snowCells ?? 0));
  await page.getByRole('button', { name: 'Open developer console' }).click();
  const snowCentimeters = qualification ? 1 : 50;
  const localizedSnow = jacksonFixture ? `snow add ${snowCentimeters}cm at -71.165,44.166 radius 250m`
    : `snow add ${snowCentimeters}cm at -121.495,46.902 radius 250m`;
  const snowStarted = Date.now(); await page.getByLabel('Developer command').fill(localizedSnow);
  await page.getByLabel('Developer command').press('Enter');
  const snowCompletion = page.getByText(new RegExp(`Added ${snowCentimeters} cm of fresh snow within 250 m .* across [\\d,]+ terrain cells\\.`)).last();
  await expect(snowCompletion).toBeVisible();
  const affectedCells = snowAffectedCells(await snowCompletion.textContent() ?? '');
  expect(affectedCells).toBeGreaterThan(0);
  expect(affectedCells).toBeLessThan(snowCells);
  await expect.poll(() => page.evaluate(() => {
    const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
      tiles?: string[] } } | undefined } }).appMap.getSource('snow');
    return Number(new URL(source?.serialize().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
  })).toBeGreaterThan(snowRevisionBefore);
  const snowRevisionAfter = await page.evaluate(() => {
    const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
      tiles?: string[] } } | undefined } }).appMap.getSource('snow');
    return Number(new URL(source?.serialize().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
  });
  const snowMs = Date.now() - snowStarted;
  await page.evaluate(() => new Promise<void>(resolve => {
    const map = (globalThis as typeof globalThis & { appMap: { once(type: string, callback: () => void): void;
      triggerRepaint(): void } }).appMap; map.once('render', resolve); map.triggerRepaint();
  }));
  expect(snowRevisionAfter).toBeGreaterThan(snowRevisionBefore);
  let localizedPixels: Record<string, unknown> = {};
  if (!qualification) await expect.poll(async () => {
    localizedPixels = await snowPixelRegions(page);
    writeFileSync(evidencePath, `${JSON.stringify({ revisionBefore: snowRevisionBefore,
      revisionAfter: snowRevisionAfter, before: beforePixels, localized: localizedPixels }, null, 2)}\n`);
    return (localizedPixels.patch as { hash: string }).hash !== (beforePixels.patch as { hash: string }).hash
      && (localizedPixels.control as { hash: string }).hash === (beforePixels.control as { hash: string }).hash;
  }, { timeout: 30_000, message: 'localized snow revision must visibly change only the known framebuffer patch' }).toBe(true);
  await page.keyboard.press('Escape');
  await expect.poll(() => page.evaluate(() => (globalThis as typeof globalThis & { appMap: {
    isSourceLoaded(id: string): boolean } }).appMap.isSourceLoaded('snow')), { timeout: 30_000 }).toBe(true);
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const map = (globalThis as typeof globalThis & { appMap: { loaded(): boolean;
      once(type: string, callback: () => void): void } }).appMap;
    if (map.loaded()) { resolve(); return; }
    const timeout = window.setTimeout(() => reject(new Error('snow source idle deadline')), 10_000);
    map.once('idle', () => { window.clearTimeout(timeout); resolve(); });
  }));
  const afterPixels = qualification ? {} : await snowPixelRegions(page);
  writeFileSync(evidencePath, `${JSON.stringify({ revisionBefore: snowRevisionBefore,
    revisionAfter: snowRevisionAfter, before: beforePixels, localized: localizedPixels, after: afterPixels }, null, 2)}\n`);
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const map = (globalThis as typeof globalThis & { appMap: { getStyle(): unknown;
      setStyle(style: unknown, options: { diff: boolean }): void; once(type: string, callback: () => void): void } }).appMap;
    const timeout = window.setTimeout(() => reject(new Error('style reload event deadline')), 30_000);
    map.once('style.load', () => { window.clearTimeout(timeout); resolve(); });
    const style = map.getStyle() as { glyphs?: string };
    map.setStyle({ version: 8, glyphs: style.glyphs, sources: {},
      layers: [{ id: 'mp-paper', type: 'background', paint: { 'background-color': '#e8e5dc' } }] }, { diff: false });
  }));
  let styleState: Record<string, unknown> = {};
  await expect.poll(async () => {
    styleState = await page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { getSource(id: string): unknown; getLayer(id: string): unknown;
      getLayoutProperty(id: string, property: string): unknown; isSourceLoaded(id: string): boolean } }).appMap;
    return { terrain: !!map.getSource('terrain-dem'), terrainLoaded: map.isSourceLoaded('terrain-dem'),
      snow: !!map.getSource('snow'), snowLoaded: map.isSourceLoaded('snow'), layer: !!map.getLayer('snow'),
      visibility: map.getLayer('snow') ? map.getLayoutProperty('snow', 'visibility') : null };
    });
    writeFileSync(evidencePath, `${JSON.stringify({ revisionBefore: snowRevisionBefore,
      revisionAfter: snowRevisionAfter, before: beforePixels, after: afterPixels, styleState }, null, 2)}\n`);
    return styleState;
  }, { timeout: 30_000 }).toMatchObject({ terrain: true, terrainLoaded: true, snow: true,
    snowLoaded: true, layer: true, visibility: 'visible' });
  const styleReloadPixels = qualification ? {} : await snowPixelRegions(page);
  const snowPixels = { revisionBefore: snowRevisionBefore, revisionAfter: snowRevisionAfter,
    before: beforePixels, localized: localizedPixels, after: afterPixels, styleReload: styleReloadPixels };
  writeFileSync(evidencePath, `${JSON.stringify(snowPixels, null, 2)}\n`);
  if (!qualification) {
    expect((localizedPixels.patch as { hash: string }).hash).not.toBe((beforePixels.patch as { hash: string }).hash);
    expect((localizedPixels.control as { hash: string }).hash).toBe((beforePixels.control as { hash: string }).hash);
    expect(styleReloadPixels.snowSample).toEqual(localizedPixels.snowSample);
  }
  await page.getByRole('button', { name: 'Date and time: simulation advances' }).click();
  await page.getByLabel('Advance to').selectOption('season');
  const cancellationPublicationStart = (await workerEntries(page, 'dualClock.worker'))
    .flatMap(entry => entry.publications ?? []).length;
  const cancelReady = page.evaluate(() => new Promise<number>((resolve, reject) => {
    const deadline = window.setTimeout(() => { observer.disconnect(); reject(new Error('Cancel control did not appear.')); }, 10_000);
    const observer = new MutationObserver(() => {
      const button = [...document.querySelectorAll('button')].find(candidate => candidate.textContent?.trim() === 'Cancel');
      const progress = document.querySelector('[aria-label="Simulation advance progress"]');
      if (!button || !progress?.hasAttribute('value')) return;
      window.clearTimeout(deadline); observer.disconnect(); (button as HTMLButtonElement).click(); resolve(performance.now());
    });
    observer.observe(document.body, { childList: true, subtree: true, characterData: true, attributes: true });
  }));
  const cancelStarted = Date.now();
  await page.getByRole('button', { name: 'Simulate Season', exact: true }).click();
  const cancelClickedAt = await cancelReady;
  await expect(page.getByText(/^Advance cancelled\./)).toBeVisible(); const cancellationWorkflowObservedMs = Date.now() - cancelStarted;
  let cancellationPublications: Awaited<ReturnType<typeof workerEntries>> = [];
  await expect.poll(async () => {
    cancellationPublications = await workerEntries(page, 'dualClock.worker');
    return cancellationPublications.flatMap(entry => entry.publications ?? []).slice(cancellationPublicationStart)
      .some(publication => publication.requestType === 'cancel' && publication.busy !== true);
  }, { timeout: 10_000, message: 'shipped dual-clock worker must acknowledge this cancellation request' }).toBe(true);
  const cancellationEvidence = cancellationPublications.flatMap(entry => entry.publications ?? [])
    .slice(cancellationPublicationStart);
  const runningPublicationIndex = cancellationEvidence.findIndex(publication =>
    publication.requestType === 'skip' && publication.busy === true);
  const cancelPublicationIndex = cancellationEvidence.findIndex(publication =>
    publication.requestType === 'cancel' && publication.busy !== true);
  expect(runningPublicationIndex, 'this advance must reach a running worker publication before cancellation').toBeGreaterThanOrEqual(0);
  expect(cancelPublicationIndex).toBeGreaterThan(runningPublicationIndex);
  const cancelPublication = cancellationEvidence[cancelPublicationIndex]!;
  const cancellationTelemetry = (await telemetry(page)).entries;
  const cancelAcknowledgement = cancellationTelemetry.find(entry => entry.stage === 'dual-worker-receive'
    && entry.requestId === cancelPublication.id && entry.generation === cancelPublication.generation
    && entry.operationGeneration === cancelPublication.operationGeneration);
  expect(cancelAcknowledgement, 'cancel acknowledgement must retain its renderer-clock timestamp').toBeTruthy();
  const cancelMs = cancelAcknowledgement!.at - cancelClickedAt;
  const postCancelCommits = cancellationTelemetry.filter(entry => entry.stage === 'dual-react-commit'
    && entry.at >= cancelClickedAt && entry.generation === cancelPublication.generation);
  expect(postCancelCommits.every(entry => (entry.operationGeneration ?? -1) >= cancelPublication.operationGeneration),
    'stale pre-cancel worker responses must not commit after cancellation').toBe(true);
  writeFileSync(path.join(evidenceDir, `${runId}.cancellation.json`), `${JSON.stringify({
    cancellationPublicationStart, cancelClickedAt, cancellationWorkflowObservedMs, workerCancellationMs: cancelMs, runningPublicationIndex,
    cancelPublication, cancellationEvidence, postCancelCommits }, null, 2)}\n`);
  await page.keyboard.press('Escape');
  return { pauseMs, cancelMs, snowMs, snowAffectedCells: affectedCells,
    snowSourceRevision: snowRevisionAfter, snowPixels };
}

async function exerciseScenarioWorkflow(page: Page): Promise<void> {
  if (workload === 'aggregate') {
    await expect.poll(async () => workerEntries(page, 'dualClock.worker').then(entries => {
      const latest = entries.flatMap(entry => entry.publications ?? []).reverse().find(publication =>
        publication.movementCount !== undefined);
      return latest?.representatives?.length ?? 0;
    })).toBe(0);
  }
  if (workflow === 'interactive') {
    await page.evaluate(async () => {
      const map = (globalThis as typeof globalThis & { appMap: { getCenter(): unknown; getZoom(): number;
        getBearing(): number; getPitch(): number; easeTo(options: Record<string, unknown>): void;
        once(type: string, listener: () => void): void } }).appMap;
      const move = (options: Record<string, unknown>) => new Promise<void>(resolve => {
        map.once('moveend', resolve); map.easeTo({ duration: 150, ...options });
      });
      await move({ zoom: map.getZoom() + .5 });
      await move({ bearing: map.getBearing() + 20, pitch: Math.min(70, map.getPitch() + 10) });
      await move({ center: map.getCenter(), zoom: map.getZoom() - .25 });
    });
    let hit: { x: number; y: number } | null = null;
    await expect.poll(async () => {
      hit = await page.evaluate(() => {
        const map = (globalThis as typeof globalThis & { appMap: { getCanvas(): HTMLCanvasElement;
        getLayer(id: string): { implementation?: Record<string, unknown> } | undefined } }).appMap;
        const layer = map.getLayer('guest-simulation-dots')?.implementation;
        const xs = layer?.hitXs as ArrayLike<number> | undefined, ys = layer?.hitYs as ArrayLike<number> | undefined;
        const canvas = map.getCanvas(), rect = canvas.getBoundingClientRect();
        return xs?.length && ys?.length ? { x: rect.left + Number(xs[0]), y: rect.top + Number(ys[0]) } : null;
      }); return hit !== null;
    }, { message: 'interactive workflow needs a visible GPU guest hit target' }).toBe(true);
    await page.mouse.move(hit!.x - 30, hit!.y - 30); await page.mouse.move(hit!.x, hit!.y, { steps: 8 });
    await page.mouse.click(hit!.x, hit!.y);
    await expect(page.locator('[aria-label^="Selected guest "]')).toBeVisible();
    await page.getByRole('button', { name: 'Follow', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stop following', exact: true })).toBeVisible();
    await expect(page.getByLabel('Snow layer controls')).toBeVisible();
    await page.locator('.maplibregl-canvas').click({ position: { x: 40, y: 40 } });
  }
  if (workflow === 'recovery') {
    for (const speed of [4, 8, 4] as const) {
      const control = page.getByRole('button', { name: `${speed}× simulation speed`, exact: true });
      await control.click(); await expect(control).toHaveAttribute('aria-pressed', 'true');
    }
    await page.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
      getStyle(): unknown; setStyle(style: unknown): void } }).appMap; map.setStyle(map.getStyle()); });
    await expect.poll(() => page.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
      getSource(id: string): unknown; loaded(): boolean } }).appMap;
      return !!map.getSource('terrain-dem') && !!map.getSource('snow') && map.loaded(); })).toBe(true);
  }
  if (workflow === 'visibility-wide') await page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { getZoom(): number; setZoom(zoom: number): void;
      triggerRepaint(): void } }).appMap; map.setZoom(map.getZoom() + 3); map.triggerRepaint();
  });
  if (workflow === 'visibility-near') await page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
      bounds?: [number, number, number, number] } }; fitBounds(bounds: [[number, number], [number, number]],
      options: Record<string, unknown>): void; triggerRepaint(): void } }).appMap;
    const bounds = map.getSource('terrain-dem').serialize().bounds;
    if (!bounds) throw new Error('Terrain bounds are unavailable for the near visibility camera.');
    map.fitBounds([[bounds[0], bounds[1]], [bounds[2], bounds[3]]], { padding: 24, pitch: 0, bearing: 0, duration: 0 });
    map.triggerRepaint();
  });
}

const presentationCounts = (page: Page) => page.evaluate(() => {
  const map = (globalThis as typeof globalThis & { appMap: {
    getLayer(id: string): { implementation?: Record<string, unknown> } | undefined } }).appMap;
  const layer = map.getLayer('guest-simulation-dots')?.implementation;
  return { drawn: Number(layer?.count ?? 0), visible: Number(layer?.hitCount ?? 0) };
});

test('integrated App to MapLibre diagnostic or qualification workflow', async ({ page, baseURL }) => {
  failureStage = 'launch';
  test.setTimeout(snowDemandDiagnostic ? 240_000
    : Math.max(300_000, warmupMs + measurementMs + (qualification ? 420_000 : 240_000)));
  const enabled = process.env.INTEGRATED_TELEMETRY !== '0';
  if (qualification) {
    expect(Number(process.env.INTEGRATED_TARGET ?? target)).toBe(target);
    expect(Number(process.env.INTEGRATED_REQUESTED_SPEED ?? requestedSpeed)).toBe(requestedSpeed);
  }
  await installInstrumentation(page, enabled);
  const pageErrors: string[] = []; page.on('pageerror', error => pageErrors.push(error.message));
  const externalRequests: string[] = [], requiredGlyphRequests: string[] = [];
  failureStage = 'fixture-installation';
  let fixture: Awaited<ReturnType<typeof loadIntegratedBenchmarkFixture>> | null = null;
  if (jacksonFixture) fixture = await runIntegratedStage(
    path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark'), runId,
    'fixture-installation', { fixture: 'jackson', operation: 'load' }, () => loadIntegratedBenchmarkFixture(
      path.resolve(process.env.INTEGRATED_FIXTURE_ROOT!), target as 0 | 1000 | 3000));
  const glyphs = new Map<string, Buffer>();
  if (fixture) for (const asset of fixture.manifest.requiredPresentationAssets) if (asset.role.startsWith('glyph:')) {
    const source = asset.sourceUrl; if (source) glyphs.set(source, readFileSync(path.join(process.env.INTEGRATED_FIXTURE_ROOT!, asset.path)));
  }
  await page.route('**/*', async route => {
    const url = route.request().url(), parsed = new URL(url);
    if (glyphs.has(url)) { requiredGlyphRequests.push(url); await route.fulfill({ status: 200,
      contentType: 'application/x-protobuf', body: glyphs.get(url)! }); return; }
    if (parsed.protocol === 'http:' || parsed.protocol === 'https:') {
      if (parsed.origin !== new URL(baseURL!).origin) { externalRequests.push(url); await route.abort('blockedbyclient'); return; }
    }
    await route.continue();
  });
  if (fixture) await installIntegratedBenchmarkScenario(page, fixture, target as 0 | 1000 | 3000, runId, requestedSpeed);
  else {
    const diagnostic = diagnosticPackage();
    await page.addInitScript(({ key, value }) => { (globalThis as typeof globalThis & Record<string, unknown>)[key] = value; },
      { key: scenarioGlobal, value: diagnostic.scenario });
  }
  await page.goto(new URL(appQuery, baseURL!).toString(), { waitUntil: 'load' });
  if (fixture) await runIntegratedStage(
    path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark'), runId,
    'fixture-installation', { fixture: 'jackson', operation: 'persist' }, () =>
      persistIntegratedBenchmarkFixture(page, fixture!, target as 0 | 1000 | 3000));
  else await seedDiagnostic(page, diagnosticPackage());
  if (jacksonFixture) await page.evaluate(quality => localStorage.setItem('skiapp:settings', JSON.stringify({ renderQuality: quality })),
    selectedScenario!.profile.toLowerCase());
  failureStage = 'boot';
  await restoreSelectedCheckpoint(page, 180_000);
  failureStage = 'source-readiness';
  await waitForMapSourceReadiness(page, jacksonFixture, 45_000);
  if (snowDemandDiagnostic) {
    expect(qualification, 'snow demand capture is limited to the deterministic diagnostic fixture').toBe(false);
    failureStage = 'interaction';
    await runSnowDemandDiagnostic(page);
    return;
  }
  expect(pageErrors).toEqual([]);
  failureStage = 'workload-readiness';
  await expect.poll(() => page.evaluate(({ key, workload, speed }) => {
    const scenario = (globalThis as typeof globalThis & Record<string, unknown>)[key] as
      { workload?: string; requestedSpeed?: number } | undefined;
    return scenario?.workload === workload && scenario.requestedSpeed === speed;
  }, { key: scenarioGlobal, workload, speed: requestedSpeed })).toBe(true);
  const installedScenario = await page.evaluate(key =>
    (globalThis as typeof globalThis & Record<string, unknown>)[key] as { seed: string }, scenarioGlobal);
  await expect.poll(async () => workerEntries(page, 'dualClock.worker').then(entries => entries.some(entry =>
    entry.publications?.some(publication => publication.type === 'publication')))).toBe(true);
  const expectedTarget = fixture ? target : 32;
  if (expectedTarget > 0 && workload !== 'aggregate') await expect.poll(async () => workerEntries(page, 'dualClock.worker').then(entries => {
    const publications = entries.flatMap(entry => entry.publications ?? []), latest = publications.reverse().find(publication =>
      publication.movementCount !== undefined);
    return latest?.movementCount ?? 0;
  })).toBeGreaterThanOrEqual(Math.ceil(expectedTarget * .95));
  if (!jacksonFixture) {
    const latestMovementPublication = async () => workerEntries(page, 'dualClock.worker').then(entries => {
      const latest = entries.flatMap(entry => entry.publications ?? []).reverse().find(publication =>
        publication.movementCount !== undefined);
      if (!latest) throw new Error('Diagnostic mode transition requires a movement publication.');
      return latest;
    });
    const beforeAggregate = await latestMovementPublication();
    const aggregateSpeed = page.getByRole('button', { name: '8× simulation speed', exact: true });
    await aggregateSpeed.click(); await expect(aggregateSpeed).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(async () => { const latest = await latestMovementPublication();
      return latest.id > beforeAggregate.id && latest.generation === beforeAggregate.generation
        && latest.committedRevision >= beforeAggregate.committedRevision
        && Number((latest.flow as { active?: number } | undefined)?.active ?? -1) >= 31
        && latest.movementCount === 0; }).toBe(true);
    const pausedAggregate = await latestMovementPublication();
    const play = page.getByRole('button', { name: 'Play game clock', exact: true });
    await expect(play).toBeEnabled(); await play.click();
    await expect.poll(async () => { const latest = await latestMovementPublication();
      return Number(latest.macroSecond) > Number(pausedAggregate.macroSecond) && latest.movementCount === 0;
    }).toBe(true);
    await page.getByRole('button', { name: 'Pause game clock', exact: true }).click(); await expect(play).toBeVisible();
    const beforeDetailed = await latestMovementPublication();
    const detailedSpeed = page.getByRole('button', { name: '4× simulation speed', exact: true });
    await detailedSpeed.click(); await expect(detailedSpeed).toHaveAttribute('aria-pressed', 'true');
    await expect.poll(async () => { const latest = await latestMovementPublication();
      return latest.id > beforeDetailed.id && latest.generation === beforeDetailed.generation
        && latest.committedRevision >= beforeDetailed.committedRevision && latest.movementCount === 32;
    }).toBe(true);
    const pausedDetailed = await latestMovementPublication(); await play.click();
    await expect.poll(async () => { const latest = await latestMovementPublication();
      return Number(latest.macroSecond) > Number(pausedDetailed.macroSecond) && latest.movementCount === 32;
    }).toBe(true);
  }
  failureStage = 'interaction';
  const interactions = await exerciseInteractions(page);
  if (jacksonFixture) {
    await expect.poll(() => page.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
      getSource(id: string): unknown; isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
      return !!map.getSource('terrain-dem') && map.isSourceLoaded('terrain-dem') && !!map.getSource('snow')
        && map.isSourceLoaded('snow') && !!map.getSource('satellite') && map.isSourceLoaded('satellite') && map.loaded();
    }), { timeout: 30_000 }).toBe(true);
    if ((process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'cold') {
      const cdp = await page.context().newCDPSession(page); await cdp.send('Network.clearBrowserCache'); await cdp.detach();
    }
  }
  await restoreSelectedCheckpoint(page, qualification ? 180_000 : 30_000);
  const resetPlay = page.getByRole('button', { name: 'Play game clock', exact: true });
  await expect(resetPlay).toBeEnabled({ timeout: 30_000 });
  const resetSnow = page.getByRole('checkbox', { name: 'Snow', exact: true });
  if (!await resetSnow.isVisible()) await page.getByRole('button', { name: 'Layers', exact: true }).click();
  await resetSnow.check();
  await expect.poll(() => page.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
    getSource(id: string): unknown; isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
    return !!map.getSource('terrain-dem') && map.isSourceLoaded('terrain-dem') && !!map.getSource('snow')
      && map.isSourceLoaded('snow') && map.loaded(); }), { timeout: 30_000 }).toBe(true);
  if (workflow !== 'interactive') await exerciseScenarioWorkflow(page);
  const resultsDir = path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark');
  const stopDiagnosticProfile = heavyDiagnosticRequested()
    ? await startIntegratedDiagnosticProfile(page, { outputDir: resultsDir, runId }) : null;
  const requestedSpeedControl = page.getByRole('button', { name: `${requestedSpeed}× simulation speed`, exact: true });
  await requestedSpeedControl.click(); await expect(requestedSpeedControl).toHaveAttribute('aria-pressed', 'true');
  const playAfterInteractions = page.getByRole('button', { name: 'Play game clock', exact: true });
  if (await playAfterInteractions.isVisible()) await playAfterInteractions.click();
  failureStage = 'measurement';
  await startSimulationEvidence(page);
  await page.waitForTimeout(warmupMs);
  const measurementPublicationStart = await simulationEvidenceCount(page);
  const activeWorkersBefore = await workerEntries(page, '').then(entries =>
    entries.filter(entry => entry.terminationCount === 0).length);
  await startFrameProbe(page);
  const measurementStarted = Date.now();
  if (workflow === 'interactive') await exerciseScenarioWorkflow(page);
  const frames = await finishFrameProbe(page, Math.max(0, measurementMs - (Date.now() - measurementStarted)) + 25);
  const sampledPublications = await finishSimulationEvidence(page);
  const diagnosticProfile = stopDiagnosticProfile ? await stopDiagnosticProfile() : undefined;
  const activeWorkersAfter = await workerEntries(page, '').then(entries =>
    entries.filter(entry => entry.terminationCount === 0).length);
  const capturedPublications = await workerEntries(page, 'dualClock.worker').then(entries =>
    entries.flatMap(entry => entry.publications ?? []));
  const measuredResponse = (publication: typeof sampledPublications[number]) => Number.isFinite(publication.macroSecond)
    && Number.isFinite(publication.microSecond)
    && Number.isFinite((publication.flow as { active?: number } | undefined)?.active)
    && Number.isFinite(publication.movementCount);
  const occupancyPublications = sampledPublications.filter(measuredResponse);
  const measurementPublications = sampledPublications.slice(measurementPublicationStart).filter(measuredResponse);
  const measurementAcknowledgements = capturedPublications.filter(publication =>
    !measuredResponse(publication));
  expect(measurementPublications.length).toBeGreaterThan(0);
  const activeOccupancy = occupancyPublications.map(publication =>
    Number((publication.flow as { active?: number } | undefined)?.active ?? Number.NaN));
  expect(activeOccupancy.every(Number.isFinite)).toBe(true);
  const activeOccupancyMin = Math.min(...activeOccupancy);
  if (workload === 'empty') expect(activeOccupancyMin).toBe(0);
  else expect(activeOccupancyMin).toBeGreaterThanOrEqual(Math.ceil(expectedTarget * .95));
  if (workload !== 'aggregate' && expectedTarget > 0) expect(occupancyPublications.every(publication =>
    (publication.movementCount ?? 0) >= Math.ceil(expectedTarget * .95))).toBe(true);
  if (workload === 'aggregate') expect(occupancyPublications.every(publication =>
    (publication.movementCount ?? 0) === 0)).toBe(true);
  const occupancyMacroSecondsAdvanced = Number(occupancyPublications.at(-1)!.macroSecond)
    - Number(occupancyPublications[0]!.macroSecond);
  const occupancyMicroSecondsAdvanced = Number(occupancyPublications.at(-1)!.microSecond)
    - Number(occupancyPublications[0]!.microSecond);
  const firstPublication = measurementPublications[0]!, lastPublication = measurementPublications.at(-1)!;
  const macroSecondsAdvanced = Number(lastPublication.macroSecond) - Number(firstPublication.macroSecond);
  const microSecondsAdvanced = Number(lastPublication.microSecond) - Number(firstPublication.microSecond);
  expect(macroSecondsAdvanced).toBeGreaterThan(0); expect(microSecondsAdvanced).toBeGreaterThan(0);
  const backlogSamples = measurementPublications.flatMap(publication => publication.requestedTargetMs !== undefined
    && publication.at ? [Math.max(0, (publication.requestedTargetMs - Date.parse(publication.at)) / 1000)] : []);
  expect(backlogSamples.length).toBeGreaterThan(0);
  const workerBacklogMaxSeconds = Math.max(...backlogSamples);
  const workerBusyPublications = measurementPublications.filter(publication => publication.busy).length;
  const rendererHeapSamples = frames.resourceSamples.flatMap(sample => sample.rendererHeapBytes === undefined
    ? [] : [sample.rendererHeapBytes]);
  const rendererMemory = rendererHeapSamples.length >= 2 ? { availability: 'available' as const,
    beforeBytes: rendererHeapSamples[0]!, afterBytes: rendererHeapSamples.at(-1)!,
    method: 'Chromium performance.memory.usedJSHeapSize sampled at measurement start and end' }
    : { availability: 'unavailable' as const, reason: 'performance.memory is unavailable in this Chromium runtime.' };
  const objectCounts = frames.resourceSamples.map(sample => sample.domNodes);
  const workerCounts = frames.resourceSamples.map(sample => sample.workers);
  const memory = { process: { availability: 'unavailable' as const,
    reason: 'Browser Playwright page context has no production browser-process memory API.' }, renderer: rendererMemory,
  worker: { availability: 'unavailable' as const, reason: 'Production Web Worker exposes no heap measurement API.' },
  gpu: { availability: 'unavailable' as const, reason: 'WebGL exposes no portable actual VRAM usage API.' },
  boundedCounts: {
    objects: { availability: 'available' as const, before: objectCounts[0]!, after: objectCounts.at(-1)!,
      maximum: Math.max(...objectCounts) },
    caches: { availability: 'unavailable' as const,
      reason: 'The production renderer exposes source and resource-entry counts, but no authoritative cache-object count.' },
    workers: { availability: 'available' as const, before: workerCounts[0]!, after: workerCounts.at(-1)!,
      maximum: Math.max(...workerCounts) },
  } };
  if (workflow === 'soak') {
    expect(frames.resourceSamples.length).toBeGreaterThanOrEqual(40);
    if (rendererMemory.availability === 'available') expect((rendererMemory.afterBytes - rendererMemory.beforeBytes)
      / rendererMemory.beforeBytes).toBeLessThanOrEqual(.1);
    expect(activeWorkersAfter).toBeLessThanOrEqual(activeWorkersBefore);
  }
  const presentation = await presentationCounts(page);
  if (workflow === 'visibility-near') expect(presentation.visible).toBeGreaterThanOrEqual(Math.ceil(target * .9));
  if (workflow === 'visibility-wide') expect(presentation.visible).toBeLessThan(activeOccupancy.at(-1)!);
  const marks = await telemetry(page), stages = new Set(marks.entries.map(entry => entry.stage));
  if (enabled) {
    const required = ['dual-worker-receive', 'dual-react-commit', 'dual-snow-applied',
      'snow-tile-generated', 'terrain-dem-generated'];
    if (expectedTarget > 0 && workload !== 'aggregate') required.push('guest-gpu-first-draw');
    for (const stage of required) expect(stages, `missing ${stage}`).toContain(stage);
  } else {
    expect(marks.entries, 'disabled telemetry must not retain internal trace entries').toEqual([]);
  }
  const measurementEndedAt = frames.startedAt + frames.windowDurationMs;
  const lastDraw = enabled ? marks.entries.filter(entry => entry.stage === 'guest-gpu-first-draw'
    && entry.at >= frames.startedAt && entry.at <= measurementEndedAt
    && Number.isInteger(entry.committedRevision)).at(-1) : undefined;
  const drawPublication = lastDraw ? marks.entries.find(entry => entry.stage === 'dual-react-commit'
    && entry.requestId === lastDraw.requestId && entry.generation === lastDraw.generation
    && entry.operationGeneration === lastDraw.operationGeneration
    && entry.committedRevision === lastDraw.committedRevision
    && entry.publicationSequence === lastDraw.publicationSequence) : undefined;
  const correlationEvidence = { measurementStartedAt: frames.startedAt, measurementEndedAt, measurementPublicationStart,
    lastDraw, drawPublication, recentDraws: marks.entries.filter(entry => entry.stage === 'guest-gpu-first-draw').slice(-8),
    measurementPublications: measurementPublications.slice(-8).map(publication => ({
      requestId: publication.id, generation: publication.generation,
      operationGeneration: publication.operationGeneration, committedRevision: publication.committedRevision,
      macroSecond: publication.macroSecond, microSecond: publication.microSecond,
    })) };
  writeFileSync(path.join(path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark'),
    `${runId}.correlation.json`), `${JSON.stringify(correlationEvidence, null, 2)}\n`);
  if (lastDraw) expect(drawPublication, 'last in-window GPU draw must match its exact committed publication identity').toBeTruthy();
  const lastDrawnRevision = lastDraw ? { availability: 'available' as const, value: lastDraw.committedRevision!,
    generation: lastDraw.generation, publicationSequence: lastDraw.publicationSequence, requestId: lastDraw.requestId,
    observedAt: lastDraw.at } : { availability: 'unavailable' as const,
    reason: enabled ? workload === 'aggregate' || workload === 'empty'
      ? 'This workload intentionally emits no detailed guest GPU frame.'
      : 'No detailed guest GPU draw occurred during this measurement window.'
      : 'Integrated telemetry was disabled for this paired condition.' };
  failureStage = 'interaction';
  const gradingMs = await exerciseGrading(page);
  failureStage = 'source-readiness';
  const finalPause = page.getByRole('button', { name: 'Pause game clock', exact: true });
  let pausedPublication: WorkerProbePublication | undefined;
  if (await finalPause.isVisible()) {
    const publicationCount = (await workerEntries(page, 'dualClock.worker')).flatMap(entry => entry.publications ?? []).length;
    await finalPause.click();
    await expect.poll(async () => {
      pausedPublication = (await workerEntries(page, 'dualClock.worker')).flatMap(entry => entry.publications ?? [])
        .slice(publicationCount).findLast(publication => publication.requestType === 'pause' && publication.busy !== true);
      return !!pausedPublication;
    }, {
      timeout: 10_000, message: 'final pause must commit before source-readiness finalization',
    }).toBe(true);
  }
  expect(pausedPublication).toMatchObject({ requestType: 'pause', busy: false });
  expect(Number.isFinite(pausedPublication?.macroSecond) && Number.isFinite(pausedPublication?.microSecond)
    && Number.isFinite(pausedPublication?.committedRevision) && typeof pausedPublication?.at === 'string').toBe(true);
  let finalReadiness: { terrain: boolean; terrainLoaded: boolean; snow: boolean; snowLoaded: boolean;
    imagery: boolean; imageryLoaded: boolean; mapLoaded: boolean; observedAt: number } | undefined;
  await expect.poll(async () => {
    const observation = await page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { getSource(id: string): unknown;
      isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
      return { terrain: !!map.getSource('terrain-dem'), terrainLoaded: map.isSourceLoaded('terrain-dem'),
        snow: !!map.getSource('snow'), snowLoaded: map.isSourceLoaded('snow'),
        imagery: !!map.getSource('satellite'), imageryLoaded: map.isSourceLoaded('satellite'),
        mapLoaded: map.loaded(), observedAt: performance.now() };
    });
    if (observation.terrain && observation.terrainLoaded && observation.snow && observation.snowLoaded
      && observation.mapLoaded && (!jacksonFixture || (observation.imagery && observation.imageryLoaded))) {
      finalReadiness = observation;
    }
    return !!finalReadiness;
  }, { timeout: 30_000, message: 'terrain, snow, and map must reach final readiness independently' }).toBe(true);
  const sourceState = finalReadiness!;
  expect(finalReadiness).toMatchObject({ terrain: true, terrainLoaded: true, snow: true, snowLoaded: true, mapLoaded: true });
  if (jacksonFixture) {
    expect(finalReadiness).toMatchObject({ imagery: true, imageryLoaded: true });
  }
  expect(frames.sourceEvents).toContain('terrain-dem'); expect(frames.sourceEvents).toContain('snow');
  expect(frames.rafIntervals.length).toBeGreaterThan(0); expect(frames.renderIntervals.length).toBeGreaterThan(0);
  expect(frames.rafIntervals.length).toBeGreaterThanOrEqual(30);
  expect(frames.renderIntervals.length).toBeGreaterThanOrEqual(30);
  expect(Math.max(...frames.rafIntervals)).toBeLessThan(measurementMs + 1_000);
  const savedKey = fixture ? fixture!.saves[target as 0 | 1000 | 3000]!.key : 'integrated-diagnostic-save';
  const readSavedDocument = (key: string) => page.evaluate(savedKey => new Promise<Record<string, unknown> | null>((resolve, reject) => {
    const open = indexedDB.open('mountain-planner-dual-saves', 1);
    open.onerror = () => reject(open.error); open.onsuccess = () => {
      const database = open.result, request = database.transaction('games').objectStore('games').get(savedKey);
      request.onerror = () => { database.close(); reject(request.error); };
      request.onsuccess = () => { database.close(); resolve(request.result ?? null); };
    };
  }), key);
  const installedDocument = await readSavedDocument(savedKey);
  expect(installedDocument).toBeTruthy();
  expect((installedDocument as { roads?: { name?: string }[] }).roads?.some(
    road => road.name === 'Integrated benchmark grade')).not.toBe(true);
  await page.locator('.game-menu-btn').click(); await page.locator('.hud-save').click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  let persistedDocument: Record<string, unknown> | null = null;
  let lastSavedCandidate: unknown = null;
  try { await expect.poll(async () => {
    const candidate = await readSavedDocument(savedKey) as { schemaVersion?: number; updatedAt?: string;
      roads?: { name?: string; terrainGraded?: boolean }[]; dualClock?: { resortRevision?: number; clock?: {
        revision?: number; macroSecond?: number; microSecond?: number; paused?: boolean } } } | null;
    lastSavedCandidate = candidate && { schemaVersion: candidate.schemaVersion, updatedAt: candidate.updatedAt,
      roads: candidate.roads?.map(road => ({ name: road.name, terrainGraded: road.terrainGraded })),
      dualClock: candidate.dualClock && { resortRevision: candidate.dualClock.resortRevision, clock: candidate.dualClock.clock } };
    if (candidate?.schemaVersion === 17 && Date.parse(candidate.updatedAt ?? '') > Date.parse(String(installedDocument?.updatedAt ?? ''))
      && candidate.roads?.some(road => road.name === 'Integrated benchmark grade' && road.terrainGraded === true)
      && Number.isFinite(candidate.dualClock?.resortRevision)
      && candidate.dualClock?.clock?.revision === pausedPublication?.committedRevision
      && candidate.dualClock?.clock?.macroSecond === pausedPublication?.macroSecond
      && candidate.dualClock?.clock?.microSecond === pausedPublication?.microSecond
      && candidate.dualClock?.clock?.paused === true) persistedDocument = candidate;
    return !!persistedDocument;
  }, { timeout: 30_000, message: 'save finalization must persist the newly graded road and coherent paused schema-17 checkpoint' }).toBe(true); }
  catch (error) {
    writeFileSync(path.join(path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark'),
      `${runId}.save-candidate.failure.json`), `${JSON.stringify({ installedUpdatedAt: installedDocument?.updatedAt,
      pausedPublication, lastSavedCandidate }, null, 2)}\n`);
    throw error;
  }
  const persistedCheckpoint = (persistedDocument as unknown as { updatedAt: string; dualClock: { resortRevision: number; clock: {
    revision: number; macroSecond: number; microSecond: number; paused: boolean } };
    roads: { name?: string; terrainGraded?: boolean }[] });
  const previewAvailable = await page.evaluate(key =>
    localStorage.getItem(`gamesave-preview:${key}`)?.startsWith('data:image/') ?? false, savedKey);
  writeFileSync(path.join(path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark'),
    `${runId}.finalization.json`), `${JSON.stringify({ savedKey, documentPersisted: true, finalReadiness, sourceState,
    persistedDocument: { schemaVersion: 17, updatedAt: persistedCheckpoint.updatedAt,
      gradedRoad: persistedCheckpoint.roads.find(road => road.name === 'Integrated benchmark grade'),
      checkpoint: persistedCheckpoint.dualClock },
    preview: previewAvailable ? { availability: 'available' } : { availability: 'unavailable',
      reason: 'Preview capture is best-effort and did not complete before document persistence.' } }, null, 2)}\n`);
  await restoreSelectedCheckpoint(page, qualification ? 180_000 : 30_000);
  let restoredPublication: WorkerProbePublication | undefined;
  await expect.poll(async () => {
    restoredPublication = (await workerEntries(page, 'dualClock.worker')).flatMap(entry => entry.publications ?? [])
      .find(publication => publication.type === 'publication');
    return !!restoredPublication;
  }).toBe(true);
  expect(restoredPublication).toMatchObject({ committedRevision: persistedCheckpoint.dualClock.clock.revision,
    macroSecond: persistedCheckpoint.dualClock.clock.macroSecond,
    microSecond: persistedCheckpoint.dualClock.clock.microSecond });
  const reopenedDocument = await readSavedDocument(savedKey) as typeof persistedCheckpoint;
  expect(reopenedDocument.updatedAt).toBe(persistedCheckpoint.updatedAt);
  expect(reopenedDocument.dualClock).toMatchObject(persistedCheckpoint.dualClock);
  expect(reopenedDocument.roads).toContainEqual(expect.objectContaining({ name: 'Integrated benchmark grade', terrainGraded: true }));
  await expect.poll(() => page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { querySourceFeatures(id: string): { properties?: Record<string, unknown> }[] } }).appMap;
    return map.querySourceFeatures('roads').some(feature => feature.properties?.name === 'Integrated benchmark grade');
  }), { timeout: 30_000, message: 'reopened map must hydrate the newly graded road' }).toBe(true);
  const reopenCycles = Math.max(1, Number(process.env.INTEGRATED_REOPEN_CYCLES ?? 1));
  for (let cycle = 1; cycle < reopenCycles; cycle++) {
    await page.reload(); await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.maplibregl-canvas')).toBeVisible({ timeout: 30_000 });
  }
  if (jacksonFixture) { expect(requiredGlyphRequests.length).toBeGreaterThan(0); expect(externalRequests).toEqual([]); }
  const deliveredFrames = frames.rafIntervals.length;
  const actualDisplay = { cssViewport: await page.evaluate(() => ({ width: innerWidth, height: innerHeight })),
    canvas: await page.locator('.maplibregl-canvas').evaluate(canvas => ({ width: (canvas as HTMLCanvasElement).width,
      height: (canvas as HTMLCanvasElement).height })), devicePixelRatio: await page.evaluate(() => devicePixelRatio),
    quality: await page.evaluate(() => JSON.parse(localStorage.getItem('skiapp:settings') ?? '{}').renderQuality ?? 'standard') };
  if (jacksonFixture) expect(actualDisplay).toEqual({ cssViewport: selectedScenario.cssViewport,
    canvas: selectedScenario!.canvas, devicePixelRatio: selectedScenario!.devicePixelRatio,
    quality: selectedScenario!.profile.toLowerCase() });
  const suppliedIdentity = process.env.INTEGRATED_RUN_IDENTITY_PATH
    ? JSON.parse(readFileSync(process.env.INTEGRATED_RUN_IDENTITY_PATH, 'utf8')) as Record<string, unknown>
    : null;
  const runtimeObservation = await page.evaluate(() => {
    const canvas = document.createElement('canvas'), gl = canvas.getContext('webgl');
    const debug = gl?.getExtension('WEBGL_debug_renderer_info'); return { kind: 'browser', version: navigator.userAgent,
      glVendor: String(debug && gl?.getParameter(debug.UNMASKED_VENDOR_WEBGL) || gl?.getParameter(gl.VENDOR) || 'unavailable'),
      glRenderer: String(debug && gl?.getParameter(debug.UNMASKED_RENDERER_WEBGL) || gl?.getParameter(gl.RENDERER) || 'unavailable'),
      glBackend: '' };
  }).then(runtime => ({ ...runtime, glBackend: glBackend(runtime.glRenderer) }));
  const result = {
    schemaVersion: 1, id: runId, pairIndex: Number(process.env.INTEGRATED_PAIR_INDEX ?? 1),
    order: process.env.INTEGRATED_PAIR_ORDER === 'BA' ? 'BA' : 'AB', condition: process.env.INTEGRATED_CONDITION === 'B' ? 'B' : 'A',
    telemetryEnabled: enabled, status: 'valid',
    invalidReasons: [],
    identity: jacksonFixture ? {
      ...suppliedIdentity,
      scenarioId: suppliedIdentity?.scenarioId ?? scenarioName,
      workloadId: suppliedIdentity?.workloadId ?? fixture!.manifest.fixtureId,
      seed: installedScenario.seed,
      checkpointHash: suppliedIdentity?.checkpointHash ?? fixture!.manifest.artifacts[target === 0 ? 'checkpointEmpty' : target === 1000 ? 'checkpoint1000' : 'checkpoint3000'].sha256,
      fixtureManifestHash: suppliedIdentity?.fixtureManifestHash ?? createHash('sha256').update(
        readFileSync(path.join(process.env.INTEGRATED_FIXTURE_ROOT!, 'fixture-manifest.json'))).digest('hex'),
      assetManifestHash: suppliedIdentity?.assetManifestHash ?? createHash('sha256').update(
        JSON.stringify(fixture!.manifest.artifacts)).digest('hex'),
      cacheStatePolicy: suppliedIdentity?.cacheStatePolicy ?? process.env.INTEGRATED_CACHE_STATE ?? 'warm',
      profile: suppliedIdentity?.profile ?? 'Standard',
      cssViewport: actualDisplay.cssViewport,
      canvas: actualDisplay.canvas,
      devicePixelRatio: actualDisplay.devicePixelRatio,
      refreshHz: suppliedIdentity?.refreshHz ?? Number(process.env.INTEGRATED_REFRESH_HZ ?? 60),
      machine: suppliedIdentity?.machine ?? { id: process.env.INTEGRATED_MACHINE_ID ?? 'unidentified', cpu: process.env.INTEGRATED_CPU ?? 'unidentified',
        gpu: process.env.INTEGRATED_GPU ?? 'unidentified', driver: process.env.INTEGRATED_DRIVER ?? 'unidentified', os: process.env.INTEGRATED_OS ?? 'unidentified' },
      runtime: { ...(suppliedIdentity?.runtime as Record<string, unknown> | undefined), ...runtimeObservation },
      source: suppliedIdentity?.source ?? { commit: process.env.INTEGRATED_COMMIT ?? 'uncommitted', workingTreeStatus: process.env.INTEGRATED_WORKTREE ?? 'dirty',
        lockfileHash: process.env.INTEGRATED_LOCK_HASH ?? 'unrecorded', buildHash: process.env.INTEGRATED_BUILD_HASH ?? 'unrecorded' },
    } : { tier: runConfiguration.tier, scenarioId: scenarioName, profile: selectedScenario.profile,
      cssViewport: actualDisplay.cssViewport, canvas: actualDisplay.canvas,
      devicePixelRatio: actualDisplay.devicePixelRatio,
      refreshHz: Number(process.env.INTEGRATED_REFRESH_HZ ?? 60), runtime: runtimeObservation },
    metrics: { frameIntervalMs: percentile(frames.rafIntervals, .95), frameP99Ms: percentile(frames.rafIntervals, .99),
      framesOver50Ratio: frames.rafIntervals.filter(value => value > 50).length / frames.rafIntervals.length,
      longestLongTaskMs: Math.max(0, ...frames.longTasks),
      pauseResponseMs: interactions.pauseMs, workerCancellationMs: interactions.cancelMs,
      sourceRevisionResponseMs: interactions.snowMs,
      snowVisibleResponseMs: { availability: 'unavailable',
        reason: 'Visible snow latency is validated by a separate revision-correlated framebuffer diagnostic.' },
      gradingConfirmationMs: gradingMs, snowAffectedCells: interactions.snowAffectedCells,
      snowSourceRevision: interactions.snowSourceRevision,
      activeOccupancyMin, macroSecondsAdvanced, microSecondsAdvanced, workerBacklogMaxSeconds,
      occupancyMacroSecondsAdvanced, occupancyMicroSecondsAdvanced,
      workerBusyPublications, lastCommittedRevision: lastPublication.committedRevision },
    samples: { frameIntervalMs: frames.rafIntervals, mapRenderIntervalMs: frames.renderIntervals, activeOccupancy,
      longTaskMs: frames.longTasks,
      visibleActors: [presentation.visible], drawnActors: [presentation.drawn] },
    measurement: { windowDurationMs: frames.windowDurationMs, expectedFrames: Math.round(frames.windowDurationMs /
      (1000 / Number(suppliedIdentity?.refreshHz ?? process.env.INTEGRATED_REFRESH_HZ ?? 60))),
      deliveredFrames, trailingGapAccounted: true, livenessPassed: deliveredFrames > 0 && frames.renderIntervals.length > 0,
      rafTrailingGapMs: frames.rafTrailingGapMs, renderTrailingGapMs: frames.renderTrailingGapMs },
    evidence: { telemetry: marks, sourceState, sourceEvents: frames.sourceEvents, requiredGlyphRequests, externalRequests,
      ...(diagnosticProfile ? { diagnosticProfile } : {}),
      cacheState: { policy: process.env.INTEGRATED_CACHE_STATE ?? 'warm', profileIsolated: true,
        checkpointRestored: jacksonFixture, fixtureManifestHash: suppliedIdentity?.fixtureManifestHash,
        cleared: (process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'cold',
        primed: (process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'warm',
        primeCompleted: (process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'warm' },
      simulation: { publications: measurementPublications.map(publication => ({ requestId: publication.id,
        generation: publication.generation, operationGeneration: publication.operationGeneration,
        macroSecond: publication.macroSecond,
        microSecond: publication.microSecond, active: Number((publication.flow as { active?: number } | undefined)?.active),
        movementCount: publication.movementCount, committedRevision: publication.committedRevision })),
      warmupAndMeasurementPublications: occupancyPublications.map(publication => ({ requestId: publication.id,
        generation: publication.generation, operationGeneration: publication.operationGeneration,
        macroSecond: publication.macroSecond, microSecond: publication.microSecond,
        active: Number((publication.flow as { active?: number } | undefined)?.active),
        movementCount: publication.movementCount, committedRevision: publication.committedRevision })),
      acknowledgements: measurementAcknowledgements, targetOccupancy: expectedTarget, requestedSpeed,
      backlog: { availability: 'available', samples: backlogSamples } },
      presentation: { lastDrawnRevision,
        visibleActors: presentation.visible, drawnActors: presentation.drawn },
      memory, resources: { activeWorkers: { before: activeWorkersBefore, after: activeWorkersAfter },
        samples: frames.resourceSamples },
      measurementPublications, firstPublication, lastPublication },
  };
  failureStage = 'teardown';
  mkdirSync(resultsDir, { recursive: true }); writeFileSync(path.join(resultsDir, `${runId}.trial.json`), `${JSON.stringify(result, null, 2)}\n`);
});

test('diagnostic empty checkpoint reaches the shipped worker and renderer with zero guests', async ({ page, baseURL }) => {
  test.skip(jacksonFixture, 'The Jackson empty scenario runs through the dedicated runner matrix.');
  test.setTimeout(300_000);
  await installInstrumentation(page, false);
  const diagnostic = diagnosticPackage(0);
  expect({ checkpointTarget: diagnostic.scenario.checkpointTarget, dailyDemand: diagnostic.scenario.dailyDemand,
    dailyDemandByWeekday: diagnostic.scenario.dailyDemandByWeekday, amenities: diagnostic.scenario.amenities.length,
    seed: diagnostic.scenario.seed }).toEqual({ checkpointTarget: 0, dailyDemand: 0,
    dailyDemandByWeekday: [0, 0, 0, 0, 0, 0, 0], amenities: 3, seed: diagnostic.save.dualClock.seed });
  await page.addInitScript(({ key, value }) => {
    (globalThis as typeof globalThis & Record<string, unknown>)[key] = value;
  }, { key: scenarioGlobal, value: diagnostic.scenario });
  await page.goto(new URL(appQuery, baseURL!).toString(), { waitUntil: 'load' });
  await seedDiagnostic(page, diagnostic);
  await restoreSelectedCheckpoint(page, 30_000);
  await expect.poll(async () => workerEntries(page, 'dualClock.worker').then(entries => {
    const latest = entries.flatMap(entry => entry.publications ?? []).reverse().find(publication =>
      publication.movementCount !== undefined);
    return latest ? { active: Number((latest.flow as { active?: number } | undefined)?.active ?? -1),
      movementCount: latest.movementCount } : null;
  })).toEqual({ active: 0, movementCount: 0 });
  await expect.poll(() => page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { getSource(id: string): unknown;
      isSourceLoaded(id: string): boolean } }).appMap;
    return !!map.getSource('terrain-dem') && map.isSourceLoaded('terrain-dem') && !!map.getSource('snow')
      && map.isSourceLoaded('snow');
  }), { timeout: 30_000 }).toBe(true);
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const map = (globalThis as typeof globalThis & { appMap: { once(type: string, listener: () => void): void;
      triggerRepaint(): void } }).appMap;
    const timeout = setTimeout(() => reject(new Error('Map did not render after empty sources became ready.')), 10_000);
    map.once('render', () => { clearTimeout(timeout); resolve(); }); map.triggerRepaint();
  }));
});

test('rejects malformed scenario and renamed or incomplete qualification manifest', () => {
  expect(snowAffectedCells('Added 1 cm across 1,888 terrain cells.')).toBe(1888);
  expect(() => validateIntegratedBenchmarkScenario({ version: 1, tier: 'qualification', workload: 'detailed',
    fixtureId: 'renamed-diagnostic', runId: 'negative', seed: 'seed', replayId: 'replay', saveKey: 'save',
    terrainKey: 'jackson-renamed-diagnostic', checkpointTarget: 1000, dailyDemand: 900,
    dailyDemandByWeekday: [900, 900, 900, 900, 900, 900, 900], amenities: [] })).toThrow();
  expect(() => validateIntegratedBenchmarkFixtureManifest({ contractVersion: 1,
    fixtureId: 'jackson-black-mountain-live-v1', terrainKey: 'jackson-renamed-diagnostic' })).toThrow();
});
