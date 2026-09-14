import { expect, test } from '../support/deterministicApp';
import { _electron as electron, type Page } from '@playwright/test';
import { seedPreparedResort, seedPreparedResortStorage, preparedTerrainFixture } from '../support/preparedResort';
import { installWorkerProbe, workerEntries } from '../support/workerProbe';
import { DualClockEngine } from '../../../src/dualClock/engine';
import { dualFixture, fixtureHour } from '../../../src/dualClock/fixtures';
import { generateBareSnowGrid } from '../../../src/snow';
import { weatherTerrainBinding } from '../../../src/weather/terrainBinding';
import { existsSync, mkdirSync, writeFileSync } from 'node:fs';
import { mkdtempSync, rmSync } from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { startStaticProductionServer, stopStaticProductionServer, verifiedDescendant } from '../support/saveLoadingBenchmark';

const WEATHER_YEAR = 1992;

function dualFixtureForBrowser(demand: number) {
  const terrain = preparedTerrainFixture();
  const input = dualFixture(demand, 2);
  input.at = '2026-11-02T08:00:00.000Z';
  input.terrain = terrain;
  input.snow = generateBareSnowGrid(terrain);
  input.snow.depthM.fill(0.5);
  input.snow.surface.fill(1);
  input.weather = Array.from({ length: 72 }, (_, index) =>
    fixtureHour(new Date(Date.UTC(WEATHER_YEAR, 0, 1, index)).toISOString()));
  const checkpoint = new DualClockEngine(input).checkpoint();
  const weather = { terrainKey: terrain.key, manifest: {
    schemaVersion: 1, terrainKey: terrain.key, terrainBinding: weatherTerrainBinding(terrain), timezone: 'UTC',
    historicalStartYear: WEATHER_YEAR, historicalEndYear: WEATHER_YEAR, quality: 'limited', sourceSummary: 'fixture',
    sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'dual-browser-fixture', complete: true,
    createdAt: '2026-01-01T00:00:00.000Z',
  }, historicalYears: [{ year: WEATHER_YEAR, hours: input.weather }] };
  return { checkpoint, weather };
}

async function putDualSave(page: Parameters<typeof seedPreparedResort>[0], demand: number): Promise<void> {
  const { checkpoint, weather } = dualFixtureForBrowser(demand);
  await page.evaluate(async ({ checkpoint, weather }) => {
    const save = JSON.parse(localStorage.getItem('gamesave:e2e-save')!);
    save.schemaVersion = 17;
    save.dualClock = checkpoint;
    localStorage.setItem('gamesave:e2e-save', JSON.stringify(save));
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open('mountain-planner-weather', 3);
      request.onupgradeneeded = () => {
        for (const [name, keyPath] of [['packages', 'terrainKey'], ['manifests', 'contentHash'], ['chunks', 'id'], ['active', 'terrainKey']] as const) {
          if (!request.result.objectStoreNames.contains(name)) {
            const store = request.result.createObjectStore(name, { keyPath });
            if (name === 'chunks') store.createIndex('contentHash', 'contentHash', { unique: false });
          }
        }
      };
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result, tx = db.transaction('packages', 'readwrite');
        tx.objectStore('packages').put(weather);
        tx.onerror = () => reject(tx.error);
        tx.oncomplete = () => { db.close(); resolve(); };
      };
    });
    const desktop = (window as unknown as { desktop?: { weather?: { save(value: unknown): Promise<unknown> };
      games?: { save(value: unknown): Promise<unknown> } } }).desktop;
    if (desktop?.weather?.save) await desktop.weather.save(weather);
    if (desktop?.games?.save) await desktop.games.save(save);
  }, { checkpoint, weather });
  await page.reload({ waitUntil: 'load' });
}

async function openPrepared(page: Parameters<typeof seedPreparedResort>[0], demand: number): Promise<void> {
  await seedPreparedResort(page);
  await putDualSave(page, demand);
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 30_000 });
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible({ timeout: 30_000 });
}

async function waitForPreparedWeatherReady(page: Page): Promise<void> {
  const play = page.getByRole('button', { name: 'Play game clock', exact: true });
  await expect(play).toBeVisible({ timeout: 30_000 });
  await expect(play).toBeEnabled({ timeout: 30_000 });
  await expect.poll(() => workerEntries(page, 'preparedWeather.worker').then((entries) => entries.length), { timeout: 30_000 }).toBe(1);
}

function validatedBaselineRoot(): string {
  const configured = process.env.SAVE_LOADING_BASELINE_DIR;
  if (!configured) throw new Error('SAVE_LOADING_BASELINE_DIR must point to a frozen baseline containing dist and dist-electron');
  const root = path.resolve(configured);
  const required = [path.join(root, 'dist', 'index.html'), path.join(root, 'dist-electron', 'main.js')];
  if (required.some((entry) => !existsSync(entry))) {
    throw new Error(`SAVE_LOADING_BASELINE_DIR is invalid; expected ${path.join(root, 'dist')} and ${path.join(root, 'dist-electron')}`);
  }
  return root;
}

async function expectPlayEnabled(page: Parameters<typeof seedPreparedResort>[0], build: 'baseline' | 'current'): Promise<void> {
  if (build === 'current') {
    await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeEnabled({ timeout: 30_000 });
    return;
  }
  const baseline = page.getByRole('button', { name: /^(Play game clock|Skip to Winter|Complete planning and skip to September 1)$/ }).first();
  await expect(baseline).toBeVisible({ timeout: 30_000 });
  await expect(baseline).toBeEnabled({ timeout: 30_000 });
}

async function installLoadingMetrics(page: Parameters<typeof seedPreparedResort>[0], delayMs = 0): Promise<void> {
  await page.addInitScript(({ delay }) => {
    const metrics = { longTasks: [] as number[], weatherPosts: [] as number[],
      longTaskEvents: [] as { start: number; duration: number }[], weatherPostEvents: [] as { start: number; duration: number }[],
      preparedWeatherCompletes: [] as number[], preparedWeatherCacheBytes: [] as boolean[], weatherAcks: [] as number[], dualPublications: [] as number[] };
    (window as unknown as { saveLoadingMetrics: typeof metrics }).saveLoadingMetrics = metrics;
    if ('PerformanceObserver' in window) {
      new PerformanceObserver((list) => {
        for (const entry of list.getEntries()) { metrics.longTasks.push(entry.duration); metrics.longTaskEvents.push({ start: entry.startTime, duration: entry.duration }); }
      }).observe({ type: 'longtask', buffered: true });
    }
    const NativeWorker = window.Worker;
    window.Worker = new Proxy(NativeWorker, {
      construct(target, args) {
        const worker = Reflect.construct(target, args) as Worker;
        const workerUrl = String(args[0]);
        worker.addEventListener('message', (event: MessageEvent<unknown>) => {
          const data = event.data as { type?: string; weatherAck?: unknown; publication?: unknown };
          if (workerUrl.includes('preparedWeather.worker') && data.type === 'complete') {
            metrics.preparedWeatherCompletes.push(performance.now());
            metrics.preparedWeatherCacheBytes.push(!!(data as { cacheBytes?: unknown }).cacheBytes);
          }
          if (workerUrl.includes('dualClock.worker')) {
            if (data.weatherAck) metrics.weatherAcks.push(performance.now());
            if (data.publication) metrics.dualPublications.push(performance.now());
          }
        });
        if (String(args[0]).includes('preparedWeather.worker')) {
          const post = worker.postMessage.bind(worker);
          Object.defineProperty(worker, 'postMessage', { configurable: true, value(message: unknown, transfer?: Transferable[]) {
            const start = performance.now();
            if (delay && typeof (message as { storageMode?: unknown })?.storageMode === 'string') {
              setTimeout(() => post(message, transfer ?? []), delay);
            } else post(message, transfer ?? []);
            const duration = performance.now() - start;
            metrics.weatherPosts.push(duration); metrics.weatherPostEvents.push({ start, duration });
          } });
        }
        return worker;
      },
    });
  }, { delay: delayMs });
}

test.describe('schema-17 save loading', () => {
  for (const demand of [0, 1000, 3000]) {
    test(`restores a bounded visible scene with ${demand} guests`, async ({ page }) => {
      test.setTimeout(90_000);
      await installLoadingMetrics(page);
      await installWorkerProbe(page);
      await openPrepared(page, demand);
      await waitForPreparedWeatherReady(page);
      const preparedWorkers = await workerEntries(page, 'preparedWeather.worker');
      expect(preparedWorkers.length).toBe(1);
      await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeEnabled();
      const metrics = await page.evaluate(() => (window as unknown as { saveLoadingMetrics: { longTasks: number[]; weatherPosts: number[] } }).saveLoadingMetrics);
      mkdirSync('test-results/save-loading', { recursive: true });
      writeFileSync(`test-results/save-loading/guest-${demand}-metrics.json`, JSON.stringify(metrics, null, 2));
      expect(Math.max(0, ...metrics.weatherPosts)).toBeLessThan(50);
    });
  }

  test('cold miss warms the cache and same-year reload deduplicates', async ({ page }) => {
    test.setTimeout(120_000);
    await installLoadingMetrics(page);
    await installWorkerProbe(page);
    await openPrepared(page, 0);
    await waitForPreparedWeatherReady(page);
    const coldCacheEvidence = await page.evaluate(() => (window as unknown as { saveLoadingMetrics: { preparedWeatherCacheBytes: boolean[] } }).saveLoadingMetrics.preparedWeatherCacheBytes);
    expect(coldCacheEvidence).toContain(true);
    expect((await workerEntries(page, 'preparedWeather.worker')).length).toBe(1);
    await page.reload({ waitUntil: 'load' });
    await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 30_000 });
    await waitForPreparedWeatherReady(page);
    const warmCacheEvidence = await page.evaluate(() => (window as unknown as { saveLoadingMetrics: { preparedWeatherCacheBytes: boolean[] } }).saveLoadingMetrics.preparedWeatherCacheBytes);
    expect(warmCacheEvidence).toEqual([false]);
    expect((await workerEntries(page, 'preparedWeather.worker')).length).toBe(1);
    await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeEnabled();
  });

  test('legacy saves retain the old controls and schema-16 write path', async ({ page }) => {
    await seedPreparedResort(page);
    await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 30_000 });
    await expect(page.locator('.tb-speed')).toHaveCount(4);
    await expect(page.getByRole('button', { name: 'Date and time: simulation advances' })).toHaveCount(0);
    await page.locator('.game-menu-btn').click();
    await page.locator('.hud-save').click();
    await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null')?.schemaVersion)).toBe(16);
  });

  test('delayed weather leaves the restored map inspectable and pending exit skips checkpointing', async ({ page }) => {
    test.setTimeout(90_000);
    await installLoadingMetrics(page, 3_000);
    await seedPreparedResort(page);
    await putDualSave(page, 0);
    await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 30_000 });
    const play = page.getByRole('button', { name: 'Play game clock', exact: true });
    await expect(play).toBeVisible();
    await expect(play).toBeDisabled();
    await page.evaluate(() => {
      const map = (window as unknown as { appMap?: { jumpTo(options: unknown): void } }).appMap;
      map?.jumpTo({ center: [-121.495, 46.905], zoom: 13 });
    });
    await page.locator('.game-menu-btn').click();
    await page.getByRole('menuitem', { name: 'Main Menu' }).click();
    await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
  });

  test('frozen and current builds compare configured weather cold/warm loading at each guest scale', async ({ browser }) => {
    test.skip(process.env.SAVE_LOADING_BENCHMARK !== '1', 'Set SAVE_LOADING_BENCHMARK=1 to run the production comparison');
    test.setTimeout(180_000);
    const baselineDirectory = path.join(validatedBaselineRoot(), 'dist');
    const currentDirectory = path.resolve('dist');
    const baseline = await startStaticProductionServer(baselineDirectory, 44174);
    const current = await startStaticProductionServer(currentDirectory, 44175);
    const measure = async (origin: string, demand: number, build: 'baseline' | 'current') => {
      const context = await browser.newContext({ baseURL: origin, viewport: { width: 1920, height: 1080 } });
      const benchmarkPage = await context.newPage();
      try {
        await installLoadingMetrics(benchmarkPage);
        await installWorkerProbe(benchmarkPage);
        await seedPreparedResort(benchmarkPage);
        await putDualSave(benchmarkPage, demand);
        const samples: { phase: 'cold' | 'warm'; cacheEvidence: 'miss' | 'hit' | 'unsupported'; overlayDismissalMs: number; terrainReadyMs: number;
          simulationRestoredMs: number | null; weatherAckMs: number | null; visibleSceneMs: number;
          playEnabledMs: number; maxLongTaskMs: number; maxWeatherPostMs: number; maxWeatherAttributedTaskMs: number;
          preparedWorkers: number }[] = [];
        const passCount = demand === 3000 ? 6 : 3;
        for (let pass = 0; pass < passCount; pass += 1) {
          await benchmarkPage.goto('/?flat', { waitUntil: 'load' });
          if (pass > 0) await benchmarkPage.evaluate(() => {
            const key = 'gamesave:e2e-save';
            const save = JSON.parse(localStorage.getItem(key) ?? 'null') as Record<string, unknown> | null;
            if (save) { delete save.weatherRun; localStorage.setItem(key, JSON.stringify(save)); }
          });
          const started = await benchmarkPage.evaluate(() => {
            const metrics = (window as unknown as { saveLoadingMetrics: { bootStart?: number } }).saveLoadingMetrics;
            metrics.bootStart = performance.now(); return metrics.bootStart;
          });
          const metricsBefore = await benchmarkPage.evaluate(() => {
            const metrics = (window as unknown as { saveLoadingMetrics: { longTaskEvents: { start: number; duration: number }[]; weatherPostEvents: { start: number; duration: number }[]; weatherAcks: number[]; preparedWeatherCacheBytes: boolean[] } }).saveLoadingMetrics;
            return { longTasks: metrics.longTaskEvents.length, posts: metrics.weatherPostEvents.length, acks: metrics.weatherAcks.length, cache: metrics.preparedWeatherCacheBytes.length };
          });
          await benchmarkPage.getByRole('button', { name: /^Continue / }).click();
          const saveReadCompleteMs = await benchmarkPage.evaluate((start) => performance.now() - start, started);
          await expect(benchmarkPage.locator('.resort-loading')).toHaveCount(0, { timeout: 30_000 });
          const overlayDismissalMs = await benchmarkPage.evaluate((start) => performance.now() - start, started);
          const terrainReadyMs = overlayDismissalMs;
          const preparedWorkers = (await workerEntries(benchmarkPage, 'preparedWeather.worker')).length;
          let weatherAckMs: number | null = null;
          if (preparedWorkers > 0) {
            await expect.poll(async () => (await benchmarkPage.evaluate((before) => (window as unknown as { saveLoadingMetrics: { weatherAcks: number[] } }).saveLoadingMetrics.weatherAcks.length, metricsBefore.acks))).toBeGreaterThan(metricsBefore.acks);
            weatherAckMs = await benchmarkPage.evaluate((before) => {
              const metrics = (window as unknown as { saveLoadingMetrics: { weatherAcks: number[] } }).saveLoadingMetrics;
              return metrics.weatherAcks.slice(before).at(-1)! - (window as unknown as { saveLoadingMetrics: { bootStart: number } }).saveLoadingMetrics.bootStart;
            }, metricsBefore.acks);
          }
          const dualStarted = performance.now();
          let simulationRestoredMs: number | null = null;
          if ((await workerEntries(benchmarkPage, 'dualClock.worker')).length > 0) {
            await expect.poll(async () => (await workerEntries(benchmarkPage, 'dualClock.worker')).some((entry) => (entry.publications?.length ?? 0) > 0)).toBe(true);
            simulationRestoredMs = performance.now() - dualStarted;
          }
          await expectPlayEnabled(benchmarkPage, build);
          const playEnabledMs = await benchmarkPage.evaluate((start) => performance.now() - start, started);
          const metrics = await benchmarkPage.evaluate(() => (window as Window & { saveLoadingMetrics?: {
            longTasks: number[]; weatherPosts: number[]; longTaskEvents: { start: number; duration: number }[];
            weatherPostEvents: { start: number; duration: number }[]; weatherAcks: number[]; preparedWeatherCacheBytes: boolean[];
          } }).saveLoadingMetrics ?? { longTasks: [], weatherPosts: [], longTaskEvents: [], weatherPostEvents: [], weatherAcks: [], preparedWeatherCacheBytes: [] });
          const postEvents = metrics.weatherPostEvents.slice(metricsBefore.posts);
          const ackTimes = metrics.weatherAcks.slice(metricsBefore.acks);
          const weatherStart = postEvents[0]?.start ?? Number.POSITIVE_INFINITY;
          const weatherEnd = ackTimes.at(-1) ?? Number.NEGATIVE_INFINITY;
          const weatherTasks = metrics.longTaskEvents.filter((entry) => entry.start < weatherEnd && entry.start + entry.duration > weatherStart);
          const cacheBytes = metrics.preparedWeatherCacheBytes.slice(metricsBefore.cache);
          samples.push({ phase: pass === 0 ? 'cold' : 'warm', cacheEvidence: cacheBytes.length === 0 ? 'unsupported' : cacheBytes.some(Boolean) ? 'miss' : 'hit', overlayDismissalMs, terrainReadyMs,
            simulationRestoredMs, weatherAckMs, visibleSceneMs: overlayDismissalMs, playEnabledMs: Math.max(saveReadCompleteMs, playEnabledMs),
            maxLongTaskMs: Math.max(0, ...metrics.longTasks), maxWeatherPostMs: Math.max(0, ...metrics.weatherPosts),
            maxWeatherAttributedTaskMs: Math.max(0, ...weatherTasks.map((entry) => entry.duration)), preparedWorkers });
        }
        const warm = samples.slice(1);
        const sortedWarm = [...warm].sort((left, right) => left.overlayDismissalMs - right.overlayDismissalMs);
        return { demand, samples, cold: samples[0]!, warm, warmMedianMs: sortedWarm[Math.floor(sortedWarm.length / 2)]?.overlayDismissalMs ?? null };
      } finally {
        await context.close();
      }
    };
    try {
      const before = [] as Awaited<ReturnType<typeof measure>>[];
      const after = [] as Awaited<ReturnType<typeof measure>>[];
      for (const demand of [0, 1000, 3000]) before.push(await measure('http://127.0.0.1:44174', demand, 'baseline'));
      for (const demand of [0, 1000, 3000]) after.push(await measure('http://127.0.0.1:44175', demand, 'current'));
      const report = { fixture: { guestCounts: [0, 1000, 3000], weatherIdentity: 'dual-browser-fixture', cachePhases: ['cold', 'warm'],
          baselineAnnualGenerationEvidence: 'unavailable: frozen build exposes no prepared-weather worker' },
        settings: { viewport: { width: 1920, height: 1080 }, chromiumArgs: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] },
        milestones: { baseline: before, current: after, sceneSplit: 'Visible scene is recorded at overlay dismissal; simulation and weather ACK are recorded independently when exposed.',
          weatherTaskAttribution: 'broad readiness-delay request-to-ACK interval overlap; this does not prove weather CPU cost' },
        comparison: { warmReductionTargetPct: 50, result: 'not comparable', reason: 'The frozen build has no prepared-weather worker boundary; report timings without claiming a reduction.' },
        baseline: before, current: after };
      mkdirSync('test-results/save-loading', { recursive: true });
      writeFileSync('test-results/save-loading/overlay-benchmark.json', JSON.stringify(report, null, 2));
      expect(report.current).toHaveLength(3);
      for (const build of report.current) {
        expect(build.samples[0]?.cacheEvidence).toBe('miss');
        expect(build.samples.slice(1).every((sample) => sample.cacheEvidence === 'hit')).toBe(true);
      }
      expect(report.current.find((build) => build.demand === 3000)?.warm).toHaveLength(5);
    } finally {
      await stopStaticProductionServer(current);
      await stopStaticProductionServer(baseline);
    }
  });

  test('Electron compares isolated cold, warm, and restart loading for both builds', async () => {
    test.skip(process.env.RUN_ELECTRON_E2E !== '1', 'Set RUN_ELECTRON_E2E=1 to run isolated Electron loading');
    test.setTimeout(180_000);
    const bootstrap = path.resolve('tests/e2e/support/electronSaveLoadingBootstrap.cjs');
    const baselineRoot = validatedBaselineRoot();
    const builds = [
      ['baseline', path.join(baselineRoot, 'dist-electron', 'main.js')],
      ['current', path.resolve('dist-electron/main.js')],
    ] as const;
    const verified: Array<{ build: string; root: string; paths: Record<string, string>; coldMs: number; warmMs: number; restartMs: number }> = [];
    for (const [build, main] of builds) {
      const root = mkdtempSync(path.join(os.tmpdir(), `mountain-planner-save-loading-${build}-`));
      const electronEnv = { ...process.env };
      delete electronEnv.ELECTRON_RUN_AS_NODE;
      const application = await electron.launch({ executablePath: path.resolve('node_modules/electron/dist/electron.exe'), args: ['--no-sandbox', bootstrap], env: {
        ...electronEnv, SAVE_LOADING_ELECTRON_USER_DATA: root, SAVE_LOADING_ELECTRON_MAIN: main,
      } });
      let applicationClosed = false;
      try {
        const firstWindow = await application.firstWindow();
        await expect(firstWindow.getByRole('navigation', { name: 'Main menu' })).toBeVisible({ timeout: 45_000 });
        const paths = await application.evaluate(({ app }) => ({ userData: app.getPath('userData'),
          saves: `${app.getPath('userData')}/saves`, weather: `${app.getPath('userData')}/weather`,
          terrains: `${app.getPath('userData')}/terrains` }));
        expect(paths.userData).toBe(root);
        expect(verifiedDescendant(root, paths.saves)).toBe(true);
        expect(verifiedDescendant(root, paths.weather)).toBe(true);
        expect(verifiedDescendant(root, paths.terrains)).toBe(true);
        await seedPreparedResortStorage(firstWindow);
        await putDualSave(firstWindow, 0);
        const coldStarted = Date.now();
        await firstWindow.getByRole('button', { name: /^Continue / }).click();
        await expect(firstWindow.locator('.resort-loading')).toHaveCount(0, { timeout: 60_000 });
        await expect(firstWindow.getByRole('button', { name: 'Play game clock', exact: true })).toBeEnabled({ timeout: 30_000 });
        const coldMs = Date.now() - coldStarted;
        await firstWindow.reload({ waitUntil: 'load' });
        const warmStarted = Date.now();
        await firstWindow.getByRole('button', { name: /^Continue / }).click();
        await expect(firstWindow.locator('.resort-loading')).toHaveCount(0, { timeout: 60_000 });
        await expect(firstWindow.getByRole('button', { name: 'Play game clock', exact: true })).toBeEnabled({ timeout: 30_000 });
        const warmMs = Date.now() - warmStarted;
        await application.close();
        applicationClosed = true;
        const restarted = await electron.launch({ executablePath: path.resolve('node_modules/electron/dist/electron.exe'), args: ['--no-sandbox', bootstrap], env: {
          ...electronEnv, SAVE_LOADING_ELECTRON_USER_DATA: root, SAVE_LOADING_ELECTRON_MAIN: main,
        } });
        try {
          const restartWindow = await restarted.firstWindow();
          await expect(restartWindow.getByRole('navigation', { name: 'Main menu' })).toBeVisible({ timeout: 45_000 });
          const restartStarted = Date.now();
          await restartWindow.getByRole('button', { name: /^Continue / }).click();
          await expect(restartWindow.locator('.resort-loading')).toHaveCount(0, { timeout: 60_000 });
          await expect(restartWindow.getByRole('button', { name: 'Play game clock', exact: true })).toBeEnabled({ timeout: 30_000 });
          verified.push({ build, root, paths, coldMs, warmMs, restartMs: Date.now() - restartStarted });
        } finally { await restarted.close(); }
      } finally {
        if (!applicationClosed) await application.close();
        rmSync(root, { recursive: true, force: true });
      }
    }
    mkdirSync('test-results/save-loading', { recursive: true });
    writeFileSync('test-results/save-loading/electron-isolation.json', JSON.stringify({ builds: verified }, null, 2));
  });
});
