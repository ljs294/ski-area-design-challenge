import { expect, test } from '../support/deterministicApp';
import { jumpTo, pointAt, setCaptureTransients, sourceFeatureCount } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const trail = (id: string, name: string, x: number) => ({
  id, name,
  parts: [{ polygon: [[[x - 0.0003, 46.9035], [x + 0.0003, 46.9035],
    [x + 0.0003, 46.9065], [x - 0.0003, 46.9065], [x - 0.0003, 46.9035]]],
  centerline: [[x, 46.906], [x, 46.904]], centerlineElevM: [1030, 1000] }],
  brushWidthM: 40, areaM2: 20_000, lengthM: 222, verticalM: 30,
  avgSlopeDeg: 8, maxSlopeDeg: 12, difficulty: 'blue', status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
});

test('node/path controller commits one atomic connector and restores its draft', async ({ page }) => {
  await seedPreparedResort(page, { trails: [
    trail('trail-west', 'West Run', -121.496), trail('trail-east', 'East Run', -121.494),
  ] });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.495, 46.905], 16);

  await page.getByRole('button', { name: 'Ski runs' }).click();
  await page.getByRole('button', { name: /Draw path/ }).click();
  const from = await pointAt(page, [-121.496, 46.905]);
  const to = await pointAt(page, [-121.494, 46.905]);
  await page.mouse.click(from.x, from.y);
  await expect.poll(() => sourceFeatureCount(page, 'node-path-draft')).toBeGreaterThan(0);
  await setCaptureTransients(page, true);
  await expect.poll(() => sourceFeatureCount(page, 'node-path-draft')).toBe(0);
  await setCaptureTransients(page, false);
  await expect.poll(() => sourceFeatureCount(page, 'node-path-draft')).toBeGreaterThan(0);
  await page.mouse.click(to.x, to.y);
  await page.getByRole('button', { name: 'Finish', exact: true }).click();
  const reviewName = page.locator('.trail-panel .lift-name-input');
  await expect(reviewName).toBeVisible();
  await reviewName.fill('Atomic Connector');
  await page.getByRole('button', { name: 'Build path' }).click();
  await expect.poll(() => sourceFeatureCount(page, 'node-paths')).toBeGreaterThan(0);

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(saved.paths).toHaveLength(1);
  expect(saved.paths[0]).toMatchObject({ name: 'Atomic Connector', status: 'complete',
    from: { kind: 'trail', trailId: 'trail-west' },
    to: { kind: 'trail', trailId: 'trail-east' } });
  expect(saved.junctions).toHaveLength(6);
  expect(saved.paths[0].fromJunctionId).toBeTruthy();
  expect(saved.paths[0].toJunctionId).toBeTruthy();
  expect(saved.junctions.map((junction: { id: string }) => junction.id)).toEqual(
    expect.arrayContaining([saved.paths[0].fromJunctionId, saved.paths[0].toJunctionId]));
});
