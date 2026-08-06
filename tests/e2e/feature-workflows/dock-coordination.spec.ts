import { expect, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';

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
