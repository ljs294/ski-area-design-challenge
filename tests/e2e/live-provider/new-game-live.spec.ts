import { expect, test } from '@playwright/test';

test('live provider picker initializes', async ({ page }) => {
  test.skip(
    process.env.RUN_LIVE_PROVIDER_E2E !== '1',
    'Set RUN_LIVE_PROVIDER_E2E=1 to allow the opt-in provider/GPU smoke test.',
  );

  await page.goto('/?flat', { waitUntil: 'load' });
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
  await page.getByRole('button', { name: 'New Game' }).click();
  await expect(page.getByRole('button', { name: /Select site/ })).toBeVisible();
  await expect.poll(() => page.evaluate(() => Boolean(
    (globalThis as typeof globalThis & { appMap?: { isStyleLoaded(): boolean } }).appMap?.isStyleLoaded(),
  ))).toBe(true);
});
