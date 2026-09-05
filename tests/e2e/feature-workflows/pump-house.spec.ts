import { expect, test } from '../support/deterministicApp';
import { pointAt } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

/**
 * The pump-house selectors intentionally follow the same deterministic map
 * fixture as the other feature workflows. This keeps the two placement clicks
 * and the review/detail contract covered without relying on map tile content.
 */
test('places, edits, saves, selects, and removes a pump house', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await page.getByRole('button', { name: 'Snowmaking' }).click();
  await expect(page.getByTestId('pump-house-overview')).toBeVisible();
  await page.getByTestId('build-pump-house').click();

  const center = await pointAt(page, [-121.495, 46.905]);
  await page.mouse.move(center.x, center.y);
  await expect(page.getByTestId('pump-house-placement-preview')).toContainText('60 ft');
  await page.mouse.click(center.x, center.y);
  await expect(page.getByTestId('pump-house-placement')).toContainText('long-axis direction');

  const headingPoint = await pointAt(page, [-121.494, 46.905]);
  await page.mouse.move(headingPoint.x, headingPoint.y);
  await page.mouse.click(headingPoint.x, headingPoint.y);
  await expect(page.getByTestId('pump-house-review')).toBeVisible();
  await expect(page.getByTestId('pump-house-review')).toContainText('Fixed gable');
  await expect(page.getByTestId('pump-house-review')).toContainText('1,000 hp / 85% efficiency');

  await page.getByTestId('pump-house-name').fill('North Pump House');
  await page.getByRole('radio', { name: /Level structure on slope/ }).check();
  await expect(page.getByTestId('pump-house-review')).toContainText('eight perimeter samples');
  await expect(page.getByTestId('confirm-pump-house')).toBeEnabled({ timeout: 15_000 });
  await page.getByTestId('confirm-pump-house').click();

  await expect(page.getByTestId('pump-house-detail')).toContainText('North Pump House');
  await page.getByTestId('pump-house-detail').getByRole('button', { name: 'Close', exact: true }).click();
  await page.getByRole('button', { name: '3D', exact: true }).click();
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { appMap: { getPitch(): number } }).appMap.getPitch()))
    .toBeGreaterThan(45);
  await expect.poll(() => page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getLayer(id: string): { type?: string } | undefined;
      getLayoutProperty(id: string, property: string): unknown;
      getSource(id: string): { serialize(): { data?: { features?: Array<{
        properties?: Record<string, unknown>; geometry?: { coordinates?: unknown };
      }> } } };
      project(point: [number, number]): { x: number; y: number };
      queryRenderedFeatures(point: { x: number; y: number }, options: { layers: string[] }): unknown[];
    } }).appMap;
    const source = map.getSource('player-buildings').serialize().data;
    const feature = source?.features?.find((row) => row.properties?.kind === 'building-footprint');
    const center = [-121.495, 46.905] as [number, number];
    return {
      layerType: map.getLayer('building-extrusion')?.type,
      visible: map.getLayoutProperty('building-extrusion', 'visibility'),
      heightM: feature?.properties?.heightM,
      rendered: map.queryRenderedFeatures(map.project(center),
        { layers: ['building-extrusion'] }).length,
    };
  })).toMatchObject({ layerType: 'fill-extrusion', visible: 'visible',
    heightM: expect.any(Number), rendered: 1 });
  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  await expect.poll(() => page.evaluate(() => {
    const save = JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null');
    return {
      buildingName: save?.buildings?.[0]?.name,
      buildingNodeId: save?.buildings?.[0]?.connection?.nodeId,
      pumpNodeId: save?.snowmakingNodes?.find((node: { ownerBuildingId?: string }) =>
        node.ownerBuildingId === save?.buildings?.[0]?.id)?.id,
    };
  })).toEqual({ buildingName: 'North Pump House',
    buildingNodeId: expect.any(String), pumpNodeId: expect.any(String) });

  await page.reload();
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await page.getByRole('button', { name: 'Snowmaking' }).click();
  await expect(page.getByTestId('pump-house-overview')).toContainText('North Pump House');
  await page.keyboard.press('2');
  await expect(page.getByRole('complementary', { name: 'Snowmaking dashboard' })).toBeVisible();
  await expect.poll(() => page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getLayoutProperty(id: string, property: string): unknown;
      getSource(id: string): { serialize(): { data?: { features?: Array<{
        properties?: Record<string, unknown>;
      }> } } };
    } }).appMap;
    const features = map.getSource('dashboard-map').serialize().data?.features ?? [];
    return {
      normalBuilding: map.getLayoutProperty('building-extrusion', 'visibility'),
      planBuildingVisible: map.getLayoutProperty('dashboard-snow-buildings', 'visibility'),
      building: features.find((row) => row.properties?.kind === 'snow-building')?.properties,
      label: features.find((row) => row.properties?.kind === 'snow-building-label')?.properties,
      pump: features.find((row) => row.properties?.kind === 'snow-node' &&
        row.properties?.nodeKind === 'pump')?.properties,
    };
  })).toMatchObject({
    normalBuilding: 'none', planBuildingVisible: 'visible',
    building: { name: 'North Pump House', pumpNodeId: expect.any(String) },
    label: { name: 'North Pump House' }, pump: { nodeKind: 'pump' },
  });
  const pumpPoint = await pointAt(page, [-121.495, 46.905]);
  await page.mouse.click(pumpPoint.x, pumpPoint.y);
  await expect(page.locator('.network-inspector[data-inspector="node"]'))
    .toContainText('Owned by building');
  await page.keyboard.press('2');
  await expect(page.getByRole('complementary', { name: 'Snowmaking dashboard' })).toHaveCount(0);
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { appMap: { getLayoutProperty(id: string, property: string): unknown } })
      .appMap.getLayoutProperty('building-extrusion', 'visibility'))).toBe('visible');
  await page.getByRole('button', { name: 'Snowmaking', exact: true }).click();
  await page.locator('[data-testid^="pump-house-row-"]').filter({ hasText: 'North Pump House' }).click();
  await expect(page.getByTestId('pump-house-detail')).toContainText('Dimensions, heading, roof, and foundation are locked');
  await expect(page.getByTestId('pump-house-detail')).toContainText('Capital cost');

  await page.getByTestId('pump-house-built-name').fill('Renamed Pump House');
  await expect(page.getByTestId('pump-house-detail')).toContainText('Renamed Pump House');
  await page.getByTestId('remove-pump-house-start').click();
  await expect(page.getByTestId('pump-house-remove-confirm')).toContainText('owned pump');
  await page.getByTestId('remove-pump-house').click();
  await expect(page.getByTestId('pump-house-overview')).toContainText('No pump houses yet.');
});
