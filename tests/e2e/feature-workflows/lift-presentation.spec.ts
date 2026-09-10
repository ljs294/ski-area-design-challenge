import { expect, test } from '../support/deterministicApp';
import type { Page } from '@playwright/test';
import { jumpTo, pointAt, setCaptureTransients, sourceFeatureCount, visibilityOf } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

const CENTER: [number, number] = [-121.495, 46.905];

const crossingLift = {
  id: 'lift-presentation',
  identifier: 'A',
  name: 'Presentation Express',
  liftTypeId: 'fixed-grip-double',
  points: [[-121.497, 46.905], [-121.493, 46.905]],
  endpointElevM: [1000, 1030],
  lengthM: 305,
  verticalM: 30,
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

const crossingRun = {
  id: 'trail-presentation-crossing',
  name: 'Presentation Run',
  parts: [{
    polygon: [[
      [-121.496, 46.903], [-121.494, 46.903],
      [-121.494, 46.907], [-121.496, 46.907], [-121.496, 46.903],
    ]],
    centerline: [[-121.495, 46.907], [-121.495, 46.903]],
    centerlineElevM: [1030, 1000],
  }],
  brushWidthM: 40,
  areaM2: 40_000,
  lengthM: 444,
  verticalM: 30,
  avgSlopeDeg: 4,
  maxSlopeDeg: 5,
  difficulty: 'blue',
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

async function hoverFilter(page: Page): Promise<unknown> {
  return page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getFilter(id: string): unknown;
    } }).appMap;
    return map.getFilter('lift-hover');
  });
}

test('lift hover wins at a crossing, clears during capture and hiding, and restores valid pointer state',
  async ({ page }) => {
    await seedPreparedResort(page, { lifts: [crossingLift], trails: [crossingRun] });
    await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
    await jumpTo(page, CENTER, 16);

    await expect.poll(() => sourceFeatureCount(page, 'lifts')).toBe(3);
    await expect.poll(() => sourceFeatureCount(page, 'lift-labels')).toBe(1);

    const crossing = await pointAt(page, CENTER);
    await page.mouse.move(crossing.x, crossing.y);
    await expect.poll(() => hoverFilter(page)).toEqual([
      'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
      ['==', ['get', 'id'], 'lift-presentation'],
    ]);

    await setCaptureTransients(page, true);
    await expect.poll(() => hoverFilter(page)).toEqual([
      'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
      ['==', ['get', 'id'], ''],
    ]);
    await setCaptureTransients(page, false);
    await expect.poll(() => hoverFilter(page)).toEqual([
      'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
      ['==', ['get', 'id'], 'lift-presentation'],
    ]);

    await page.getByRole('button', { name: 'Layers' }).click();
    await page.getByRole('checkbox', { name: 'Ski lifts', exact: true }).uncheck();
    await expect.poll(() => hoverFilter(page)).toEqual([
      'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
      ['==', ['get', 'id'], ''],
    ]);
    expect(await visibilityOf(page, 'lift-line-casing')).toBe('none');
    await page.getByRole('checkbox', { name: 'Ski lifts', exact: true }).check();
    await expect.poll(() => hoverFilter(page)).toEqual([
      'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
      ['==', ['get', 'id'], ''],
    ]);
  });

test('capture does not restore a lift hover cleared by tool activation', async ({ page }) => {
  await seedPreparedResort(page, { lifts: [crossingLift] });
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, CENTER, 16);

  const crossing = await pointAt(page, CENTER);
  await page.mouse.move(crossing.x, crossing.y);
  await expect.poll(() => hoverFilter(page)).toEqual([
    'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
    ['==', ['get', 'id'], 'lift-presentation'],
  ]);

  await setCaptureTransients(page, true);
  await expect.poll(() => hoverFilter(page)).toEqual([
    'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
    ['==', ['get', 'id'], ''],
  ]);
  // Arming another lift while capture is hiding transients clears the ranked
  // hit state. Unhiding must therefore leave the hover filter empty.
  await page.getByRole('button', { name: 'Toolbox', exact: true }).click();
  await page.getByRole('tab', { name: 'Lifts', exact: true }).click();
  await page.getByRole('button', { name: /Add ski lift/ }).click();
  await setCaptureTransients(page, false);
  await expect.poll(() => hoverFilter(page)).toEqual([
    'all', ['==', ['get', 'kind'], 'line'], ['==', ['get', 'draft'], false],
    ['==', ['get', 'id'], ''],
  ]);
  await page.keyboard.press('Escape');
});

test('each lift has one point label anchored at its distance midpoint', async ({ page }) => {
  await seedPreparedResort(page, { lifts: [crossingLift] });
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  const presentation = await page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getSource(id: string): { serialize(): { data?: unknown } } | undefined;
      getStyle(): { layers?: Array<{ id: string; source?: string; layout?: Record<string, unknown> }> };
    } }).appMap;
    const source = map.getSource('lift-labels')?.serialize().data as GeoJSON.FeatureCollection;
    return {
      features: source.features,
      layer: map.getStyle().layers?.find((layer) => layer.id === 'lift-labels'),
    };
  });

  expect(presentation.features).toHaveLength(1);
  expect(presentation.features[0]).toMatchObject({
    properties: { id: 'lift-presentation', label: 'A - Presentation Express' },
    geometry: { type: 'Point', coordinates: [-121.495, 46.905] },
  });
  expect(presentation.layer).toMatchObject({
    source: 'lift-labels',
    layout: { 'symbol-placement': 'point', 'text-field': ['get', 'label'] },
  });
});
