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
  await page.getByRole('button', { name: 'Continue Game' }).click();
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

  await expect(page.getByTestId('pump-house-overview')).toContainText('North Pump House');
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
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await page.getByRole('button', { name: 'Snowmaking' }).click();
  await expect(page.getByTestId('pump-house-overview')).toContainText('North Pump House');
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
