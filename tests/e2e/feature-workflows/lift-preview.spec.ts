import type { Page } from '@playwright/test';
import { expect, test } from '../support/deterministicApp';
import { jumpTo, pointAt, setCaptureTransients, sourceFeatureCount } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const coordinates = (page: Page) => page.evaluate(() => {
  const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
  const data = (map.getSource('lift-draft') as import('maplibre-gl').GeoJSONSource).serialize().data as GeoJSON.FeatureCollection;
  return (data.features.find(f => f.geometry.type === 'LineString')?.geometry as GeoJSON.LineString)?.coordinates;
});

// Run with `node scripts/runE2E.mjs --dev --project=feature-workflows lift-preview`
// to exercise main.tsx's real development StrictMode setup/cleanup/setup replay.
test('lift drawing survives effect replay and preserves its fixed base', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.495, 46.905], 16);
  await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
  await page.getByRole('tab', { name: 'Lifts', exact: true }).click();
  await page.getByRole('button', { name: /Add ski lift/ }).click();
  await page.getByRole('treeitem', { name: 'Detachable Chairlift', exact: true }).click();
  await page.getByRole('treeitem', { name: 'Six-Pack', exact: true }).click();
  await page.getByRole('button', { name: 'Draw lift', exact: true }).click();
  const base = await pointAt(page, [-121.4962, 46.9044]);
  const top = await pointAt(page, [-121.4938, 46.9056]);
  await page.mouse.click(base.x, base.y);
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(3);
  const anchored = await coordinates(page);
  expect(anchored[0]).toEqual(anchored[1]);
  await page.mouse.move(top.x, top.y);
  await expect.poll(async () => (await coordinates(page))[1]).not.toEqual(anchored[0]);
  expect((await coordinates(page))[0]).toEqual(anchored[0]);
  await expect.poll(() => page.evaluate(() => {
    const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    return map.queryRenderedFeatures({ layers: ['lift-draft-terminals'] }).length;
  })).toBeGreaterThan(0);
  await page.evaluate(() => {
    const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    map.setStyle(map.getStyle(), { diff: false });
  });
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(3);
  expect((await coordinates(page))[0]).toEqual(anchored[0]);
  await setCaptureTransients(page, true);
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(0);
  await setCaptureTransients(page, false);
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(3);
  await page.keyboard.press('Escape');
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(0);
  await page.mouse.move(base.x, base.y);
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(0);
  await page.getByRole('button', { name: /Add ski lift/ }).click();
  await page.getByRole('button', { name: 'Draw lift', exact: true }).click();
  await page.mouse.click(top.x, top.y);
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(3);
  expect((await coordinates(page))[0]).not.toEqual(anchored[0]);
  await page.keyboard.press('Escape');
  await expect.poll(() => sourceFeatureCount(page, 'lift-draft')).toBe(0);
});
