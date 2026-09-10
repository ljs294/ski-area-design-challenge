import { expect, openMenu, test } from '../support/deterministicApp';

for (const viewport of [{ width: 800, height: 600 }, { width: 1440, height: 900 }]) {
test(`bundled menu covers a full orbit with providers blocked at ${viewport.width}px`, async ({ page }, info) => {
  test.setTimeout(180000);
  await page.setViewportSize(viewport);
  await page.addInitScript(() => localStorage.setItem('skiapp:settings', JSON.stringify({
    renderQuality: 'high', reducedMotion: true,
  })));
  const errors: string[] = [];
  page.on('console', (event) => { if (event.type() === 'error') errors.push(event.text()); });
  await openMenu(page);
  await expect(page.locator('.menu-backdrop-map')).toHaveAttribute('data-ready', 'true', { timeout: 30000 });
  // Inspect the full scene at native resolution on the software GPU. The
  // renderProfile tests separately cover backing-resolution quality policy.
  await page.evaluate(() => {
    (window as unknown as { menuMap: import('maplibre-gl').Map }).menuMap.setPixelRatio(1);
  });
  await expect(page.locator('.trail-timber-post')).toHaveCount(2);
  for (const post of await page.locator('.trail-timber-post').all()) {
    const bounds = await post.boundingBox();
    expect(bounds!.y + bounds!.height).toBeGreaterThanOrEqual(page.viewportSize()!.height);
  }
  await expect(page.locator('[data-rating="blue"]')).toHaveCount(1);
  await expect(page.locator('[data-rating="black"]')).toHaveCount(1);
  for (let bearing = 0; bearing < 360; bearing += 30) {
    await page.evaluate((bearing) => {
      const map = (window as unknown as { menuMap: import('maplibre-gl').Map }).menuMap;
      map.jumpTo({ bearing });
    }, bearing);
    await expect.poll(() => page.evaluate(() => {
      const map = (window as unknown as { menuMap: import('maplibre-gl').Map }).menuMap;
      return map.areTilesLoaded();
    }), { timeout: 30000 }).toBe(true);
    await expect(page.locator('.menu-backdrop-map')).toHaveAttribute('data-ready', 'true');
    expect(await page.evaluate(() => {
      const map = (window as unknown as { menuMap: import('maplibre-gl').Map }).menuMap;
      return map.getPaintProperty('menu-hillshade', 'hillshade-illumination-anchor');
    })).toBe('map');
    await page.screenshot({ path: info.outputPath(`orbit-${bearing}.png`) });
  }
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  const radii = await page.locator('.trail-sign, .trail-nameplate, .trail-menu-footer button, .settings-panel, .settings-panel button').evaluateAll(
    (elements) => elements.map((element) => getComputedStyle(element).borderTopLeftRadius));
  expect(new Set(radii)).toEqual(new Set(['2px']));
  expect(errors).toEqual([]);
});
}

test('missing background asset retains a usable fallback', async ({ page }) => {
  await page.route('**/menu-background/manifest.json', (route) => route.fulfill({ status: 404, body: 'missing' }));
  await openMenu(page);
  await expect(page.locator('.menu-backdrop-map')).toHaveAttribute('data-ready', 'false');
  await page.getByRole('button', { name: 'My Resorts', exact: true }).click();
  await expect(page.getByRole('dialog', { name: 'My Resorts' })).toBeVisible();
});

test('corrupt cover is rejected while menu actions remain usable', async ({ page }) => {
  const diagnostics: string[] = [];
  page.on('console', (event) => { if (event.type() === 'error') diagnostics.push(event.text()); });
  await page.route('**/menu-background/smooth-cover/**/*.png', (route) => route.fulfill({ body: 'corrupt', contentType: 'image/png' }));
  await openMenu(page);
  await expect.poll(() => diagnostics.some((line) => line.includes('Corrupt bundled menu tile'))).toBe(true);
  await expect(page.locator('.menu-backdrop-map')).toHaveAttribute('data-ready', 'false');
  await page.getByRole('button', { name: 'Settings', exact: true }).click();
  await expect(page.getByRole('dialog')).toBeVisible();
});

test('local background preserves motion and reduced motion settings', async ({ page }) => {
  await page.setViewportSize({ width: 800, height: 600 });
  await openMenu(page);
  await page.evaluate(() => localStorage.setItem('skiapp:settings', JSON.stringify({ renderQuality: 'high', reducedMotion: false })));
  await page.reload();
  await expect(page.locator('.menu-backdrop-map')).toHaveAttribute('data-ready', 'true', { timeout: 30000 });
  const bearing = () => page.evaluate(() => (window as unknown as { menuMap: import('maplibre-gl').Map }).menuMap.getBearing());
  const start = await bearing();
  await expect.poll(bearing).toBeGreaterThan(start + .05);
  await page.evaluate(() => localStorage.setItem('skiapp:settings', JSON.stringify({ renderQuality: 'high', reducedMotion: true })));
  await page.reload();
  await expect(page.locator('.menu-backdrop-map')).toHaveAttribute('data-ready', 'true', { timeout: 30000 });
  const stopped = await bearing();
  // Sampling over multiple animation frames verifies a stable camera without a fixed sleep.
  await page.evaluate(() => new Promise<void>((resolve) => {
    let frames = 0; const tick = () => { if (++frames === 12) resolve(); else requestAnimationFrame(tick); }; tick();
  }));
  expect(await bearing()).toBe(stopped);
});
