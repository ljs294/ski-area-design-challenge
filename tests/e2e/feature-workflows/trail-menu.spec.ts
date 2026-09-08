import { expect, openMenu, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';

for (const theme of ['light', 'dark']) {
  for (const [width, height] of [[800, 600], [1024, 768], [1920, 1080]]) {
    test(`trail signs ${theme} ${width}x${height}`, async ({ page }, info) => {
      await page.setViewportSize({ width, height });
      await openMenu(page);
      await page.evaluate((theme) => localStorage.setItem('skiapp:settings', JSON.stringify({ theme, reducedMotion: true })), theme);
      await page.reload();
      await expect(page.getByRole('heading', { name: 'Ski Area Design Challenge', exact: true })).toBeVisible();
      await expect(page.getByRole('button', { name: /^Continue / })).toHaveCount(0);
      for (const name of ['New Resort', 'My Resorts', 'Settings', 'Credits']) {
        const button = page.getByRole('button', { name, exact: true });
        await button.focus();
        await expect(button).toBeInViewport();
      }
      await page.getByRole('button', { name: 'New Resort', exact: true }).focus();
      await page.screenshot({ path: info.outputPath('menu.png') });
      await page.getByRole('button', { name: 'Settings', exact: true }).click();
      await expect(page.getByRole('dialog')).toBeVisible();
      await page.keyboard.press('Escape');
      await page.getByRole('button', { name: 'My Resorts', exact: true }).click();
      await expect(page.getByRole('dialog', { name: 'My Resorts' })).toBeVisible();
    });
  }
}

test('trail menu continues a saved mountain without a thumbnail', async ({ page }) => {
  await seedPreparedResort(page);
  await expect(page.locator('.trail-sign-continue')).toBeVisible();
  await expect(page.locator('.trail-sign-continue img')).toHaveCount(0);
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
  await expect(page.locator('.game-toolbar')).toBeVisible();
});

test('large signs and long mountain names remain reachable', async ({ page }, info) => {
  await page.setViewportSize({ width: 800, height: 600 });
  await seedPreparedResort(page);
  await page.evaluate(() => {
    const save = JSON.parse(localStorage.getItem('gamesave:e2e-save')!);
    save.name = 'Crystal Mountain Upper Summit and Northern Ridge Winter Resort';
    localStorage.setItem('gamesave:e2e-save', JSON.stringify(save));
    localStorage.setItem('skiapp:settings', JSON.stringify({ interfaceScale: 150, renderQuality: 'performance', reducedMotion: true }));
  });
  await page.reload();
  for (const name of [/^Continue /, /^New Resort$/, /^My Resorts$/, /^Settings$/, /^Credits$/]) {
    const button = page.getByRole('button', { name });
    await button.focus();
    await expect(button).toBeInViewport();
    const box = await button.boundingBox();
    expect(box!.x).toBeGreaterThanOrEqual(0);
    expect(box!.x + box!.width).toBeLessThanOrEqual(800);
  }
  await page.getByRole('button', { name: /^Continue / }).focus();
  await page.screenshot({ path: info.outputPath('large-signs.png') });
});
