import { expect, test } from '@playwright/test';

test('live providers prepare and persist a New Game resort package', async ({ page }) => {
  test.setTimeout(360_000);
  test.skip(
    process.env.RUN_LIVE_PROVIDER_E2E !== '1',
    'Set RUN_LIVE_PROVIDER_E2E=1 to allow the opt-in provider/GPU smoke test.',
  );

  const providerKinds = new Set<string>();
  page.on('request', (request) => {
    const url = request.url();
    if (url.includes('elevation.nationalmap.gov')) providerKinds.add('USGS');
    if (url.includes('imagery.nationalmap.gov')) providerKinds.add('NAIP');
    if (url.includes('wmts.terrascope.be')) providerKinds.add('WorldCover');
  });
  await page.route('**/elevation-tiles-prod.s3.amazonaws.com/**', (route) => route.abort());
  await page.route('**/server.arcgisonline.com/**', (route) => route.abort());
  await page.route('**/tiles.openfreemap.org/**', (route) => route.abort());
  await page.addInitScript(() => localStorage.setItem('skiapp:settings', JSON.stringify({
    reducedMotion: true,
    renderQuality: 'standard',
  })));

  await page.goto('/?flat', { waitUntil: 'load' });
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
  await page.getByRole('button', { name: 'New Game' }).click();
  await expect(page.getByRole('button', { name: /Select site/ })).toBeVisible();
  await expect.poll(() => page.evaluate(() => Boolean(
    (globalThis as typeof globalThis & {
      appMap?: { getLayer(id: string): unknown };
    }).appMap?.getLayer('site-box-fill'),
  ))).toBe(true);

  await page.getByRole('button', { name: /Select site/ }).click();
  const canvas = page.locator('.maplibregl-canvas');
  const box = await canvas.boundingBox();
  expect(box).not.toBeNull();
  const center = { x: box!.x + box!.width / 2, y: box!.y + box!.height / 2 };
  await page.mouse.move(center.x - 55, center.y - 55);
  await page.mouse.down();
  await page.mouse.move(center.x + 55, center.y + 55, { steps: 12 });
  await page.mouse.up();
  await page.getByRole('button', { name: 'View this area' }).click();
  await page.locator('.name-entry-input').fill('Live Provider Acceptance');
  await page.getByRole('button', { name: 'Start Designing' }).click();

  await expect(page.locator('.hud-resort')).toBeVisible({ timeout: 300_000 });
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 60_000 });
  expect(providerKinds).toContain('USGS');
  expect(providerKinds).toContain('WorldCover');

  const persisted = await page.evaluate(async () => {
    const index = JSON.parse(localStorage.getItem('gamesave-index') ?? '[]') as Array<{
      key: string;
    }>;
    const save = JSON.parse(localStorage.getItem(`gamesave:${index[0]?.key}`) ?? 'null') as {
      schemaVersion?: number;
      terrainKey?: string;
    } | null;
    const terrain = await new Promise<Record<string, unknown> | null>((resolve, reject) => {
      const request = indexedDB.open('mountain-planner-terrain', 1);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const database = request.result;
        const transaction = database.transaction('terrains', 'readonly');
        const read = transaction.objectStore('terrains').get(save?.terrainKey);
        read.onerror = () => reject(read.error);
        read.onsuccess = () => { database.close(); resolve(read.result ?? null); };
      };
    });
    return {
      saveSchema: save?.schemaVersion,
      terrainKey: save?.terrainKey,
      recordKey: terrain?.key,
      terrainSchema: terrain?.schemaVersion,
      coverComplete: (terrain?.coverGrid as { complete?: boolean } | undefined)?.complete,
    };
  });
  expect(persisted).toMatchObject({
    saveSchema: 11,
    terrainKey: persisted.recordKey,
    terrainSchema: 6,
    coverComplete: true,
  });
});
