import { expect, openMenu, test } from '../support/deterministicApp';
import { seedPreparedResort } from '../support/preparedResort';

test('home, library and setup use one consistent player journey', async ({ page }, info) => {
  await page.setViewportSize({ width: 1920, height: 1080 });
  await openMenu(page);
  await expect(page.getByRole('heading', { name: /Your mountain.*Your design/  })).toBeVisible();
  await page.screenshot({ path: info.outputPath('home-light.png') });
  await page.getByRole('button', { name: 'My Resorts' }).click();
  await expect(page.getByRole('dialog', { name: 'My Resorts' })).toBeVisible();
  await expect(page.getByText('No resorts yet.', { exact: false })).toBeVisible();
  await page.keyboard.press('Escape');
  await page.getByRole('button', { name: 'New Resort' }).click();
  await expect(page.getByLabel('New resort setup')).toBeVisible();
  await page.getByRole('button', { name: /Select site/ }).click();
  await expect(page.locator('.setup-steps [aria-current="step"]')).toHaveText('2Define boundary');
  await page.screenshot({ path: info.outputPath('setup-light.png') });
});

test('workspace keeps tools, analysis and weather in one panel in both themes', async ({ page }, info) => {
  await page.setViewportSize({ width: 1920, height: 1080 });
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
  await page.getByRole('button', { name: 'Ski lifts' }).click();
  await expect(page.getByLabel('Lifts workspace')).toBeVisible();
  const panel = await page.locator('.workspace-panel').boundingBox();
  expect(panel!.x + panel!.width).toBeLessThan(1920 * .3);
  await page.screenshot({ path: info.outputPath('workspace-light.png') });
  await page.getByRole('button', { name: 'Analysis', exact: true }).click();
  await expect(page.getByRole('complementary', { name: 'Trail Map dashboard' })).toBeVisible();
  await expect(page.locator('.workspace-panel')).toHaveCount(1);
  await page.screenshot({ path: info.outputPath('analysis-light.png') });
  await page.locator('.tb-weather').click();
  await expect(page.getByLabel('Weather workspace')).toBeVisible();
  await expect(page.getByRole('complementary', { name: 'Trail Map dashboard' })).toHaveCount(0);
  await page.getByRole('button', { name: 'Menu', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Settings' }).click();
  await page.getByRole('button', { name: 'Dark', exact: true }).click();
  await page.getByRole('button', { name: 'Close settings' }).click();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.screenshot({ path: info.outputPath('weather-dark.png') });
  await page.getByRole('button', { name: 'Ski lifts' }).click();
  await page.getByRole('button', { name: /Add ski lift/ }).click();
  await expect(page.getByRole('tree', { name: 'Lift type' })).toBeVisible();
  await page.screenshot({ path: info.outputPath('construction-dark.png') });
  await page.getByRole('button', { name: 'Infrastructure', exact: true }).click();
  await expect(page.getByRole('tree', { name: 'Lift type' })).toHaveCount(0);
  await expect(page.getByLabel('Infrastructure workspace')).toBeVisible();
});


test('live theme changes preserve map camera, layer order and an active road draft', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15000 });
  await page.getByRole('button', { name: 'Infrastructure', exact: true }).click();
  await page.getByRole('button', { name: /Build road/ }).click();
  await page.mouse.click(1050, 500);
  await expect(page.getByRole('button', { name: 'Undo point' })).toBeEnabled();
  const snapshot = () => page.evaluate(() => {
    const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    return { camera: [map.getCenter().lng, map.getCenter().lat, map.getZoom(), map.getBearing(), map.getPitch()],
      layers: map.getStyle().layers.map((layer) => [layer.id, layer.layout?.visibility ?? 'visible']),
      draft: (map.getSource('road-draft') as import('maplibre-gl').GeoJSONSource).serialize().data,
      paint: map.getPaintProperty('local-water-lines', 'line-color') };
  });
  const before = await snapshot();
  await page.getByRole('button', { name: 'Menu', exact: true }).click();
  await page.getByRole('menuitem', { name: 'Settings' }).click();
  await page.getByRole('button', { name: 'Dark', exact: true }).click();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('dialog', { name: 'Settings' })).toHaveCount(0);
  await expect(page.getByRole('button', { name: 'Menu', exact: true })).toBeFocused();
  const after = await snapshot();
  expect(after.camera).toEqual(before.camera);
  expect(after.layers).toEqual(before.layers);
  expect(after.draft).toEqual(before.draft);
  expect(after.paint).not.toEqual(before.paint);
  await expect(page.getByRole('button', { name: 'Undo point' })).toBeEnabled();
});

// Provider requests are blocked by the deterministic fixture: exercise recovery without live services.
test('setup backtracking keeps the boundary and preparation failure is recoverable', async ({ page }) => {
  await page.setViewportSize({ width: 1920, height: 1080 });
  await openMenu(page);
  await page.getByRole('button', { name: 'New Resort' }).click();
  await page.getByRole('button', { name: /Select site/ }).click();
  await page.mouse.move(1050, 420);
  await page.mouse.down();
  await page.mouse.move(1230, 600, { steps: 8 });
  await page.mouse.up();
  const dimensions = await page.locator('.site-dims').textContent();
  await page.getByRole('button', { name: 'Back', exact: true }).click();
  await page.getByRole('button', { name: /Select site/ }).click();
  await expect(page.locator('.site-dims')).toHaveText(dimensions!);
  await page.getByRole('button', { name: 'View this area' }).click();
  await page.getByRole('button', { name: 'Prepare terrain' }).click();
  await expect(page.getByRole('heading', { name: 'Preparation failed' })).toBeVisible({ timeout: 20000 });
  await page.getByRole('button', { name: 'Back to boundary' }).click();
  await expect(page.locator('.site-dims')).toHaveText(dimensions!);
  await page.getByRole('button', { name: 'View this area' }).click();
  await page.getByRole('button', { name: 'Prepare terrain' }).click();
  await expect(page.getByRole('heading', { name: 'Preparation failed' })).toBeVisible({ timeout: 20000 });
});

test('library search and confirmed deletion refresh Continue', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'My Resorts' }).click();
  const library = page.getByRole('dialog', { name: 'My Resorts' });
  await library.getByRole('searchbox').fill('unmatched');
  await expect(library.getByText(/No resorts match/)).toBeVisible();
  await library.getByRole('searchbox').fill('Deterministic');
  await library.getByRole('button', { name: 'Delete Deterministic Peak' }).click();
  await expect(library.getByRole('button', { name: 'Keep resort' })).toBeVisible();
  await library.getByRole('button', { name: 'Keep resort' }).click();
  await expect(library.getByRole('button', { name: 'Open resort' })).toBeVisible();
  await library.getByRole('button', { name: 'Delete Deterministic Peak' }).click();
  await library.getByRole('button', { name: 'Delete resort', exact: true }).click();
  await expect(library.getByText(/No resorts yet/)).toBeVisible();
  await page.keyboard.press('Escape');
  await expect(page.getByRole('button', { name: /^Continue /  })).toHaveCount(0);
});

test('system appearance tracks live changes and persists explicit scale', async ({ page }) => {
  await page.emulateMedia({ colorScheme: 'light' });
  await openMenu(page);
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'light');
  await page.emulateMedia({ colorScheme: 'dark' });
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await page.getByRole('button', { name: '125%', exact: true }).click();
  await page.keyboard.press('Escape');
  await page.reload();
  await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
  expect(await page.locator('html').evaluate((element) => element.style.getPropertyValue('--ui-scale'))).toBe('1.25');
});