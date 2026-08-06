import { expect, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';
import { setCaptureTransients, sourceFeatureCount } from '../support/mapProbe';

test('Layers can remain beside an active trail tool and switching tools cancels it', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await page.getByRole('button', { name: 'Ski runs' }).click();
  await page.getByRole('button', { name: /Create Trail/ }).click();
  await expect(page.locator('.dock-trails')).toBeVisible();
  await expect(page.getByText('Place Trailhead', { exact: true })).toBeVisible();

  await page.getByRole('button', { name: 'Layers' }).click();
  await expect(page.locator('.dock-trails')).toBeVisible();
  await expect(page.locator('.dock-layers')).toBeVisible();

  await page.getByRole('button', { name: 'Infrastructure' }).click();
  await expect(page.locator('.dock-trails')).toHaveCount(0);
  await expect(page.locator('.dock-layers')).toHaveCount(0);
  await expect(page.locator('.dock-infrastructure')).toBeVisible();
});

test('capture hides and exactly restores an active family transient', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await page.getByRole('button', { name: 'Infrastructure' }).click();
  await page.getByRole('button', { name: 'Build road' }).click();
  const canvas = page.locator('.maplibregl-canvas');
  const box = await canvas.boundingBox();
  expect(box).not.toBeNull();
  await page.mouse.click(box!.x + box!.width / 2, box!.y + box!.height / 2);
  await expect.poll(() => sourceFeatureCount(page, 'road-draft')).toBeGreaterThan(0);

  await setCaptureTransients(page, true);
  await expect.poll(() => sourceFeatureCount(page, 'road-draft')).toBe(0);
  await setCaptureTransients(page, false);
  await expect.poll(() => sourceFeatureCount(page, 'road-draft')).toBeGreaterThan(0);

  await page.getByRole('button', { name: 'Cancel' }).click();
});
