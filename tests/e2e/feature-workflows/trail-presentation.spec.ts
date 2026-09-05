import { expect, test } from '@playwright/test';
import { haversineMeters } from '../../../src/geo';
import { jumpTo, pointAt } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const CENTER: [number, number] = [-121.495, 46.905];
const rect = (west: number, south: number, east: number, north: number) => [[
  [west, south], [east, south], [east, north], [west, north], [west, south],
]];

function savedRun(id: string, name: string, polygon: number[][][], centerline: number[][],
  segments?: Record<string, unknown>[]) {
  return {
    id, name, parts: [{ polygon, centerline, centerlineElevM: centerline.map((_, index) => 1100 - index * 20),
      segments }], brushWidthM: 20, areaM2: 4000, lengthM: 100, verticalM: 40,
    avgSlopeDeg: 10, maxSlopeDeg: 12, difficulty: id === 'branch' ? 'green' : 'blue',
    status: 'complete', createdAt: '2026-01-01T00:00:00.000Z',
  };
}

async function trailSource(page: Page) {
  return page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getSource(id: string): { serialize(): { data?: unknown } } | undefined;
    } }).appMap;
    return map.getSource('trails')?.serialize().data as GeoJSON.FeatureCollection;
  });
}

test('overlapping run swaths become one surface while repeated clicks cycle their identities', async ({ page }) => {
  const polygon = rect(-121.49535, 46.9047, -121.49465, 46.9053);
  const alpha = savedRun('alpha', 'Alpha Run', polygon,
    [[-121.49505, 46.9053], [-121.49505, 46.9047]]);
  const beta = savedRun('beta', 'Beta Run', polygon,
    [[-121.49495, 46.9053], [-121.49495, 46.9047]]);
  await seedPreparedResort(page, { trails: [beta, alpha] });
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, CENTER, 16);

  await expect.poll(async () => (await trailSource(page)).features
    .filter((feature) => feature.properties?.kind === 'surface').length).toBe(1);
  const source = await trailSource(page);
  expect(source.features.filter((feature) => feature.properties?.kind === 'line-label' ||
    feature.properties?.kind === 'head-label')).toHaveLength(2);

  const overlap = await pointAt(page, CENTER);
  await page.mouse.click(overlap.x, overlap.y);
  await expect(page.getByText('Alpha Run', { exact: true })).toBeVisible();
  await page.mouse.click(overlap.x, overlap.y);
  await expect(page.getByText('Beta Run', { exact: true })).toBeVisible();
});

test('a terminating branch yields while its same-run continuation remains drawn through the node', async ({ page }) => {
  const node = CENTER;
  const top: [number, number] = [-121.495, 46.90545];
  const bottom: [number, number] = [-121.495, 46.90455];
  const branchStart: [number, number] = [-121.49565, 46.905];
  const through = savedRun('through', 'Through Run',
    rect(-121.49513, 46.9045, -121.49487, 46.9055), [top, node, bottom], [
      { id: 'through:upper', centerline: [top, node], centerlineElevM: [1120, 1080],
        fromJunctionId: 'top', toJunctionId: 'join' },
      { id: 'through:lower', centerline: [node, bottom], centerlineElevM: [1080, 1040],
        fromJunctionId: 'join', toJunctionId: 'bottom' },
    ]);
  const branch = savedRun('branch', 'Branch Run',
    rect(-121.4957, 46.90491, -121.49495, 46.90509), [branchStart, node], [
      { id: 'branch:end', centerline: [branchStart, node], centerlineElevM: [1100, 1080],
        fromJunctionId: 'branch-start', toJunctionId: 'join' },
    ]);
  await seedPreparedResort(page, { trails: [branch, through] });
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await expect.poll(async () => (await trailSource(page)).features
    .filter((feature) => feature.properties?.kind === 'route').length).toBeGreaterThan(0);
  const routes = (await trailSource(page)).features.filter((feature) =>
    feature.properties?.kind === 'route');
  const throughPoints = routes.filter((feature) => feature.properties?.id === 'through')
    .flatMap((feature) => (feature.geometry as GeoJSON.LineString).coordinates as [number, number][]);
  const branchPoints = routes.filter((feature) => feature.properties?.id === 'branch')
    .flatMap((feature) => (feature.geometry as GeoJSON.LineString).coordinates as [number, number][]);
  expect(Math.min(...throughPoints.map((coordinate) => haversineMeters(coordinate, node))))
    .toBeLessThan(1);
  expect(Math.min(...branchPoints.map((coordinate) => haversineMeters(coordinate, node))))
    .toBeGreaterThan(9);
});
