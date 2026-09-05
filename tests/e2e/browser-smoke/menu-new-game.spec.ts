import { expect, openMenu, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';

test('main menu enters the New Game site-picker shell', async ({ page }) => {
  await openMenu(page);

  await expect(page.getByRole('button', { name: /^Continue /  })).toHaveCount(0);
  await page.getByRole('button', { name: 'New Resort' }).click();

  await expect(page.locator('.main-menu')).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Menu', exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: /Select site/ })).toBeVisible();
  await expect(page.locator('.game-dock')).toHaveCount(0);

  await page.getByRole('button', { name: 'Menu', exact: true }).click();
  await expect(page.getByRole('menuitem', { name: 'My Resorts' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Settings' })).toBeVisible();
  await expect(page.getByRole('menuitem', { name: 'Save' })).toHaveCount(0);
});

test('Performance keeps the full-size terrain menu backdrop', async ({ page }) => {
  await openMenu(page);
  await page.evaluate(() => localStorage.setItem('skiapp:settings', JSON.stringify({
    renderQuality: 'performance',
  })));
  await page.reload({ waitUntil: 'load' });

  const backdrop = page.locator('.menu-backdrop-map');
  await expect(backdrop).toBeVisible();
  const box = await backdrop.boundingBox();
  expect(box?.width).toBe(1280);
  expect(box?.height).toBe(720);
});

test('a prepared resort reveals a usable, mutually exclusive construction dock', async ({ page }) => {
  await seedPreparedResort(page);

  const continueButton = page.getByRole('button', { name: /^Continue /  });
  await expect(continueButton).toBeEnabled();
  await continueButton.click();

  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await expect(page.locator('.workspace-shell')).toBeVisible();
  await expect(page.getByText('Deterministic Peak', { exact: true })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Layers' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ski lifts' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Ski runs' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Snowmaking' })).toBeVisible();
  await expect(page.getByRole('button', { name: 'Infrastructure' })).toBeVisible();

  await page.getByRole('button', { name: 'Layers' }).click();
  await expect(page.locator('.dock-layers')).toBeVisible();
  await expect(page.getByRole('button', { name: 'Layers' })).toHaveAttribute('aria-pressed', 'true');

  await page.getByRole('button', { name: 'Ski lifts' }).click();
  await expect(page.locator('.dock-layers')).toHaveCount(0);
  await expect(page.locator('.dock-lifts')).toBeVisible();
  await expect(page.getByText('Ski Lifts (0)', { exact: true })).toBeVisible();
});

test('summer uses one confirmed September skip and weather panels have an opaque surface', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await page.locator('.tb-play').click();
  const confirmation = page.getByRole('dialog', { name: 'Finish summer planning?' });
  await expect(confirmation).toBeVisible();
  await expect(confirmation).toContainText('September 1');
  await confirmation.getByRole('button', { name: 'Keep planning' }).click();
  await expect(confirmation).toHaveCount(0);

  await page.locator('.tb-weather').click();
  const weather = page.getByLabel('Weather analysis overlay');
  await expect(weather).toBeVisible();
  expect(await weather.evaluate((element) => getComputedStyle(element).backgroundColor))
    .not.toBe('rgba(0, 0, 0, 0)');
  expect(await weather.evaluate((element) => getComputedStyle(element).overflow)).toBe('hidden');
  expect((await weather.boundingBox())?.width).toBeGreaterThan(300);
});
