import { expect, test } from '../support/deterministicApp';
import { preparedTerrainFixture, seedPreparedResort } from '../support/preparedResort';
import { DualClockEngine } from '../../../src/dualClock/engine';
import { dualFixture, fixtureHour } from '../../../src/dualClock/fixtures';
import { generateBareSnowGrid } from '../../../src/snow';
import { buildSkiNetwork } from '../../../src/network';
import { weatherTerrainBinding } from '../../../src/weather/terrainBinding';
import { installWorkerProbe, workerEntries } from '../support/workerProbe';
import { mkdirSync, writeFileSync } from 'node:fs';

function shortLiftGuestFixture() {
  const base: [number, number] = [-121.495, 46.902];
  const top: [number, number] = [-121.495, 46.90218];
  const halfWidth = 0.00022;
  const lift = { id: 'e2e-short-lift', identifier: 'A', name: 'Short Summit', liftTypeId: 'detachable-six-pack' as const,
    points: [base, top] as [[number, number], [number, number]], endpointElevM: [1000, 1020] as [number, number],
    lengthM: 20, verticalM: 20, status: 'complete' as const, createdAt: '2026-01-01T00:00:00.000Z' };
  const trail = { id: 'e2e-short-trail', name: 'Short Return', brushWidthM: 30, areaM2: 600, lengthM: 20,
    verticalM: 20, avgSlopeDeg: 45, maxSlopeDeg: 45, difficulty: 'blue' as const, status: 'complete' as const,
    createdAt: '2026-01-01T00:00:00.000Z', parts: [{
      polygon: [[[top[0] - halfWidth, top[1]], [top[0] + halfWidth, top[1]], [base[0] + halfWidth, base[1]],
        [base[0] - halfWidth, base[1]], [top[0] - halfWidth, top[1]]]], centerline: [top, base], centerlineElevM: [1020, 1000],
    }] };
  const terrain = preparedTerrainFixture();
  const network = buildSkiNetwork([trail], [lift]);
  const liftEdge = network.edges.find((edge) => edge.kind === 'lift');
  if (!liftEdge || liftEdge.kind !== 'lift') throw new Error('Short e2e lift did not build into the production network.');
  const fixture = dualFixture(900);
  fixture.terrain = terrain; fixture.snow = generateBareSnowGrid(terrain);
  fixture.resort = { revision: 1, edges: network.edges, trails: [trail],
    portal: { id: 'e2e-portal', nodeId: liftEdge.from, lngLat: base, capacityPerMinute: 1000 },
    dailyDemand: 900, ticketPriceCents: 10000, amenities: [] };
  fixture.weather = Array.from({ length: 72 }, (_, index) => fixtureHour(new Date(Date.parse(fixture.at) + index * 3_600_000).toISOString()));
  return { checkpoint: new DualClockEngine(fixture).checkpoint(), terrain, lift, trail, weather: weatherFixture(terrain), base };
}

function weatherFixture(terrain: ReturnType<typeof preparedTerrainFixture>) {
  return { terrainKey: terrain.key, manifest: {
    schemaVersion: 1, terrainKey: terrain.key, terrainBinding: weatherTerrainBinding(terrain), timezone: 'UTC',
    historicalStartYear: 1991, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture', sourceVersion: 'fixture-v1',
    generatorVersion: 2, contentHash: 'dual-console-guest-fixture', complete: true, createdAt: '2026-01-01T00:00:00.000Z',
  }, historicalYears: [{ year: 1992, hours: Array.from({ length: 72 }, (_, index) => fixtureHour(new Date(Date.UTC(1992, 0, 1, index)).toISOString())) }] };
}

test('the diagnostic console opens and runs commands in the gameplay shell', async ({ page }) => {
  await seedPreparedResort(page);
  await page.goto('/?flat&dev-console', { waitUntil: 'load' });
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await expect(page.getByRole('button', { name: /Open developer console/ })).toBeVisible();
  await page.keyboard.press('F10');
  const console = page.getByRole('dialog', { name: 'Developer console' });
  await expect(console).toBeVisible();
  const command = console.getByRole('textbox', { name: 'Developer command' });
  await command.fill('time');
  await command.press('Enter');
  await expect(console).toContainText('2026');
  await command.fill('help');
  await command.press('Enter');
  await expect(console).toContainText('skip <duration>');
  await expect(console).toContainText('Jump forward without simulating elapsed world time');
});

test('a bare dual-clock resort accepts console snow and draws its real lift loop at 1x', async ({ page }) => {
  test.setTimeout(90_000);
  await installWorkerProbe(page);
  const fixture = shortLiftGuestFixture();
  await seedPreparedResort(page, { lifts: [fixture.lift], trails: [fixture.trail] });
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
  }, fixture);
  await page.goto('/?flat&dev-console', { waitUntil: 'load' });
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  const gpu = () => page.evaluate(() => {
    const empty = { count: 0, hitCount: 0, codes: [] as number[], hit: false, x: null, y: null, compactMode: false, pendingLength: 0 };
    try {
      const map = (window as unknown as { appMap?: { getLayer(id: string): { implementation?: Record<string, unknown> } } }).appMap;
      const layer = map?.getLayer('guest-simulation-dots')?.implementation as Record<string, unknown> | undefined;
      if (!layer) return empty;
      const codes = Array.from((layer.legacyStatusCodes as ArrayLike<number> | undefined) ?? []);
      const count = Number(layer.count ?? 0), hitCount = Number(layer.hitCount ?? 0);
      const x = hitCount > 0 ? (layer.hitXs as Float32Array)[0]! : null;
      const y = hitCount > 0 ? (layer.hitYs as Float32Array)[0]! : null;
      const hit = x !== null && y !== null ? (layer.hitTest as (point: { x: number; y: number }) => unknown)({
        x, y,
      }) : null;
      return { count, hitCount, codes, hit: Boolean(hit), x, y, compactMode: layer.compactMode,
        pendingLength: (layer.pending as Float32Array | undefined)?.length ?? 0 };
    } catch { return empty; }
  });
  await page.keyboard.press('F10');
  const console = page.getByRole('dialog', { name: 'Developer console' });
  const command = console.getByRole('textbox', { name: 'Developer command' });
  await command.fill('snow add 50cm'); await command.press('Enter');
  await expect(console).toContainText('Added 50 cm of fresh snow');
  await expect(console).toContainText('game time did not advance');

  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expect.poll(async () => (await gpu()).count, { timeout: 20_000 }).toBeGreaterThan(0);
  await expect.poll(async () => (await gpu()).codes.includes(4), { timeout: 20_000 }).toBe(true);
  await expect.poll(async () => (await gpu()).codes.includes(3), { timeout: 20_000 }).toBe(true);
  const riderProbe = await gpu();
  const riderCenter = riderProbe.x === null || riderProbe.y === null ? fixture.base : await page.evaluate(({ x, y }) => {
    const map = (window as unknown as { appMap: { unproject(point: [number, number]): { lng: number; lat: number }; jumpTo(options: unknown): void } }).appMap;
    const center = map.unproject([x!, y!]);
    return [center.lng, center.lat];
  }, riderProbe);
  await page.evaluate((center) => {
    (window as unknown as { appMap: { jumpTo(options: unknown): void } }).appMap.jumpTo({ center, zoom: 17 });
  }, riderCenter);
  await page.waitForTimeout(250);
  mkdirSync('test-results/dual-clock', { recursive: true });
  writeFileSync('test-results/dual-clock/console-snow-gpu-probe.json', JSON.stringify({ riderProbe, riderCenter, afterJump: await gpu() }, null, 2));
  await page.locator('.maplibregl-canvas').screenshot({ path: 'test-results/dual-clock/console-snow-lift-rider.png' });
  let observedWorkerFrames = await workerEntries(page, 'dualClock.worker');
  try {
    await expect.poll(async () => {
      observedWorkerFrames = await workerEntries(page, 'dualClock.worker');
      return observedWorkerFrames.some((entry) => entry.movementStatuses?.some((counts) => (counts.skiing ?? 0) > 0));
    }, { timeout: 20_000 }).toBe(true);
  } finally {
    writeFileSync('test-results/dual-clock/console-snow-worker-probe.json', JSON.stringify(observedWorkerFrames, null, 2));
  }
  await expect.poll(async () => (await gpu()).codes.includes(2), { timeout: 20_000 }).toBe(true);
  await expect.poll(async () => (await gpu()).hit, { timeout: 20_000 }).toBe(true);

  await page.screenshot({ path: 'test-results/dual-clock/console-snow-lift-loop.png' });
  await page.evaluate(() => {
    const map = (window as unknown as { appMap: { getStyle(): unknown; setStyle(style: unknown, options: unknown): void } }).appMap;
    map.setStyle(map.getStyle(), { diff: false });
  });
  await expect.poll(async () => (await gpu()).hit, { timeout: 15_000 }).toBe(true);
  await page.getByRole('button', { name: 'Dashboards', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Dashboards', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close Dashboards', exact: true }).click();
  await expect.poll(async () => (await gpu()).hit, { timeout: 15_000 }).toBe(true);

  await page.getByRole('button', { name: /^Menu/ }).click(); await page.locator('.hud-save').click();
  await page.reload(); await page.getByRole('button', { name: /^Continue / }).click();
  await expect.poll(async () => (await gpu()).count, { timeout: 15_000 }).toBeGreaterThan(0);
});

test('short-lift guests stay visible at 2× and 4×, then switch to aggregate flow at 8× across map lifecycle changes', async ({ page }) => {
  test.setTimeout(90_000);
  const fixture = shortLiftGuestFixture();
  await seedPreparedResort(page, { lifts: [fixture.lift], trails: [fixture.trail] });
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
  }, fixture);
  await page.goto('/?flat&dev-console', { waitUntil: 'load' });
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  const centerFixture = () => page.evaluate((center) => {
    (window as unknown as { appMap: { jumpTo(options: unknown): void } }).appMap.jumpTo({ center, zoom: 17 });
  }, fixture.base);
  const presentation = () => page.evaluate((base) => {
    const map = (window as unknown as { appMap: {
      getLayer(id: string): { implementation?: Record<string, unknown> } | undefined;
      getLayoutProperty(id: string, property: string): unknown;
      getSource(id: string): { serialize(): { data?: { features?: { properties?: Record<string, unknown> }[] } } } | undefined;
      getCenter(): { lng: number; lat: number };
      getZoom(): number;
      project(point: [number, number]): { x: number; y: number };
      isStyleLoaded(): boolean;
    } }).appMap;
    const guestLayer = map.getLayer('guest-simulation-dots');
    const guests = guestLayer?.implementation;
    const aggregate = map.getSource('guest-aggregate-flow')?.serialize().data?.features ?? [];
    return { count: Number(guests?.count ?? 0), hitCount: Number(guests?.hitCount ?? 0),
      guestVisibility: map.getLayoutProperty('guest-simulation-dots', 'visibility'),
      hasGuestLayer: Boolean(guestLayer), hasGuestImplementation: Boolean(guests),
      compactMode: Boolean(guests?.compactMode), pendingLength: Number((guests?.pending as Float32Array | undefined)?.length ?? 0),
      hasMap: Boolean(guests?.map), hasProgram: Boolean(guests?.program), hasBuffer: Boolean(guests?.buffer),
      center: map.getCenter(), zoom: map.getZoom(), projectedBase: map.project(base), styleLoaded: map.isStyleLoaded(),
      aggregateLabels: aggregate.map(feature => String(feature.properties?.label ?? '')) };
  }, fixture.base);
  await centerFixture();
  await page.getByRole('button', { name: '2× simulation speed', exact: true }).click();
  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expect.poll(async () => (await presentation()).hitCount, { timeout: 20_000 }).toBeGreaterThan(0);
  await expect(page.getByRole('button', { name: '2× simulation speed', exact: true })).toHaveAttribute('aria-pressed', 'true');

  await page.getByRole('button', { name: '4× simulation speed', exact: true }).click();
  await expect.poll(async () => (await presentation()).hitCount, { timeout: 15_000 }).toBeGreaterThan(0);
  await expect(page.getByRole('button', { name: '4× simulation speed', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await page.getByRole('button', { name: 'Pause game clock', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible();

  await page.evaluate(() => {
    const map = (window as unknown as { appMap: { getStyle(): unknown; setStyle(style: unknown, options: unknown): void } }).appMap;
    map.setStyle(map.getStyle(), { diff: false });
  });
  await expect.poll(async () => (await presentation()).styleLoaded, { timeout: 15_000 }).toBe(true);
  let styleReloadPresentation: Awaited<ReturnType<typeof presentation>> | undefined;
  try {
    await expect.poll(async () => (await presentation()).hitCount, { timeout: 15_000 }).toBeGreaterThan(0);
  } finally {
    styleReloadPresentation = await presentation();
    mkdirSync('test-results/dual-clock', { recursive: true });
    writeFileSync('test-results/dual-clock/renderer-speeds-style-reload.json', JSON.stringify(styleReloadPresentation, null, 2));
  }
  await page.getByRole('button', { name: 'Dashboards', exact: true }).click();
  await expect(page.getByRole('region', { name: 'Dashboards', exact: true })).toBeVisible();
  await page.getByRole('button', { name: 'Close Dashboards', exact: true }).click();
  await expect.poll(async () => (await presentation()).hitCount, { timeout: 15_000 }).toBeGreaterThan(0);

  await page.getByRole('button', { name: /^Menu/ }).click(); await page.locator('.hud-save').click();
  await page.reload(); await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await centerFixture();
  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expect.poll(async () => (await presentation()).hitCount, { timeout: 20_000 }).toBeGreaterThan(0);

  await page.getByRole('button', { name: '8× simulation speed', exact: true }).click();
  await expect.poll(async () => (await presentation()).aggregateLabels.some(label =>
    label.includes('Short Summit:') && label.includes('waiting') && label.includes('riding')), { timeout: 15_000 }).toBe(true);
  await expect.poll(async () => (await presentation()).count, { timeout: 15_000 }).toBe(0);
  mkdirSync('test-results/dual-clock', { recursive: true });
  await page.locator('.maplibregl-canvas').screenshot({ path: 'test-results/dual-clock/renderer-speeds-aggregate.png' });
});
