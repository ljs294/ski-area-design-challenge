import { expect, test } from '@playwright/test';

test('Jackson 2019 reruns deterministically and exposes comparisons, forecasts, and export', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Weather Model Lab' })).toBeVisible();
  await page.getByRole('button', { name: 'Run year' }).click();
  await expect(page.getByText('Completed', { exact: true })).toBeVisible({ timeout: 30_000 });
  const first = await page.locator('.summary article').first().locator('strong').textContent();
  await expect(page.getByRole('heading', { name: 'Monthly comparison' })).toBeVisible();
  await expect(page.getByText('Forecast issues').locator('..').locator('strong')).not.toHaveText('0');
  await expect(page.getByRole('button', { name: 'Export JSON' })).toBeEnabled();
  await page.getByRole('button', { name: 'Run year' }).click();
  await expect(page.getByText('Completed', { exact: true })).toBeVisible({ timeout: 30_000 });
  expect(await page.locator('.summary article').first().locator('strong').textContent()).toBe(first);
});
