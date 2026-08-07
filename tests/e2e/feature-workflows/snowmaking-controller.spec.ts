import { expect, test } from '../support/deterministicApp';
import { pointAt, setCaptureTransients, sourceFeatureCount } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const FIXED_TIME = '2026-01-01T00:00:00.000Z';
const dam = {
  id: 'dam-seed', name: 'Dam Seed', points: [[-121.497, 46.904], [-121.496, 46.905]],
  crestElevationM: 1010, streamId: 'stream-seed', streamName: 'Seed Creek',
  sourceWidthM: 3, inflowM3s: 0.5,
  pondRings: [[[-121.497, 46.904], [-121.496, 46.904], [-121.496, 46.905],
    [-121.497, 46.904]]],
  areaM2: 100, averageDepthM: 2, capacityM3: 200, maxDamHeightM: 4,
  createdAt: FIXED_TIME,
};
const pond = {
  id: 'pond-seed', name: 'Pond Seed', boundary: [[-121.494, 46.904],
    [-121.493, 46.904], [-121.493, 46.905], [-121.494, 46.904]],
  topElevationM: 1015, areaM2: 100, averageDepthM: 2, maxDepthM: 3,
  capacityM3: 200, isSnowmaking: true, createdAt: FIXED_TIME,
};
const nodes = [
  { id: 'dam-node', name: 'Dam Intake', kind: 'intake', point: dam.points[0],
    elevM: 1010, source: { kind: 'dam', damId: dam.id }, createdAt: FIXED_TIME },
  { id: 'pond-node', name: 'Pond Intake', kind: 'intake', point: pond.boundary[0],
    elevM: 1015, source: { kind: 'pond', pondId: pond.id }, createdAt: FIXED_TIME },
];

test('snowmaking façade owns contributions, reconciliation, editing, and persistence', async ({ page }) => {
  await seedPreparedResort(page, { dams: [dam], ponds: [pond], snowmakingNodes: nodes });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await expect.poll(() => sourceFeatureCount(page, 'player-dams')).toBeGreaterThan(0);
  await expect.poll(() => sourceFeatureCount(page, 'player-standalone-ponds')).toBe(1);
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(2);

  await page.getByRole('button', { name: 'Snowmaking' }).click();
  await page.getByRole('button', { name: /Build standalone pond/ }).click();
  const point = await pointAt(page, [-121.495, 46.905]);
  await page.mouse.click(point.x, point.y);
  await expect.poll(() => sourceFeatureCount(page, 'standalone-pond-draft')).toBeGreaterThan(0);
  await setCaptureTransients(page, true);
  await expect.poll(() => sourceFeatureCount(page, 'standalone-pond-draft')).toBe(0);
  await setCaptureTransients(page, false);
  await expect.poll(() => sourceFeatureCount(page, 'standalone-pond-draft')).toBeGreaterThan(0);
  await page.getByRole('button', { name: 'Cancel' }).click();

  await page.getByRole('button', { name: /Dam Intake/ }).click();
  await page.locator('.snowmaking-panel .lift-name-input').fill('Renamed Intake');
  await expect.poll(() => page.evaluate(() => {
    const source = (window as unknown as { appMap: { getSource(id: string): {
      serialize(): { data?: { features?: { properties?: { name?: string } }[] } } } } }).appMap
      .getSource('snowmaking-network');
    return source.serialize().data?.features?.map((feature) => feature.properties?.name) ?? [];
  })).toContain('Renamed Intake');
  await page.getByRole('button', { name: 'Close' }).click();

  await page.getByRole('button', { name: /^Pond Seed/ }).click();
  await page.getByRole('checkbox', { name: 'Snowmaking pond' }).uncheck();
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(1);
  await page.getByRole('button', { name: 'Close' }).click();
  await page.getByRole('button', { name: /^Dam Seed/ }).click();
  await page.getByRole('button', { name: 'Remove dam' }).click();
  await expect.poll(() => sourceFeatureCount(page, 'player-dams')).toBe(0);
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(0);

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(saved).toMatchObject({ schemaVersion: 11, dams: [],
    ponds: [{ id: 'pond-seed', isSnowmaking: false }], snowmakingNodes: [] });
});
