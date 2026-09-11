import { expect, test } from '../support/deterministicApp';
import { seedPreparedResort, preparedTerrainFixture } from '../support/preparedResort';
import { installWorkerProbe, workerEntries } from '../support/workerProbe';
import { DualClockEngine } from '../../../src/dualClock/engine';
import { dualFixture, fixtureHour, fixtureTerrain } from '../../../src/dualClock/fixtures';
import { weatherTerrainBinding } from '../../../src/weather/terrainBinding';
import type { DualWorkerResponse } from '../../../src/app/dualClockProtocol';
import { buildSkiNetwork } from '../../../src/network';
import { sanitizeTrails } from '../../../src/trails';
import { generateBareSnowGrid } from '../../../src/snow';
import type { SavedLift } from '../../../src/types/lifts';
import type { SavedTrail } from '../../../src/types/trails';
import { mkdirSync, writeFileSync } from 'node:fs';

if (process.env.DUAL_CLOCK_GPU === '1') test.use({ launchOptions: { args: ['--use-gl=angle', '--use-angle=d3d11'] } });

test('dual clocks expose macro time, all presets, warnings and precise paused saves', async ({ page }) => {
  test.setTimeout(90_000);
  await installWorkerProbe(page);
  const fixture = dualFixture(0, 2), terrain = preparedTerrainFixture();
  fixture.at = '2026-05-01T07:00:00.000Z';
  const base: [number, number] = [-121.495, 46.902], top: [number, number] = [-121.495, 46.908];
  const lift: SavedLift = { id: 'dual-save-lift', identifier: 'A', name: 'Save Lift', liftTypeId: 'fixed-grip-quad',
    points: [base, top], endpointElevM: [1000, 1250], lengthM: 670, verticalM: 250,
    status: 'complete', createdAt: '2026-01-01T00:00:00.000Z' };
  const trail: SavedTrail = sanitizeTrails([{ id: 'dual-save-trail', name: 'Save Run', brushWidthM: 36,
    areaM2: 24_000, lengthM: 670, verticalM: 250, avgSlopeDeg: 22, maxSlopeDeg: 22,
    difficulty: 'blue', status: 'complete', createdAt: '2026-01-01T00:00:00.000Z', parts: [{
      polygon: [[[-121.4954, 46.9018], [-121.4946, 46.9018], [-121.4946, 46.9082],
        [-121.4954, 46.9082], [-121.4954, 46.9018]]], centerline: [top, base], centerlineElevM: [1250, 1000],
    }] }])[0]!;
  const network = buildSkiNetwork([trail], [lift]);
  fixture.snow = generateBareSnowGrid(terrain); fixture.snow.depthM.fill(1); fixture.snow.surface.fill(1);
  fixture.resort = { ...fixture.resort, edges: network.edges, trails: [trail], dailyDemand: 900,
    portal: { id: 'dual-save-portal', nodeId: network.edges.find(edge => edge.kind === 'lift')!.from,
      lngLat: base, capacityPerMinute: 1000 } };
  await seedPreparedResort(page, { lifts: [lift], trails: [trail] });
  const checkpoint = new DualClockEngine(fixture).checkpoint();
  const weather = { terrainKey: terrain.key, manifest: {
    schemaVersion: 1, terrainKey: terrain.key, terrainBinding: weatherTerrainBinding(terrain), timezone: 'UTC',
    historicalStartYear: 1991, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture',
    sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'dual-browser-fixture', complete: true,
    createdAt: '2026-01-01T00:00:00.000Z',
  }, historicalYears: [{ year: 1992, hours: Array.from({ length: 72 }, (_, i) => fixtureHour(new Date(Date.UTC(1992, 0, 1, i)).toISOString())) }] };
  await page.evaluate(async ({ checkpoint, weather }) => {
    const save = JSON.parse(localStorage.getItem('gamesave:e2e-save')!);
    save.schemaVersion = 17; save.dualClock = checkpoint;
    localStorage.setItem('gamesave:e2e-save', JSON.stringify(save));
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open('mountain-planner-weather', 3);
      request.onupgradeneeded = () => {
        for (const [name, keyPath] of [['packages', 'terrainKey'], ['manifests', 'contentHash'], ['chunks', 'id'], ['active', 'terrainKey']]) {
          if (!request.result.objectStoreNames.contains(name)) {
            const store = request.result.createObjectStore(name, { keyPath });
            if (name === 'chunks') store.createIndex('contentHash', 'contentHash', { unique: false });
          }
        }
      };
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const db = request.result, tx = db.transaction('packages', 'readwrite'); tx.objectStore('packages').put(weather);
        tx.oncomplete = () => { db.close(); resolve(); }; tx.onerror = () => reject(tx.error);
      };
    });
  }, { checkpoint, weather });
  await page.reload();
  await page.getByRole('button', { name: /^Continue / }).click();
  const clock = page.getByRole('button', { name: 'Date and time: simulation advances' });
  await expect(clock).toBeVisible();
  await expect(clock).toContainText(/May 1/);
  await clock.click();
  await expect(page.getByLabel('Advance to')).toHaveValue('winter');
  await page.getByRole('button', { name: 'Simulate to Winter', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Advance simulation' })).toContainText('Advance completed', { timeout: 30_000 });
  await expect(clock).toContainText(/Nov 2/);
  await page.keyboard.press('Escape');

  // Regression: saving after the May-to-winter jump used to call the legacy
  // clock serializer and throw before the save handler could report an error.
  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Pause game clock', exact: true })).toBeVisible();
  await expect.poll(async () => workerEntries(page, 'dualClock.worker').then(entries => entries
    .some(entry => entry.publications?.some(publication =>
      ((publication.flow as { active?: number } | undefined)?.active ?? 0) > 0))), { timeout: 20_000 }).toBe(true);
  await page.locator('.game-menu-btn').click(); await page.locator('.hud-save').click();
  await expect(page.getByRole('alert')).toHaveCount(0);
  await expect.poll(() => page.evaluate(async () => new Promise<{
    macroSecond: number; at: string; paused: boolean; snowCells: number
  } | null>((resolve) => {
    const open = indexedDB.open('mountain-planner-dual-saves', 1);
    open.onsuccess = () => { const db = open.result;
      const request = db.transaction('games').objectStore('games').get('e2e-save');
      request.onsuccess = () => { const save = request.result; db.close(); resolve(save?.dualClock ? {
        macroSecond: save.dualClock.clock.macroSecond, at: save.dualClock.clock.at,
        paused: save.dualClock.clock.paused, snowCells: save.dualClock.snow?.depthM?.length ?? 0,
      } : null); };
    };
  })), { timeout: 20_000 }).toMatchObject({ paused: true });
  const firstSaved = await page.evaluate(async () => new Promise<{ macroSecond: number; at: string; snowCells: number }>((resolve) => {
    const open = indexedDB.open('mountain-planner-dual-saves', 1);
    open.onsuccess = () => { const db = open.result;
      const request = db.transaction('games').objectStore('games').get('e2e-save');
      request.onsuccess = () => { const save = request.result; db.close(); resolve({
        macroSecond: save.dualClock.clock.macroSecond, at: save.dualClock.clock.at,
        snowCells: save.dualClock.snow.depthM.length }); };
    };
  }));
  expect(firstSaved.macroSecond).toBeGreaterThan(24 * 43_200);
  expect(firstSaved.at).toMatch(/^2026-11-02/);
  expect(firstSaved.snowCells).toBeGreaterThan(0);

  for (const speed of [1, 2, 4, 8, 16, 64]) {
    const control = page.getByRole('button', { name: `${speed}× simulation speed`, exact: true });
    await expect(control).toBeEnabled({ timeout: 20_000 });
    await control.click();
    await expect(control).toHaveAttribute('aria-pressed', 'true');
  }
  await page.getByRole('button', { name: /^Warnings/ }).click();
  await expect(page.getByRole('region', { name: 'Mountain warnings' })).toContainText('Mountain warnings');
  await page.keyboard.press('Escape');
  await clock.click();
  await expect(page.getByRole('dialog', { name: 'Advance simulation' })).toBeVisible();
  await expect(page.getByLabel('Advance to')).toHaveValue('day');
  await page.getByRole('button', { name: 'Simulate Day', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'Advance simulation' })).toContainText('Advance completed', { timeout: 30_000 });
  await expect(clock).toContainText(/Nov 3/);
  await page.keyboard.press('Escape');
  await page.locator('.game-menu-btn').click(); await page.locator('.hud-save').click();
  await expect.poll(() => page.evaluate(async () => new Promise<number | null>((resolve) => {
    const open = indexedDB.open('mountain-planner-dual-saves', 1);
    open.onsuccess = () => { const db = open.result;
      if (!db.objectStoreNames.contains('games')) { db.close(); resolve(null); return; }
      const request = db.transaction('games').objectStore('games').get('e2e-save');
      request.onsuccess = () => { db.close(); resolve(request.result?.schemaVersion ?? null); };
    };
  }))).toBe(17);
  await page.reload();
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(clock).toContainText(/Nov 3/);
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: '64× simulation speed', exact: true })).toBeEnabled();
  await clock.click(); await page.getByLabel('Advance to').selectOption('season');
  await page.getByRole('button', { name: 'Simulate Season', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Cancel', exact: true })).toBeVisible();
  const visualCancelMs = await page.evaluate(async () => {
    const dialog = document.querySelector('[aria-label="Advance simulation"]')!;
    const cancel = [...dialog.querySelectorAll('button')].find(button => button.textContent === 'Cancel')!;
    const start = performance.now(); cancel.click();
    return await new Promise<number>((resolve, reject) => {
      const check = () => {
        if (dialog.textContent?.includes('Advance cancelled')) resolve(performance.now() - start);
        else if (performance.now() - start > 1000) reject(new Error('Cancel did not update the advance UI'));
        else requestAnimationFrame(check);
      }; requestAnimationFrame(check);
    });
  });
  // SwiftShader is the deterministic CI renderer; certify the performance budget on hardware.
  expect(visualCancelMs).toBeLessThan(process.env.DUAL_CLOCK_GPU === '1' ? 100 : 1000);
  await page.keyboard.press('Escape');

  // Exercise the shipped persistent worker, including cancellation while snow is staged.
  const entries = await workerEntries(page, 'dualClock.worker'); expect(entries.length).toBeGreaterThan(0);
  const input = dualFixture(1000, 128); input.terrain = fixtureTerrain(input);
  input.weather = Array.from({ length: 168 * 24 + 1 }, (_, i) => fixtureHour(new Date(Date.parse(input.at) + i * 3600000).toISOString()));
  const result = await page.evaluate(async ({ url, input }) => {
    const prepared = { ...input, snow: { ...input.snow!, depthM: new Float32Array(input.snow!.depthM), surface: new Uint8Array(input.snow!.surface) } };
    const worker = new Worker(url, { type: 'module' });
    const replies: DualWorkerResponse[] = []; let cancelAt = 0, movement = false;
    return await new Promise<{ elapsed: number; paused: boolean; coherent: boolean; headless: boolean; movement: boolean }>((resolve, reject) => {
      const timeout = setTimeout(() => { worker.terminate(); reject(new Error('Worker cancellation timed out')); }, 10_000);
      worker.onerror = error => reject(new Error(error.message));
      worker.onmessage = ({ data }: MessageEvent<DualWorkerResponse>) => {
        replies.push(data);
        if (data.type === 'error') { clearTimeout(timeout); worker.terminate(); reject(new Error(data.error)); return; }
        if (data.id === 1) {
          movement = !!data.movement?.count && Number.isFinite(new Float64Array(data.movement.buffer, 0, 2)[0]);
          if (data.movement) worker.postMessage({ generation: 99, requestId: 1, committedRevision: data.committedRevision,
            type: 'recycle-movement', buffer: data.movement.buffer }, [data.movement.buffer]);
          worker.postMessage({ generation: 99, requestId: 2, committedRevision: data.committedRevision, type: 'skip', request: { destination: 'season', target: data.publication!.clock.winterEnd } });
        }
        if (data.id === 2 && !cancelAt) { cancelAt = performance.now(); worker.postMessage({ generation: 99, requestId: 3, committedRevision: data.committedRevision, type: 'cancel' }); }
        if (data.id === 3) {
          clearTimeout(timeout); worker.terminate();
          resolve({ elapsed: performance.now() - cancelAt, paused: data.publication!.clock.paused, movement,
            coherent: data.publication!.clock.revision >= replies[0].publication!.clock.revision,
            headless: replies.filter(r => r.id === 2).every(r => r.publication!.points.length === 0 && !r.movement) });
        }
      };
      worker.postMessage({ generation: 99, requestId: 1, committedRevision: 0, type: 'initialize', input: prepared });
    });
  }, { url: entries[0].url, input: { ...input, snow: { ...input.snow!, depthM: Array.from(input.snow!.depthM), surface: Array.from(input.snow!.surface) } } });
  expect(result.paused).toBe(true); expect(result.coherent).toBe(true); expect(result.headless).toBe(true);
  expect(result.movement).toBe(true);
  expect(result.elapsed).toBeLessThan(250);
  mkdirSync('test-results/dual-clock', { recursive: true });
  writeFileSync(`test-results/dual-clock/cancellation-${process.env.DUAL_CLOCK_GPU === '1' ? 'hardware' : 'software'}.json`, JSON.stringify({
    visualCancelMs, workerCancelMs: result.elapsed, coherent: result.coherent, headless: result.headless, movementRoundTrip: result.movement }, null, 2));
});

test('legacy games keep their original controls and schema-16 write path', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0);
  await expect(page.locator('.tb-speed')).toHaveCount(4);
  await expect(page.getByRole('button', { name: 'Date and time: simulation advances' })).toHaveCount(0);
  await page.locator('.game-menu-btn').click(); await page.locator('.hud-save').click();
  await expect.poll(() => page.evaluate(() => JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null')?.schemaVersion)).toBe(16);
  await page.locator('.game-menu-btn').click();
  await page.getByRole('menuitem', { name: 'Main Menu' }).click();
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.tb-speed')).toHaveCount(4);
});
