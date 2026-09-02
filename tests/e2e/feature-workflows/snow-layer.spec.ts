import { expect, test } from '../support/deterministicApp';
import { pointAt, visibilityOf } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const CENTER: [number, number] = [-121.495, 46.905];

async function openSnow(page: Parameters<typeof seedPreparedResort>[0]) {
  const snowToggle = page.getByRole('checkbox', { name: 'Snow', exact: true });
  if (!await snowToggle.isVisible()) await page.getByRole('button', { name: 'Layers' }).click();
  await snowToggle.check();
  await expect(page.getByLabel('Snow layer controls')).toBeVisible();
}

test('snow overlay switches modes, survives restyle, and reloads its schema-16 snapshot', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await page.getByRole('button', { name: 'Layers' }).click();
  const layerMenu = page.locator('.dock-layers .dock-panel');
  const layerMenuHeight = await layerMenu.evaluate((element) => element.clientHeight);
  await page.getByRole('checkbox', { name: 'Snow', exact: true }).check();
  await expect.poll(() => layerMenu.evaluate((element) => element.clientHeight)).toBe(layerMenuHeight);
  await page.getByRole('button', { name: 'Close Snow layer' }).click();
  await expect(page.getByLabel('Snow layer controls')).toHaveCount(0);
  await expect.poll(() => visibilityOf(page, 'snow')).toBe('none');

  await page.getByRole('checkbox', { name: 'Snow', exact: true }).check();
  await page.keyboard.press('Escape');
  await expect(page.getByLabel('Snow layer controls')).toHaveCount(0);
  await expect.poll(() => visibilityOf(page, 'snow')).toBe('none');

  await openSnow(page);
  await expect.poll(() => visibilityOf(page, 'snow')).toBe('visible');

  const center = await pointAt(page, CENTER);
  await page.mouse.move(center.x, center.y);
  await expect(page.getByLabel('Snow layer controls')).toContainText(/\d+ in/);
  await expect(page.getByLabel('Snow layer controls')).toContainText('P · Powder');
  await page.locator('.dock-layers').getByRole('button', { name: 'Close' }).click();
  await expect(page.getByLabel('Snow layer controls')).toBeVisible();

  await page.getByRole('button', { name: 'Conditions', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Conditions', exact: true })).toHaveAttribute('aria-pressed', 'true');
  await expect(page.getByLabel('Snow layer controls')).toContainText('WP · Wet Powder');
  await expect.poll(() => page.evaluate(() => {
    const source = (window as unknown as { appMap: { getSource(id: string): {
      serialize(): { tiles?: string[] } } } }).appMap.getSource('snow');
    return source.serialize().tiles?.[0] ?? '';
  })).toContain('mode=conditions');

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.getByRole('menuitem', { name: 'Settings' }).click();
  await page.getByRole('button', { name: 'Meters' }).click();
  await page.getByRole('button', { name: 'Close settings' }).click();
  await expect.poll(() => visibilityOf(page, 'snow')).toBe('visible');
  await expect(page.getByRole('button', { name: 'Conditions', exact: true })).toHaveAttribute('aria-pressed', 'true');

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  await expect.poll(() => page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null')?.schemaVersion)).toBe(16);
  await expect.poll(() => page.evaluate(() =>
    typeof JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null')?.snow?.cells)).toBe('string');

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.getByRole('menuitem', { name: 'Main Menu' }).click();
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();

  // Replace the stored snapshot with a distinctive valid 2x2 grid: 321 cm,
  // Wet Powder. If load regenerated the baseline, the cursor would show Powder.
  await page.evaluate(() => {
    const key = 'gamesave:e2e-save';
    const save = JSON.parse(localStorage.getItem(key) ?? 'null');
    const cell = String.fromCharCode(0x41, 0xb1);
    save.snow = { version: 1, bounds: { west: -121.5, south: 46.9,
      east: -121.49, north: 46.91 }, width: 2, height: 2, cells: btoa(cell.repeat(4)) };
    localStorage.setItem(key, JSON.stringify(save));
  });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await openSnow(page);
  const reloadedCenter = await pointAt(page, CENTER);
  await page.mouse.move(reloadedCenter.x, reloadedCenter.y);
  await expect(page.getByLabel('Snow layer controls')).toContainText('321 cm');
  await expect(page.getByLabel('Snow layer controls')).toContainText('WP · Wet Powder');
});
