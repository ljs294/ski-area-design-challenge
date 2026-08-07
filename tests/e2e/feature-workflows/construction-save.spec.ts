import { expect, test } from '../support/deterministicApp';
import { jumpTo, pointAt, setCaptureTransients, sourceFeatureCount } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';
import { installWorkerProbe, workerEntries } from '../support/workerProbe';

const CENTER: [number, number] = [-121.495, 46.905];
const BASE: [number, number] = [-121.4962, 46.9044];
const TOP: [number, number] = [-121.4938, 46.9056];

const seededTrail = {
  id: 'trail-save-coherence',
  name: 'Atomic Traverse',
  parts: [{
    polygon: [[
      [-121.496, 46.904], [-121.494, 46.904],
      [-121.494, 46.906], [-121.496, 46.906], [-121.496, 46.904],
    ]],
    centerline: [[-121.495, 46.906], [-121.495, 46.904]],
    centerlineElevM: [1030, 1000],
  }],
  brushWidthM: 40,
  areaM2: 20_000,
  lengthM: 222,
  verticalM: 30,
  avgSlopeDeg: 8,
  maxSlopeDeg: 12,
  difficulty: 'blue',
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

test('double confirmation builds once and Save persists one coherent document', async ({ page }) => {
  await seedPreparedResort(page, { trails: [seededTrail] });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, CENTER, 16);

  await page.getByRole('button', { name: 'Ski lifts' }).click();
  await page.getByRole('button', { name: /Add ski lift/ }).click();
  const base = await pointAt(page, BASE);
  const top = await pointAt(page, TOP);
  await page.mouse.click(base.x, base.y);
  await expect.poll(() => sourceFeatureCount(page, 'lifts')).toBe(3);
  await setCaptureTransients(page, true);
  await expect.poll(() => sourceFeatureCount(page, 'lifts')).toBe(0);
  await setCaptureTransients(page, false);
  await expect.poll(() => sourceFeatureCount(page, 'lifts')).toBe(3);
  await page.mouse.click(top.x, top.y);
  await expect(page.getByText('New fixed-grip chairlift', { exact: true })).toBeVisible();
  await page.locator('.lift-name-input').fill('Atomic Double');
  await page.getByRole('button', { name: 'Complete', exact: true }).click();

  const build = page.getByRole('button', { name: 'Build lift', exact: true });
  await expect(build).toBeEnabled();
  // Two confirmations in one browser task both see the review state. Only the
  // synchronous document lock can reject the second before React publishes.
  await build.evaluate((button) => {
    button.click();
    button.click();
  });

  await expect(page.getByText('Ski Lifts (1)', { exact: true })).toBeVisible({ timeout: 15_000 });
  await expect.poll(() => sourceFeatureCount(page, 'lifts')).toBe(3);
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { appSaveState: { unsaved: boolean } }).appSaveState.unsaved,
  )).toBe(true);

  await page.getByRole('button', { name: 'Ski runs' }).click();
  await page.locator('[data-row-id="trail-save-coherence"]').click();
  await page.getByRole('button', { name: 'Edit', exact: true }).click();
  await expect(page.getByText('Edit run', { exact: true })).toBeVisible();
  await page.getByRole('button', { name: /^Menu/ }).click();
  // Patch topology and invoke Save in the same browser task. A render-timed
  // ref would still contain the old run; the committed topology projection is
  // synchronous and therefore must persist the new name.
  await page.evaluate(() => {
    const input = document.querySelector<HTMLInputElement>('.trail-panel .lift-name-input');
    const save = document.querySelector<HTMLButtonElement>('.hud-save');
    if (!input || !save) throw new Error('The run editor or Save action is unavailable.');
    const setter = Object.getOwnPropertyDescriptor(HTMLInputElement.prototype, 'value')?.set;
    setter?.call(input, 'Renamed in save tick');
    input.dispatchEvent(new Event('input', { bubbles: true }));
    save.click();
  });
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { appSaveState: { unsaved: boolean } }).appSaveState.unsaved,
  )).toBe(false);

  const persisted = await page.evaluate(async () => {
    const save = JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null');
    const terrain = await new Promise<Record<string, unknown>>((resolve, reject) => {
      const request = indexedDB.open('mountain-planner-terrain', 1);
      request.onerror = () => reject(request.error);
      request.onsuccess = () => {
        const database = request.result;
        const transaction = database.transaction('terrains', 'readonly');
        const read = transaction.objectStore('terrains').get(save.terrainKey);
        read.onerror = () => reject(read.error);
        read.onsuccess = () => {
          database.close();
          resolve(read.result);
        };
      };
    });
    return {
      save,
      terrainKey: terrain.key,
      terrainCoverChecksum:
        (terrain.coverMetadata as { checksum?: string } | undefined)?.checksum ?? null,
      live: (window as unknown as { appSaveState: Record<string, unknown> }).appSaveState,
    };
  });

  expect(persisted.save).toMatchObject({
    schemaVersion: 11,
    terrainKey: 'e2e-terrain',
    lifts: [{ name: 'Atomic Double', status: 'complete' }],
    trails: [{ id: 'trail-save-coherence', name: 'Renamed in save tick' }],
  });
  expect(persisted.save.lifts).toHaveLength(1);
  expect(persisted.terrainKey).toBe(persisted.save.terrainKey);
  expect(persisted.terrainCoverChecksum).toBe(persisted.live.coverChecksum);
  expect(persisted.live).toMatchObject({
    terrainKey: persisted.save.terrainKey,
    terrainDirty: { elevation: false, cover: false },
    unsaved: false,
  });
});

test('road confirmation builds once and survives best-effort cover failure', async ({ page }) => {
  await installWorkerProbe(page, { failPostFor: 'coverEdit.worker' });
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, CENTER, 16);

  await page.getByRole('button', { name: 'Infrastructure' }).click();
  await page.getByRole('button', { name: /Build road/ }).click();
  const first = await pointAt(page, BASE);
  const second = await pointAt(page, TOP);
  await page.mouse.click(first.x, first.y);
  await page.mouse.click(second.x, second.y);
  await page.getByRole('button', { name: 'Finish route' }).click();

  await expect(page.getByText('Review road', { exact: true })).toBeVisible();
  await page.locator('.infrastructure-panel .lift-name-input').fill('Atomic Road');
  const build = page.getByRole('button', { name: 'Build road', exact: true });
  await expect(build).toBeEnabled({ timeout: 15_000 });
  await build.evaluate((button) => {
    button.click();
    button.click();
  });

  await expect(page.getByText(/Infrastructure.*1 roads/)).toBeVisible({ timeout: 15_000 });
  await expect.poll(() => sourceFeatureCount(page, 'player-roads')).toBeGreaterThan(0);
  await expect.poll(async () => (await workerEntries(page, 'coverEdit.worker')).length).toBe(1);

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { appSaveState: { unsaved: boolean } }).appSaveState.unsaved,
  )).toBe(false);

  const persisted = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(persisted).toMatchObject({
    schemaVersion: 11,
    terrainKey: 'e2e-terrain',
    roads: [{ name: 'Atomic Road', roadType: 'two-lane', terrainGraded: true }],
  });
  expect(persisted.roads).toHaveLength(1);
});
