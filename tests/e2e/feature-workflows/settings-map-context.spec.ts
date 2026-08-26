import type { Page } from '@playwright/test';
import { expect, test } from '../support/deterministicApp';
import { sourceFeatureCount } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const repairedElements = [
  {
    type: 'way', id: 901, tags: { highway: 'service', name: 'Recovered Access Road' },
    geometry: [{ lon: -121.499, lat: 46.901 }, { lon: -121.491, lat: 46.909 }],
  },
  {
    type: 'way', id: 902, tags: { waterway: 'stream', name: 'Recovered Creek' },
    geometry: [{ lon: -121.498, lat: 46.909 }, { lon: -121.492, lat: 46.901 }],
  },
];

async function persistedState(page: Page) {
  return page.evaluate(async () => {
    const save = localStorage.getItem('gamesave:e2e-save');
    const terrain = await new Promise<Record<string, unknown> | null>((resolve, reject) => {
      const request = indexedDB.open('mountain-planner-terrain');
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const database = request.result;
        const transaction = database.transaction(
          ['terrain-metadata', 'terrain-assets', 'terrains'], 'readonly');
        const metadata = transaction.objectStore('terrain-metadata').get('e2e-terrain');
        const assets = transaction.objectStore('terrain-assets').get('e2e-terrain');
        const legacy = transaction.objectStore('terrains').get('e2e-terrain');
        transaction.onerror = () => reject(transaction.error);
        transaction.oncomplete = () => {
          database.close();
          const result = metadata.result && assets.result
            ? { ...metadata.result, ...assets.result } as Record<string, unknown>
            : (legacy.result as Record<string, unknown> | undefined) ?? null;
          if (!result) return resolve(null);
          const coverGrid = result.coverGrid as { data?: ArrayLike<number> } | undefined;
          resolve({
            ...result,
            sampleHeights: Array.from(result.sampleHeights as ArrayLike<number>),
            contourSegments: result.contourSegments
              ? Array.from(result.contourSegments as ArrayLike<number>) : undefined,
            coverGrid: coverGrid ? { ...coverGrid,
              data: Array.from(coverGrid.data ?? []) } : undefined,
          });
        };
      };
    });
    return { save, terrain };
  });
}

test('Settings repairs missing map context after provider exhaustion without changing resort data', async ({ page }) => {
  let overpassRequests = 0;
  let releaseFirstFailure = () => {};
  const firstFailureGate = new Promise<void>((resolve) => { releaseFirstFailure = resolve; });
  await page.route('**/api/interpreter', async (route) => {
    overpassRequests += 1;
    if (overpassRequests <= 3) {
      if (overpassRequests === 1) await firstFailureGate;
      await route.fulfill({ status: 503, contentType: 'text/plain', body: 'Unavailable' });
      return;
    }
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ elements: repairedElements }),
    });
  });

  await seedPreparedResort(page, {}, { mapContext: 'missing' });
  const before = await persistedState(page);
  expect(before.terrain?.vectorFeatures).toBeUndefined();

  // Resort Data is contextual and therefore absent from main-menu Settings.
  await page.getByRole('button', { name: 'Settings' }).click();
  await expect(page.getByRole('tab', { name: 'General' })).toHaveAttribute('aria-selected', 'true');
  await expect(page.getByRole('tab', { name: 'Resort Data' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Close settings' }).click();

  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await expect.poll(() => sourceFeatureCount(page, 'local-context')).toBe(0);

  await page.getByRole('button', { name: 'Menu' }).click();
  await expect(page.getByRole('menuitem', { name: 'Download Map Context' })).toHaveCount(0);
  await page.getByRole('menuitem', { name: 'Settings' }).click();

  // Tabs use roving focus and leaving Controls cancels a pending key capture.
  const generalTab = page.getByRole('tab', { name: 'General' });
  await generalTab.focus();
  await generalTab.press('ArrowRight');
  await expect(page.getByRole('tab', { name: 'Controls' })).toHaveAttribute('aria-selected', 'true');
  await page.locator('.keybind-btn').first().click();
  await expect(page.getByText('Press a key…')).toBeVisible();
  await page.getByRole('tab', { name: 'Resort Data' }).click();
  await page.getByRole('tab', { name: 'Controls' }).click();
  await expect(page.getByText('Press a key…')).toHaveCount(0);
  await page.getByRole('tab', { name: 'Resort Data' }).click();

  await page.getByRole('button', { name: 'Download Map Context' }).click();
  await expect.poll(() => overpassRequests).toBe(1);
  await expect(page.getByRole('button', { name: 'Downloading Map Context…' })).toBeDisabled();
  releaseFirstFailure();
  await expect(page.getByRole('alert')).toContainText('overpass-api.de');
  await expect(page.getByRole('alert')).toContainText('maps.mail.ru');
  await expect(page.getByRole('alert')).toContainText('overpass.private.coffee');
  expect(overpassRequests).toBe(3);

  await page.getByRole('button', { name: 'Retry Map Context' }).click();
  await expect(page.getByText('Map context available')).toBeVisible();
  await expect.poll(() => sourceFeatureCount(page, 'local-context')).toBe(2);
  expect(overpassRequests).toBe(4);

  const after = await persistedState(page);
  const vectors = after.terrain?.vectorFeatures as {
    roads?: Array<{ name?: string }>;
    waterLines?: Array<{ name?: string }>;
  } | undefined;
  expect(vectors?.roads?.[0]?.name).toBe('Recovered Access Road');
  expect(vectors?.waterLines?.[0]?.name).toBe('Recovered Creek');
  expect(after.terrain).toMatchObject({
    schemaVersion: before.terrain?.schemaVersion,
    key: before.terrain?.key,
    mountainName: before.terrain?.mountainName,
    sampleHeights: before.terrain?.sampleHeights,
    coverGrid: before.terrain?.coverGrid,
    contourSegments: before.terrain?.contourSegments,
    packageManifest: before.terrain?.packageManifest,
  });
  expect(after.save).toBe(before.save);
});

test('closing Settings cancels context acquisition before persistence', async ({ page }) => {
  let overpassRequests = 0;
  let releaseResponse = () => {};
  const responseGate = new Promise<void>((resolve) => { releaseResponse = resolve; });
  await page.route('**/api/interpreter', async (route) => {
    overpassRequests += 1;
    await responseGate;
    await route.fulfill({
      status: 200,
      contentType: 'application/json',
      body: JSON.stringify({ elements: repairedElements }),
    }).catch(() => {});
  });

  await seedPreparedResort(page, {}, { mapContext: 'missing' });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await page.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: 'Settings' }).click();
  await page.getByRole('tab', { name: 'Resort Data' }).click();
  await page.getByRole('button', { name: 'Download Map Context' }).click();
  await expect.poll(() => overpassRequests).toBe(1);
  await expect(page.getByRole('button', { name: 'Downloading Map Context…' })).toBeDisabled();

  await page.getByRole('button', { name: 'Close settings' }).click();
  releaseResponse();
  await page.waitForTimeout(150);
  expect(overpassRequests).toBe(1);
  await expect.poll(() => sourceFeatureCount(page, 'local-context')).toBe(0);
  expect((await persistedState(page)).terrain?.vectorFeatures).toBeUndefined();

  await page.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: 'Settings' }).click();
  await page.getByRole('tab', { name: 'Resort Data' }).click();
  await expect(page.getByRole('button', { name: 'Download Map Context' })).toBeEnabled();
});
