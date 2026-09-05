import { expect, openMenu, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';
import { jumpTo, pointAt } from '../support/mapProbe';

for (const theme of ['light', 'dark'] as const) for (const size of [{ width: 1920, height: 1080 }, { width: 2560, height: 1440 }]) {
  test(`reference journey ${theme} ${size.width}`, async ({ page }, info) => {
    await page.setViewportSize(size);
    await openMenu(page);
    await page.evaluate((theme) => localStorage.setItem('skiapp:settings', JSON.stringify({ theme, reducedMotion: true })), theme);
    await page.reload();
    await expect(page.locator('html')).toHaveAttribute('data-theme', theme);
    await page.screenshot({ path: info.outputPath('home.png') });
    await page.getByRole('button', { name: 'New Resort' }).click();
    await expect(page.getByLabel('New resort setup')).toBeVisible();
    await page.screenshot({ path: info.outputPath('setup.png') });
    await seedPreparedResort(page);
    await page.evaluate((theme) => localStorage.setItem('skiapp:settings', JSON.stringify({ theme, reducedMotion: true })), theme);
    await page.reload();
    await page.getByRole('button', { name: /^Continue /  }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
    await jumpTo(page, [-121.495, 46.905], 16);
    await page.getByRole('button', { name: 'Ski lifts' }).click();
    await page.getByRole('button', { name: /Add ski lift/ }).click();
    await page.getByRole('button', { name: 'Draw lift', exact: true }).click();
    for (const point of [[-121.4962, 46.9044], [-121.4938, 46.9056]] as [number, number][]) {
      const pixel = await pointAt(page, point);
      await page.mouse.click(pixel.x, pixel.y);
    }
    await expect(page.getByText('Review lift', { exact: true })).toBeVisible();
    await page.getByRole('button', { name: 'Complete', exact: true }).click();
    await expect(page.getByRole('button', { name: 'Build lift', exact: true })).toBeEnabled();
    await page.screenshot({ path: info.outputPath('construction-review.png') });
    await page.getByRole('button', { name: 'Build lift', exact: true }).click();
    await expect(page.locator('.lift-detail')).toBeVisible();
    await expect(page.getByRole('button', { name: 'Build another', exact: true })).toBeEnabled();
    await page.screenshot({ path: info.outputPath('inspection.png') });
    await page.getByRole('button', { name: 'Analysis', exact: true }).click();
    await expect(page.getByRole('complementary', { name: 'Trail Map dashboard' })).toBeVisible();
    await page.getByRole('button', { name: 'Fit dashboard', exact: true }).click();
    await expect.poll(() => page.evaluate(() => {
      const map = (window as unknown as { appMap: {
        isMoving(): boolean;
        queryRenderedFeatures(options: { layers: string[] }): unknown[];
      } }).appMap;
      return !map.isMoving() && map.queryRenderedFeatures({ layers: ['dashboard-trail-edges'] }).length > 0;
    })).toBe(true);
    await page.screenshot({ path: info.outputPath('analysis.png') });
    await page.getByRole('button', { name: 'Expand workspace' }).click();
    await expect.poll(async () => (await page.locator('.workspace-panel').boundingBox())!.width).toBe(680);
  });
}

for (const scale of ['100', '125', '150']) {
  test(`compact viewport contains workspace actions at ${scale}%`, async ({ page }, info) => {
    await page.setViewportSize({ width: 1280, height: 720 });
    await seedPreparedResort(page);
    await page.evaluate((interfaceScale) => localStorage.setItem('skiapp:settings', JSON.stringify({ interfaceScale, theme: 'dark' })), scale);
    await page.reload();
    await page.getByRole('button', { name: /^Continue /  }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
    await page.getByRole('button', { name: 'Ski lifts' }).click();
    await page.getByRole('button', { name: /Add ski lift/ }).click();
    await page.getByRole('button', { name: 'Draw lift', exact: true }).click();
    const panel = await page.locator('.workspace-panel').boundingBox();
    expect(panel!.x).toBeGreaterThanOrEqual(0);
    expect(panel!.x + panel!.width).toBeLessThanOrEqual(1280);
    expect(panel!.y + panel!.height).toBeLessThan(720);
    await page.getByRole('button', { name: 'Close workspace' }).click();
    await page.getByRole('button', { name: 'Menu', exact: true }).click();
    await page.getByRole('menuitem', { name: 'Settings' }).click();
    await expect(page.getByRole('button', { name: `${scale}%`, exact: true })).toHaveAttribute('aria-pressed', 'true');
    await page.screenshot({ path: info.outputPath('settings.png') });
    await page.getByRole('button', { name: 'Close settings' }).click();
  });
}
