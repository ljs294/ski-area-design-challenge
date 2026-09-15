import { createHash } from 'node:crypto';
import { mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { chromium, expect, test, type Browser, type Page } from '@playwright/test';
import { startIntegratedDiagnosticProfile } from '../../../scripts/integratedDiagnosticProfiler.mjs';
import { launchPackagedElectron } from '../../../scripts/packagedElectronControl.mjs';
import { installWorkerProbe, workerEntries } from '../support/workerProbe';
import { installIntegratedBenchmarkScenario, loadIntegratedBenchmarkFixture,
  persistIntegratedBenchmarkFixture, scenarioForSelectedFixture } from './support/integratedFixture';
import { resolveIntegratedBenchmarkConfiguration } from '../../../scripts/integratedBenchmarkConfiguration.mjs';
import { retainIntegratedFailure, runIntegratedStage, waitForMapSourceReadiness, waitForTerminalLoadingOverlay,
  type IntegratedFailureStage } from './support/integratedRuntime';

const telemetryConfig = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY__';
const telemetryState = '__MOUNTAIN_PLANNER_BENCHMARK_TELEMETRY_STATE__';
const scenarioGlobal = '__MOUNTAIN_PLANNER_INTEGRATED_BENCHMARK__';
const runConfiguration = resolveIntegratedBenchmarkConfiguration();
let failureStage: IntegratedFailureStage = 'launch';
type ScenarioContract = { checkpointTarget: 0 | 1000 | 3000; requestedSpeed: 1 | 2 | 4 | 8 | 16 | 64;
  workload: 'empty' | 'detailed' | 'aggregate'; workflow: string; profile: 'Standard' | 'High';
  cssViewport: { width: number; height: number }; canvas: { width: number; height: number }; devicePixelRatio: number };
const scenarioMatrix = JSON.parse(readFileSync(path.resolve('scripts/integratedBenchmarkScenarios.json'), 'utf8')) as
  Record<string, ScenarioContract>;
const appQuery = process.env.INTEGRATED_APP_QUERY ?? '?dev-console';
if (new URLSearchParams(appQuery.startsWith('?') ? appQuery.slice(1) : appQuery).has('flat')) {
  throw new Error('Integrated benchmark cannot disable terrain with the flat query.');
}

function snowAffectedCells(text: string): number {
  return Number((/across ([\d,]+) terrain cells/.exec(text)?.[1] ?? '0').replaceAll(',', ''));
}

type ReleaseRuntimeEvent = { type: string; at: string; detail?: unknown };

function heavyDiagnosticRequested(): boolean {
  if (process.env.INTEGRATED_PROFILING !== 'heavy') return false;
  if (!process.env.INTEGRATED_RUN_IDENTITY_PATH) throw new Error('Heavy profiling requires runner-supplied diagnostic identity.');
  const identity = JSON.parse(readFileSync(process.env.INTEGRATED_RUN_IDENTITY_PATH, 'utf8')) as { tier?: string };
  if (identity.tier !== 'diagnostic') throw new Error('Heavy profiling is restricted to diagnostic trials.');
  return true;
}

function releaseExecutable(): string {
  const supplied = process.env.ELECTRON_RELEASE_PATH;
  if (!supplied) throw new Error('ELECTRON_RELEASE_PATH is required for packaged qualification.');
  const executable = path.resolve(supplied), normalized = executable.replaceAll('\\', '/').toLowerCase();
  if (normalized.includes('/node_modules/') || path.basename(executable).toLowerCase() === 'electron.exe') {
    throw new Error('Integrated Electron qualification requires the packaged release executable, not Electron from node_modules.');
  }
  return executable;
}

function isolatedUserData(): string {
  const supplied = process.env.INTEGRATED_USER_DATA_DIR;
  if (!supplied) throw new Error('INTEGRATED_USER_DATA_DIR is required for packaged qualification.');
  return path.resolve(supplied);
}

async function launchRelease(executablePath: string, userData: string, scenario: ReturnType<typeof scenarioForSelectedFixture>,
  telemetryEnabled: boolean, events: ReleaseRuntimeEvent[]) {
  const control = await launchPackagedElectron({ executablePath, userDataDir: userData, scenario, telemetryEnabled,
    devConsole: new URLSearchParams(appQuery.startsWith('?') ? appQuery.slice(1) : appQuery).has('dev-console'),
    deviceScaleFactor: scenarioMatrix[process.env.INTEGRATED_SCENARIO ?? '']?.devicePixelRatio ?? 1 });
  const browser = await chromium.connectOverCDP(control.endpoint);
  const deadline = Date.now() + 30_000;
  let page: Page | undefined;
  while (!page && Date.now() < deadline) {
    page = browser.contexts().flatMap(context => context.pages())[0];
    if (!page) await new Promise(resolve => setTimeout(resolve, 50));
  }
  if (!page) { await browser.close().catch(() => undefined); await control.close();
    throw new Error('Packaged Electron exposed CDP without a renderer page.'); }
  const record = (type: string, detail?: unknown) => {
    if (events.length < 200) events.push({ type, at: new Date().toISOString(), ...(detail === undefined ? {} : { detail }) });
  };
  page.on('framenavigated', frame => { if (frame === page!.mainFrame()) record('main-frame-navigated', frame.url()); });
  page.on('crash', () => record('renderer-crashed'));
  page.on('close', () => record('page-closed'));
  browser.on('disconnected', () => record('browser-disconnected'));
  void control.exited.then(result => record('process-exited', result));
  await page.waitForURL(url => url.protocol === 'file:' && /\/index\.html$/.test(url.pathname), { timeout: 30_000 });
  await page.waitForLoadState('load', { timeout: 30_000 });
  await page.waitForFunction(() => {
    const bridge = (globalThis as typeof globalThis & { desktop?: { terrain?: unknown; weather?: unknown; games?: unknown } }).desktop;
    return !!bridge?.terrain && !!bridge.weather && !!bridge.games;
  }, undefined, { timeout: 30_000 });
  return { browser, control, page };
}

async function closeRelease(release: { browser: Browser; control: Awaited<ReturnType<typeof launchPackagedElectron>>;
  page: Page }): Promise<void> {
  await release.page.evaluate(() => (globalThis as typeof globalThis & { desktop?: { exit(): void } }).desktop?.exit())
    .catch(() => undefined);
  await release.control.close();
  await release.browser.close().catch(() => undefined);
}

async function awaitLoadingComplete(page: Page, timeout = 180_000): Promise<void> {
  await waitForTerminalLoadingOverlay(page, timeout);
}

async function installTelemetry(page: Page, enabled: boolean): Promise<void> {
  await installWorkerProbe(page);
  await page.addInitScript(({ config, state, active }) => {
    (globalThis as typeof globalThis & Record<string, unknown>)[config] = { enabled: active, maxEntries: 20_000 };
    delete (globalThis as typeof globalThis & Record<string, unknown>)[state];
    const evidence: { sources: string[]; renders: number[] } = { sources: [], renders: [] };
    (globalThis as typeof globalThis & { integratedEarlyMapEvidence?: unknown }).integratedEarlyMapEvidence = evidence;
    let currentMap: { on(type: string, listener: (event: { sourceId?: string; isSourceLoaded?: boolean }) => void): void } | undefined;
    Object.defineProperty(globalThis, 'appMap', { configurable: true, enumerable: true,
      get: () => currentMap, set: value => { currentMap = value as typeof currentMap;
        currentMap?.on('sourcedata', event => { if (event.sourceId && event.isSourceLoaded) evidence.sources.push(event.sourceId); });
        currentMap?.on('render', () => evidence.renders.push(performance.now())); } });
  }, { config: telemetryConfig, state: telemetryState, active: enabled });
}

async function exerciseScenarioWorkflow(page: Page, workflow: string, workload: string): Promise<void> {
  await expect.poll(() => page.evaluate(({ key, workload }) => {
    const scenario = (globalThis as typeof globalThis & Record<string, unknown>)[key] as { workload?: string } | undefined;
    return scenario?.workload === workload;
  }, { key: scenarioGlobal, workload })).toBe(true);
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
      hit = await page.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
        getCanvas(): HTMLCanvasElement; getLayer(id: string): { implementation?: Record<string, unknown> } | undefined } }).appMap;
      const layer = map.getLayer('guest-simulation-dots')?.implementation, xs = layer?.hitXs as ArrayLike<number> | undefined,
        ys = layer?.hitYs as ArrayLike<number> | undefined, rect = map.getCanvas().getBoundingClientRect();
      return xs?.length && ys?.length ? { x: rect.left + Number(xs[0]), y: rect.top + Number(ys[0]) } : null; });
      return hit !== null;
    }).toBe(true);
    await page.mouse.move(hit!.x - 30, hit!.y - 30); await page.mouse.move(hit!.x, hit!.y, { steps: 8 });
    await page.mouse.click(hit!.x, hit!.y); await expect(page.locator('[aria-label^="Selected guest "]')).toBeVisible();
    await page.getByRole('button', { name: 'Follow', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Stop following', exact: true })).toBeVisible();
    await expect(page.getByLabel('Snow layer controls')).toBeVisible();
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

async function startWindow(page: Page): Promise<void> {
  await page.evaluate(() => {
    const map = (globalThis as typeof globalThis & { appMap: { on(type: string, listener: () => void): void;
      off(type: string, listener: () => void): void; triggerRepaint(): void;
      getStyle(): { sources?: Record<string, unknown>; layers?: unknown[] };
      getLayer(id: string): { implementation?: Record<string, unknown> } | undefined } }).appMap;
    const startedAt = performance.now(), raf: number[] = [], renders: number[] = [], longTasks: number[] = [],
      resources: { at: number; rendererHeapBytes?: number; domNodes: number; resourceEntries: number;
        mapSources: number; mapLayers: number; drawnGuests: number; workers: number }[] = [];
    let priorRaf = startedAt, priorRender = 0, frame = 0;
    const tick = (at: number) => { raf.push(at - priorRaf); priorRaf = at; frame = requestAnimationFrame(tick); };
    const render = () => { const at = performance.now(); renders.push(priorRender ? at - priorRender : 0); priorRender = at; };
    const observer = typeof PerformanceObserver === 'undefined' ? null : new PerformanceObserver(list => {
      for (const entry of list.getEntries()) longTasks.push(entry.duration);
    });
    try { observer?.observe({ type: 'longtask', buffered: true }); } catch { /* unavailable */ }
    const sampleResources = () => { const style = map.getStyle();
      const memory = (performance as Performance & { memory?: { usedJSHeapSize?: number } }).memory;
      resources.push({ at: performance.now(), ...(Number.isFinite(memory?.usedJSHeapSize)
        ? { rendererHeapBytes: memory!.usedJSHeapSize } : {}), domNodes: document.getElementsByTagName('*').length,
      resourceEntries: performance.getEntriesByType('resource').length, mapSources: Object.keys(style.sources ?? {}).length,
      mapLayers: style.layers?.length ?? 0,
      drawnGuests: Number(map.getLayer('guest-simulation-dots')?.implementation?.count ?? 0),
      workers: ((globalThis as typeof globalThis & { appWorkerProbe?: { terminationCount: number }[] }).appWorkerProbe ?? [])
        .filter(entry => entry.terminationCount === 0).length }); };
    sampleResources(); const resourceTimer = setInterval(sampleResources, 30_000);
    map.on('render', render); frame = requestAnimationFrame(tick); map.triggerRepaint();
    (globalThis as typeof globalThis & { integratedElectronProbe?: unknown }).integratedElectronProbe = {
      startedAt, raf, renders, longTasks, resources, stop: () => { cancelAnimationFrame(frame); clearInterval(resourceTimer);
        sampleResources(); map.off('render', render);
        observer?.disconnect(); }, lastRaf: () => priorRaf, lastRender: () => priorRender };
  });
}

async function finishWindow(page: Page, durationMs: number) {
  await page.waitForTimeout(durationMs);
  return page.evaluate(() => {
    const probe = (globalThis as typeof globalThis & { integratedElectronProbe: { startedAt: number; raf: number[];
      renders: number[]; longTasks: number[]; resources: { at: number; rendererHeapBytes?: number; domNodes: number;
        resourceEntries: number; mapSources: number; mapLayers: number; drawnGuests: number; workers: number }[];
      stop(): void; lastRaf(): number; lastRender(): number } }).integratedElectronProbe;
    probe.stop();
    const endedAt = performance.now(), rafTrailingGapMs = endedAt - probe.lastRaf(),
      renderTrailingGapMs = endedAt - probe.lastRender();
    probe.raf.push(rafTrailingGapMs); probe.renders.push(renderTrailingGapMs);
    return { startedAt: probe.startedAt, windowDurationMs: endedAt - probe.startedAt, raf: probe.raf, renders: probe.renders,
      longTasks: probe.longTasks, resourceSamples: probe.resources, rafTrailingGapMs, renderTrailingGapMs };
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

function glBackend(renderer: string): string | null {
  const match = renderer.match(/(D3D12|D3D11|Vulkan|Metal|OpenGL|SwiftShader)/i);
  return match?.[1] ?? null;
}

function percentile(values: number[], p: number): number {
  const sorted = [...values].sort((a, b) => a - b), point = (sorted.length - 1) * p,
    lower = Math.floor(point), fraction = point - lower;
  return sorted[lower]! + (sorted[Math.min(lower + 1, sorted.length - 1)]! - sorted[lower]!) * fraction;
}

async function exerciseGrading(page: Page): Promise<number> {
  const points = await page.evaluate(({ center, west, east }) => {
    const map = (globalThis as typeof globalThis & { appMap: { jumpTo(options: unknown): void;
      project(value: [number, number]): { x: number; y: number }; getCanvas(): HTMLCanvasElement } }).appMap;
    map.jumpTo({ center, zoom: 16, pitch: 0, bearing: 0 });
    const rect = map.getCanvas().getBoundingClientRect(), project = (value: [number, number]) => {
      const point = map.project(value); return { x: rect.left + point.x, y: rect.top + point.y };
    };
    return { west: project(west), east: project(east) };
  }, { center: [-71.165, 44.166] as [number, number], west: [-71.166, 44.166] as [number, number],
    east: [-71.164, 44.166] as [number, number] });
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

test('rejects an unpackaged Electron executable before launch', () => {
  const prior = process.env.ELECTRON_RELEASE_PATH;
  process.env.ELECTRON_RELEASE_PATH = path.resolve('node_modules/electron/dist/electron.exe');
  try { expect(() => releaseExecutable()).toThrow(/packaged release executable/); }
  finally { if (prior === undefined) delete process.env.ELECTRON_RELEASE_PATH; else process.env.ELECTRON_RELEASE_PATH = prior; }
});

test('packaged Electron runs the integrated workload through desktop persistence and reopen', async () => {
  failureStage = 'launch';
  test.skip(process.env.RUN_INTEGRATED_ELECTRON !== '1',
    'Set RUN_INTEGRATED_ELECTRON=1, ELECTRON_RELEASE_PATH, INTEGRATED_USER_DATA_DIR, and INTEGRATED_FIXTURE_ROOT.');
  if (runConfiguration.fixture !== 'jackson') throw new Error('Packaged Electron supports only the Jackson fixture; run CI diagnostics in the browser project.');
  test.setTimeout(240_000);
  const executablePath = releaseExecutable(), userData = isolatedUserData();
  const fixtureRoot = path.resolve(process.env.INTEGRATED_FIXTURE_ROOT ?? '');
  if (!process.env.INTEGRATED_FIXTURE_ROOT) throw new Error('INTEGRATED_FIXTURE_ROOT is required.');
  const scenarioName = runConfiguration.scenarioName;
  const selectedScenario = scenarioMatrix[scenarioName];
  if (!selectedScenario) throw new Error(`Unknown integrated benchmark scenario: ${scenarioName}`);
  const target = selectedScenario.checkpointTarget, requestedSpeed = selectedScenario.requestedSpeed;
  const workload = selectedScenario.workload, workflow = selectedScenario.workflow;
  const results = path.resolve(process.env.INTEGRATED_RESULTS_DIR ?? 'test-results/integrated-benchmark');
  const runId = process.env.INTEGRATED_RUN_ID ?? `electron-${Date.now()}`;
  failureStage = 'fixture-installation';
  const fixture = await runIntegratedStage(results, runId, 'fixture-installation',
    { fixture: 'jackson', operation: 'load' }, () => loadIntegratedBenchmarkFixture(fixtureRoot, target));
  expect(Number(process.env.INTEGRATED_TARGET ?? target)).toBe(target);
  expect(Number(process.env.INTEGRATED_REQUESTED_SPEED ?? requestedSpeed)).toBe(requestedSpeed);
  const warmupMs = Number(process.env.INTEGRATED_WARMUP_MS ?? 30_000);
  const measurementMs = Number(process.env.INTEGRATED_MEASURE_MS ?? 120_000);
  test.setTimeout(Math.max(600_000, warmupMs + measurementMs + 480_000));
  const enabled = process.env.INTEGRATED_TELEMETRY !== '0';
  const scenario = scenarioForSelectedFixture(fixture, runId, requestedSpeed);
  const glyphs = new Map<string, Buffer>();
  for (const asset of fixture.manifest.requiredPresentationAssets) if (asset.role.startsWith('glyph:') && asset.sourceUrl) {
    glyphs.set(asset.sourceUrl, readFileSync(path.join(fixtureRoot, asset.path)));
  }
  const externalRequests: string[] = [], requiredGlyphRequests: string[] = [];
  const runtimeEvents: ReleaseRuntimeEvent[] = [];
  failureStage = 'launch';
  let release: Awaited<ReturnType<typeof launchRelease>> | null = null;
  let primaryError: unknown;
  try {
    release = await launchRelease(executablePath, userData, scenario, enabled, runtimeEvents);
    let window = release.page;
    failureStage = 'boot';
    await window.setViewportSize(selectedScenario.cssViewport);
    await expect.poll(() => window.evaluate(key =>
      (globalThis as typeof globalThis & Record<string, unknown>)[key], scenarioGlobal)).toEqual(scenario);
    expect(release.control.args).toContain(`--user-data-dir=${userData}`);
    await window.route('**/*', async route => {
      const url = route.request().url();
      if (glyphs.has(url)) { requiredGlyphRequests.push(url); await route.fulfill({ status: 200,
        contentType: 'application/x-protobuf', body: glyphs.get(url)! }); return; }
      if (/^https?:/.test(url)) { externalRequests.push(url); await route.abort('blockedbyclient'); return; }
      await route.continue();
    });
    await installTelemetry(window, enabled);
    await installIntegratedBenchmarkScenario(window, fixture, target, runId, requestedSpeed);
    let persistenceTimings;
    failureStage = 'fixture-installation';
    try { persistenceTimings = await runIntegratedStage(results, runId, 'fixture-installation',
      { fixture: 'jackson', operation: 'persist', runtimeEvents }, () =>
        persistIntegratedBenchmarkFixture(window, fixture, target)); }
    catch (error) {
      mkdirSync(results, { recursive: true });
      const diagnostic = { stage: 'fixture-persistence', runtimeEvents, exitCode: release.control.exitCode,
        stdout: release.control.stdout, stderr: release.control.stderr,
        error: error instanceof Error ? error.message : String(error) };
      const diagnosticPath = path.join(results, `${runId}.persistence-failure.json`);
      writeFileSync(diagnosticPath, `${JSON.stringify(diagnostic, null, 2)}\n`);
      throw new Error(`Packaged fixture persistence failed; diagnostics: ${diagnosticPath}`,
        { cause: error instanceof Error ? error : undefined });
    }
    await window.evaluate(quality => localStorage.setItem('skiapp:settings', JSON.stringify({ renderQuality: quality })),
      selectedScenario.profile.toLowerCase());
    failureStage = 'boot';
    await window.reload({ waitUntil: 'load' });
    await window.getByRole('button', { name: /^Continue / }).click();
    await awaitLoadingComplete(window);
    await expect(window.locator('.maplibregl-canvas')).toBeVisible();
    failureStage = 'source-readiness';
    await waitForMapSourceReadiness(window, true, 45_000);
    const installedScenario = await window.evaluate(key =>
      (globalThis as typeof globalThis & Record<string, unknown>)[key] as { seed: string }, scenarioGlobal);
    await expect.poll(async () => workerEntries(window, 'dualClock.worker').then(entries => entries.some(entry =>
      entry.publications?.some(publication => publication.type === 'publication')))).toBe(true);
    failureStage = 'workload-readiness';
    await expect(window.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible();
    failureStage = 'interaction';
    const initialPlay = window.getByRole('button', { name: 'Play game clock', exact: true });
    await expect(initialPlay).toBeEnabled({ timeout: 45_000 }); await initialPlay.click();
    const pauseStarted = Date.now(); await window.getByRole('button', { name: 'Pause game clock', exact: true }).click();
    await expect(window.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible();
    const pauseMs = Date.now() - pauseStarted;
    const snowToggle = window.getByRole('checkbox', { name: 'Snow', exact: true });
    if (!await snowToggle.isVisible()) await window.getByRole('button', { name: 'Layers', exact: true }).click();
    await snowToggle.check();
    const snowRevisionBefore = await window.evaluate(() => {
      const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
        tiles?: string[] } } | undefined } }).appMap.getSource('snow');
      return Number(new URL(source?.serialize().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
    });
    const snowCells = await window.evaluate(() => Number((globalThis as typeof globalThis & {
      appSaveState?: { snowCells?: number } }).appSaveState?.snowCells ?? 0));
    await window.getByRole('button', { name: 'Open developer console' }).click();
    const snowStarted = Date.now();
    await window.getByLabel('Developer command').fill('snow add 1cm at -71.165,44.166 radius 250m');
    await window.getByLabel('Developer command').press('Enter');
    const snowCompletion = window.getByText(/Added 1 cm of fresh snow within 250 m .* across [\d,]+ terrain cells\./).last();
    await expect(snowCompletion).toBeVisible();
    const affectedCells = snowAffectedCells(await snowCompletion.textContent() ?? '');
    expect(affectedCells).toBeGreaterThan(0);
    expect(affectedCells).toBeLessThan(snowCells);
    await expect.poll(() => window.evaluate(() => {
      const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
        tiles?: string[] } } | undefined } }).appMap.getSource('snow');
      return Number(new URL(source?.serialize().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
    })).toBeGreaterThan(snowRevisionBefore);
    const snowRevisionAfter = await window.evaluate(() => {
      const source = (globalThis as typeof globalThis & { appMap: { getSource(id: string): { serialize(): {
        tiles?: string[] } } | undefined } }).appMap.getSource('snow');
      return Number(new URL(source?.serialize().tiles?.[0] ?? 'https://invalid/').searchParams.get('rev') ?? -1);
    });
    await window.evaluate(() => new Promise<void>(resolve => {
      const map = (globalThis as typeof globalThis & { appMap: { once(type: string, callback: () => void): void;
        triggerRepaint(): void } }).appMap; map.once('render', resolve); map.triggerRepaint();
    }));
    if (enabled) await expect.poll(() => window.evaluate(({ key, revision }) => {
      const state = (globalThis as typeof globalThis & Record<string, unknown>)[key] as
        { entries?: ({ stage?: string; detail?: string } | undefined)[] } | undefined;
      return state?.entries?.some(entry => entry?.stage === 'snow-tile-generated' && entry.detail
        && Number(new URL(entry.detail).searchParams.get('rev')) === revision) ?? false;
    }, { key: telemetryState, revision: snowRevisionAfter }), {
      timeout: 30_000, message: 'localized snow revision must reach a generated visible tile',
    }).toBe(true);
    const snowMs = Date.now() - snowStarted; await window.keyboard.press('Escape');
    const gradingMs = await exerciseGrading(window);
    await window.getByRole('button', { name: 'Date and time: simulation advances' }).click();
    await window.getByLabel('Advance to').selectOption('season');
    await window.getByRole('button', { name: 'Simulate Season', exact: true }).click();
    const cancel = window.getByRole('button', { name: 'Cancel', exact: true }); await expect(cancel).toBeVisible();
    const cancelStarted = Date.now(); await cancel.click();
    await expect(window.getByText(/^Advance cancelled\./)).toBeVisible(); const cancelMs = Date.now() - cancelStarted;
    await window.keyboard.press('Escape');
    await expect.poll(() => window.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
      getSource(id: string): unknown; isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
      return !!map.getSource('terrain-dem') && map.isSourceLoaded('terrain-dem') && !!map.getSource('snow')
        && map.isSourceLoaded('snow') && !!map.getSource('satellite') && map.isSourceLoaded('satellite') && map.loaded();
    }), { timeout: 45_000 }).toBe(true);
    if ((process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'cold') {
      const cdp = await window.context().newCDPSession(window);
      await cdp.send('Network.clearBrowserCache'); await cdp.detach();
    }
    await window.reload({ waitUntil: 'load' }); await window.getByRole('button', { name: /^Continue / }).click();
    await awaitLoadingComplete(window);
    await expect(window.locator('.maplibregl-canvas')).toBeVisible({ timeout: 45_000 });
    const resetPlay = window.getByRole('button', { name: 'Play game clock', exact: true });
    await expect(resetPlay).toBeEnabled({ timeout: 45_000 });
    const resetSnow = window.getByRole('checkbox', { name: 'Snow', exact: true });
    if (!await resetSnow.isVisible()) await window.getByRole('button', { name: 'Layers', exact: true }).click();
    await resetSnow.check();
    await expect.poll(() => window.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
      getSource(id: string): unknown; isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
      return !!map.getSource('terrain-dem') && map.isSourceLoaded('terrain-dem') && !!map.getSource('snow')
        && map.isSourceLoaded('snow') && map.loaded(); }), { timeout: 45_000 }).toBe(true);
    if (workflow !== 'interactive') await exerciseScenarioWorkflow(window, workflow, workload);
    const requestedSpeedControl = window.getByRole('button', { name: `${requestedSpeed}× simulation speed`, exact: true });
    await requestedSpeedControl.click(); await expect(requestedSpeedControl).toHaveAttribute('aria-pressed', 'true');
    const playAfterInteractions = window.getByRole('button', { name: 'Play game clock', exact: true });
    if (await playAfterInteractions.isVisible()) await playAfterInteractions.click();
    failureStage = 'measurement';
    await startSimulationEvidence(window);
    const stopDiagnosticProfile = heavyDiagnosticRequested()
      ? await startIntegratedDiagnosticProfile(window, { outputDir: results, runId }) : null;
    await window.waitForTimeout(warmupMs);
    const measurementPublicationStart = await simulationEvidenceCount(window);
    await startWindow(window);
    const measurementStarted = Date.now();
    if (workflow === 'interactive') await exerciseScenarioWorkflow(window, workflow, workload);
    const frames = await finishWindow(window, Math.max(0, measurementMs - (Date.now() - measurementStarted)) + 25);
    const diagnosticProfile = stopDiagnosticProfile ? await stopDiagnosticProfile() : undefined;
    const sampledPublications = await finishSimulationEvidence(window);
    const capturedPublications = await workerEntries(window, 'dualClock.worker').then(entries =>
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
    else expect(activeOccupancyMin).toBeGreaterThanOrEqual(Math.ceil(target * .95));
    if (workload !== 'aggregate' && target > 0) expect(occupancyPublications.every(publication =>
      (publication.movementCount ?? 0) >= Math.ceil(target * .95))).toBe(true);
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
      method: 'Electron renderer performance.memory.usedJSHeapSize sampled at measurement start and end' }
      : { availability: 'unavailable' as const, reason: 'performance.memory is unavailable in this Electron renderer.' };
    const processMemory = { availability: 'unavailable' as const,
      reason: 'Packaged CDP control does not expose Electron main-process memory.' };
    const objectCounts = frames.resourceSamples.map(sample => sample.domNodes);
    const workerCounts = frames.resourceSamples.map(sample => sample.workers);
    const memory = { process: processMemory, renderer: rendererMemory,
      worker: { availability: 'unavailable' as const, reason: 'Production Web Worker exposes no heap measurement API.' },
      gpu: { availability: 'unavailable' as const, reason: 'Electron GPU process memory is not actual VRAM usage.' },
      boundedCounts: {
        objects: { availability: 'available' as const, before: objectCounts[0]!, after: objectCounts.at(-1)!,
          maximum: Math.max(...objectCounts) },
        caches: { availability: 'unavailable' as const,
          reason: 'The production renderer exposes source and resource-entry counts, but no authoritative cache-object count.' },
        workers: { availability: 'available' as const, before: workerCounts[0]!, after: workerCounts.at(-1)!,
          maximum: Math.max(...workerCounts) },
      } };
    if (workflow === 'soak') {
      await expect(frames.resourceSamples.length).toBeGreaterThanOrEqual(40);
      if (rendererMemory.availability === 'available') expect((rendererMemory.afterBytes - rendererMemory.beforeBytes)
        / rendererMemory.beforeBytes).toBeLessThanOrEqual(.1);
      expect(workerCounts.at(-1)!).toBeLessThanOrEqual(workerCounts[0]!);
    }
    const presentation = await presentationCounts(window);
    if (workflow === 'visibility-near') expect(presentation.visible).toBeGreaterThanOrEqual(Math.ceil(target * .9));
    if (workflow === 'visibility-wide') expect(presentation.visible).toBeLessThan(activeOccupancy.at(-1)!);
    const sourceState = await window.evaluate(() => { const map = (globalThis as typeof globalThis & { appMap: {
      getSource(id: string): unknown; isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
      return { terrain: !!map.getSource('terrain-dem'), terrainLoaded: map.isSourceLoaded('terrain-dem'),
        snow: !!map.getSource('snow'), snowLoaded: map.isSourceLoaded('snow'), imagery: !!map.getSource('satellite'),
        imageryLoaded: map.isSourceLoaded('satellite'), mapLoaded: map.loaded() }; });
    expect(sourceState).toMatchObject({ terrain: true, terrainLoaded: true, snow: true, snowLoaded: true, mapLoaded: true });
    expect(sourceState).toMatchObject({ imagery: true, imageryLoaded: true });
    const earlyMapEvidence = await window.evaluate(() =>
      (globalThis as typeof globalThis & { integratedEarlyMapEvidence: { sources: string[]; renders: number[] } }).integratedEarlyMapEvidence);
    expect(earlyMapEvidence.sources).toContain('terrain-dem'); expect(earlyMapEvidence.sources).toContain('snow');
    expect(earlyMapEvidence.renders.length).toBeGreaterThan(0);
    expect(frames.raf.length).toBeGreaterThanOrEqual(30);
    expect(frames.renders.length).toBeGreaterThanOrEqual(30);
    const telemetry = await window.evaluate(({ key }) => {
      const state = (globalThis as typeof globalThis & Record<string, unknown>)[key] as { entries?: unknown[]; maxEntries?: number;
        size?: number; writeIndex?: number; dropped?: number } | undefined;
      if (!state?.entries || !state.maxEntries || !state.size) return { entries: [], dropped: state?.dropped ?? 0 };
      const first = state.size === state.maxEntries ? state.writeIndex ?? 0 : 0, entries = [];
      for (let index = 0; index < state.size; index++) { const entry = state.entries[(first + index) % state.maxEntries];
        if (entry) entries.push(entry); } return { entries, dropped: state.dropped ?? 0 };
    }, { key: telemetryState });
    const stages = new Set((telemetry.entries as { stage?: string }[]).map(entry => entry.stage));
    if (enabled) {
      const required = ['dual-worker-receive', 'dual-react-commit', 'dual-snow-applied',
        'snow-tile-generated', 'terrain-dem-generated'];
      if (target > 0 && workload !== 'aggregate') required.push('guest-gpu-first-draw');
      for (const stage of required) expect(stages).toContain(stage);
    } else expect(telemetry.entries).toEqual([]);
    const telemetryEntries = telemetry.entries as { stage?: string; at?: number; generation?: number;
      publicationSequence?: number; requestId?: number; committedRevision?: number }[];
    const lastDraw = enabled ? telemetryEntries.filter(entry => entry.stage === 'guest-gpu-first-draw'
      && Number(entry.at) >= frames.startedAt && Number.isInteger(entry.committedRevision)).at(-1) : undefined;
    if (lastDraw) expect(measurementPublications.some(publication => publication.id === lastDraw.requestId
      && publication.generation === lastDraw.generation
      && publication.committedRevision === lastDraw.committedRevision)).toBe(true);
    const lastDrawnRevision = lastDraw ? { availability: 'available' as const, value: lastDraw.committedRevision!,
      generation: lastDraw.generation, publicationSequence: lastDraw.publicationSequence, requestId: lastDraw.requestId,
      observedAt: lastDraw.at } : { availability: 'unavailable' as const,
      reason: enabled ? workload === 'aggregate' || workload === 'empty'
        ? 'This workload intentionally emits no detailed guest GPU frame.'
        : 'No detailed guest GPU draw occurred during this measurement window.'
        : 'Integrated telemetry was disabled for this paired condition.' };
    await window.locator('.game-menu-btn').click(); await window.locator('.hud-save').click();
    await expect(window.getByRole('alert')).toHaveCount(0);
    await expect.poll(() => window.evaluate(async key => {
      const bridge = (globalThis as typeof globalThis & { desktop?: { games?: { loadPreview(key: string): Promise<{
        ok: boolean; dataUrl?: string }> } } }).desktop;
      const result = await bridge?.games?.loadPreview(key); return result?.ok === true && !!result.dataUrl;
    }, fixture.saves[target]!.key)).toBe(true);
    await closeRelease(release);
    release = await launchRelease(executablePath, userData, scenario, enabled, runtimeEvents); window = release.page;
    await window.setViewportSize(selectedScenario.cssViewport);
    await expect.poll(() => window.evaluate(key =>
      (globalThis as typeof globalThis & Record<string, unknown>)[key], scenarioGlobal)).toEqual(scenario);
    await installTelemetry(window, enabled); await installIntegratedBenchmarkScenario(window, fixture, target, runId, requestedSpeed);
    await window.reload({ waitUntil: 'load' }); await window.getByRole('button', { name: /^Continue / }).click();
    await expect(window.locator('.maplibregl-canvas')).toBeVisible({ timeout: 45_000 });
    const reopenCycles = Math.max(1, Number(process.env.INTEGRATED_REOPEN_CYCLES ?? 1));
    for (let cycle = 1; cycle < reopenCycles; cycle++) {
      await closeRelease(release); release = await launchRelease(executablePath, userData, scenario, enabled, runtimeEvents);
      window = release.page; await window.setViewportSize(selectedScenario.cssViewport);
      await expect.poll(() => window.evaluate(key =>
        (globalThis as typeof globalThis & Record<string, unknown>)[key], scenarioGlobal)).toEqual(scenario);
      await installTelemetry(window, enabled);
      await installIntegratedBenchmarkScenario(window, fixture, target, runId, requestedSpeed);
      await window.reload({ waitUntil: 'load' }); await window.getByRole('button', { name: /^Continue / }).click();
      await expect(window.locator('.maplibregl-canvas')).toBeVisible({ timeout: 45_000 });
    }
    const runtime = await window.evaluate(() => { const canvas = document.createElement('canvas'), gl = canvas.getContext('webgl'),
      debug = gl?.getExtension('WEBGL_debug_renderer_info'); return { kind: 'electron', version: navigator.userAgent,
        glVendor: String(debug && gl?.getParameter(debug.UNMASKED_VENDOR_WEBGL) || gl?.getParameter(gl.VENDOR) || 'unavailable'),
        glRenderer: String(debug && gl?.getParameter(debug.UNMASKED_RENDERER_WEBGL) || gl?.getParameter(gl.RENDERER) || 'unavailable'),
        glBackend: '' }; }).then(value => ({ ...value, glBackend: glBackend(value.glRenderer) }));
    expect(runtime.glBackend, `unrecognized GL backend in ${runtime.glRenderer}`).not.toBeNull();
    expect(runtime.glRenderer.toLowerCase()).not.toContain('swiftshader');
    expect(requiredGlyphRequests.length).toBeGreaterThan(0); expect(externalRequests).toEqual([]);
    const artifact = fixture.manifest.artifacts[target === 0 ? 'checkpointEmpty' : target === 1000 ? 'checkpoint1000' : 'checkpoint3000'];
    const actualDisplay = { cssViewport: await window.evaluate(() => ({ width: innerWidth, height: innerHeight })),
      canvas: await window.locator('.maplibregl-canvas').evaluate(canvas => ({ width: (canvas as HTMLCanvasElement).width,
        height: (canvas as HTMLCanvasElement).height })), devicePixelRatio: await window.evaluate(() => devicePixelRatio),
      quality: await window.evaluate(() => JSON.parse(localStorage.getItem('skiapp:settings') ?? '{}').renderQuality ?? 'standard') };
    expect(actualDisplay).toEqual({ cssViewport: selectedScenario.cssViewport, canvas: selectedScenario.canvas,
      devicePixelRatio: selectedScenario.devicePixelRatio, quality: selectedScenario.profile.toLowerCase() });
    const suppliedIdentity = process.env.INTEGRATED_RUN_IDENTITY_PATH
      ? JSON.parse(readFileSync(process.env.INTEGRATED_RUN_IDENTITY_PATH, 'utf8')) as Record<string, unknown> : {};
    const identity = { ...suppliedIdentity, scenarioId: suppliedIdentity.scenarioId ?? scenarioName,
      workloadId: suppliedIdentity.workloadId ?? fixture.manifest.fixtureId,
      seed: installedScenario.seed, checkpointHash: suppliedIdentity.checkpointHash ?? artifact.sha256,
      fixtureManifestHash: createHash('sha256').update(readFileSync(path.join(fixtureRoot, 'fixture-manifest.json'))).digest('hex'),
      assetManifestHash: suppliedIdentity.assetManifestHash ?? createHash('sha256').update(JSON.stringify(fixture.manifest.artifacts)).digest('hex'),
      cacheStatePolicy: suppliedIdentity.cacheStatePolicy ?? process.env.INTEGRATED_CACHE_STATE ?? 'warm',
      profile: suppliedIdentity.profile ?? 'Standard',
      cssViewport: actualDisplay.cssViewport, canvas: actualDisplay.canvas,
      devicePixelRatio: actualDisplay.devicePixelRatio, refreshHz: Number(process.env.INTEGRATED_REFRESH_HZ ?? 60),
      machine: suppliedIdentity.machine ?? { id: process.env.INTEGRATED_MACHINE_ID ?? 'unidentified', cpu: process.env.INTEGRATED_CPU ?? 'unidentified',
        gpu: process.env.INTEGRATED_GPU ?? 'unidentified', driver: process.env.INTEGRATED_DRIVER ?? 'unidentified', os: process.env.INTEGRATED_OS ?? 'unidentified' },
      runtime: { ...(suppliedIdentity.runtime as Record<string, unknown> | undefined), ...runtime },
      source: suppliedIdentity.source ?? { commit: process.env.INTEGRATED_COMMIT ?? 'uncommitted', workingTreeStatus: process.env.INTEGRATED_WORKTREE ?? 'dirty',
        lockfileHash: process.env.INTEGRATED_LOCK_HASH ?? 'unrecorded',
        buildHash: createHash('sha256').update(readFileSync(executablePath)).digest('hex') } };
    const deliveredFrames = frames.raf.length, trial = { schemaVersion: 1, id: runId,
      pairIndex: Number(process.env.INTEGRATED_PAIR_INDEX ?? 1), order: process.env.INTEGRATED_PAIR_ORDER === 'BA' ? 'BA' : 'AB',
      condition: process.env.INTEGRATED_CONDITION === 'B' ? 'B' : 'A', telemetryEnabled: enabled,
      status: 'valid', invalidReasons: [], identity,
      metrics: { frameIntervalMs: percentile(frames.raf, .95), frameP99Ms: percentile(frames.raf, .99),
        framesOver50Ratio: frames.raf.filter(value => value > 50).length / deliveredFrames,
        longestLongTaskMs: Math.max(0, ...frames.longTasks),
        pauseResponseMs: pauseMs, workerCancellationMs: cancelMs, snowVisibleResponseMs: snowMs,
        gradingConfirmationMs: gradingMs, snowAffectedCells: affectedCells, snowSourceRevision: snowRevisionAfter,
        activeOccupancyMin, macroSecondsAdvanced, microSecondsAdvanced, workerBacklogMaxSeconds,
        occupancyMacroSecondsAdvanced, occupancyMicroSecondsAdvanced,
        workerBusyPublications, lastCommittedRevision: lastPublication.committedRevision },
      samples: { frameIntervalMs: frames.raf, mapRenderIntervalMs: frames.renders, activeOccupancy,
        longTaskMs: frames.longTasks,
        visibleActors: [presentation.visible], drawnActors: [presentation.drawn] },
      measurement: { windowDurationMs: frames.windowDurationMs, expectedFrames: Math.round(frames.windowDurationMs /
        (1000 / Number(identity.refreshHz ?? process.env.INTEGRATED_REFRESH_HZ ?? 60))),
        deliveredFrames, trailingGapAccounted: true, livenessPassed: deliveredFrames > 0 && frames.renders.length > 0,
        rafTrailingGapMs: frames.rafTrailingGapMs, renderTrailingGapMs: frames.renderTrailingGapMs },
      evidence: { executablePath, userData, sourceState, earlyMapEvidence, telemetry, requiredGlyphRequests,
        persistence: { timings: persistenceTimings, runtimeEvents },
        ...(diagnosticProfile ? { diagnosticProfile } : {}),
        cacheState: { policy: process.env.INTEGRATED_CACHE_STATE ?? 'warm', profileIsolated: true,
          checkpointRestored: true, fixtureManifestHash: suppliedIdentity.fixtureManifestHash,
          cleared: (process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'cold',
          primed: (process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'warm',
          primeCompleted: (process.env.INTEGRATED_CACHE_STATE ?? 'warm') === 'warm' },
        externalRequests, memory, resources: { samples: frames.resourceSamples },
        simulation: { publications: measurementPublications.map(publication => ({
          requestId: publication.id, generation: publication.generation,
          operationGeneration: publication.operationGeneration,
          macroSecond: publication.macroSecond, microSecond: publication.microSecond,
          active: Number((publication.flow as { active?: number } | undefined)?.active),
          movementCount: publication.movementCount, committedRevision: publication.committedRevision })),
        warmupAndMeasurementPublications: occupancyPublications.map(publication => ({ requestId: publication.id,
          generation: publication.generation, operationGeneration: publication.operationGeneration,
          macroSecond: publication.macroSecond, microSecond: publication.microSecond,
          active: Number((publication.flow as { active?: number } | undefined)?.active),
          movementCount: publication.movementCount, committedRevision: publication.committedRevision })),
        acknowledgements: measurementAcknowledgements, targetOccupancy: target, requestedSpeed,
        backlog: { availability: 'available', samples: backlogSamples } },
        presentation: { lastDrawnRevision,
        visibleActors: presentation.visible, drawnActors: presentation.drawn },
        measurementPublications, firstPublication, lastPublication } };
    mkdirSync(results, { recursive: true }); writeFileSync(path.join(results, `${runId}.trial.json`), `${JSON.stringify(trial, null, 2)}\n`);
  } catch (error) {
    primaryError = error;
    const observations: Record<string, unknown> = { runtimeEvents,
      stdout: release?.control.stdout.slice(-8_192), stderr: release?.control.stderr.slice(-8_192),
      exitCode: release?.control.exitCode };
    if (release) try { observations.url = release.page.url();
      observations.body = (await release.page.locator('body').innerText()).slice(0, 8_192); }
    catch (observationError) { observations.observationError = observationError instanceof Error
      ? observationError.message : String(observationError); }
    retainIntegratedFailure(results, runId, failureStage, error, observations);
    throw error;
  } finally {
    failureStage = 'teardown';
    if (release) try { await closeRelease(release); }
    catch (error) {
      retainIntegratedFailure(results, runId, 'teardown', error, { runtimeEvents,
        stdout: release.control.stdout.slice(-8_192), stderr: release.control.stderr.slice(-8_192),
        exitCode: release.control.exitCode });
      if (!primaryError) throw error;
    }
  }
});
