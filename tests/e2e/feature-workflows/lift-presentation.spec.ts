import { expect, test } from '../support/deterministicApp';
import type { Page } from '@playwright/test';
import { jumpTo, pointAt, setCaptureTransients, sourceFeatureCount, visibilityOf } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';
import { haversineMeters } from '../../../src/geo';

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

const angledLabelLift = {
  id: 'lift-label-angle',
  identifier: 'B',
  name: 'Angle Express',
  liftTypeId: 'fixed-grip-double',
  // A diagonal line makes the map-plane rotation observable while retaining
  // the persisted two-terminal lift contract.
  points: [[-121.4975, 46.9035], [-121.4932, 46.9068]],
  endpointElevM: [1000, 1015],
  lengthM: 380,
  verticalM: 15,
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

function midpointAndAngle(points: readonly [number, number][]): {
  point: [number, number];
  angle: number;
} {
  const mercatorY = (latitude: number): number => {
    const clamped = Math.max(-85.051129, Math.min(85.051129, latitude));
    const radians = clamped * Math.PI / 180;
    return 0.5 - Math.log((1 + Math.sin(radians)) / (1 - Math.sin(radians))) / (4 * Math.PI);
  };
  const lengths = points.slice(1).map((point, index) => haversineMeters(points[index], point));
  const total = lengths.reduce((sum, length) => sum + length, 0);
  let traveled = 0;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index]!;
    if (traveled + length >= total / 2) {
      const from = points[index]!;
      const to = points[index + 1]!;
      const ratio = length > 0 ? (total / 2 - traveled) / length : 0;
      let deltaX = (to[0] - from[0]) / 360;
      if (deltaX > 0.5) deltaX -= 1;
      if (deltaX < -0.5) deltaX += 1;
      const deltaY = mercatorY(to[1]) - mercatorY(from[1]);
      return {
        point: [from[0] + (to[0] - from[0]) * ratio, from[1] + (to[1] - from[1]) * ratio],
        angle: Math.atan2(deltaY, deltaX) * 180 / Math.PI,
      };
    }
    traveled += length;
  }
  const from = points.at(-2)!;
  const to = points.at(-1)!;
  let deltaX = (to[0] - from[0]) / 360;
  if (deltaX > 0.5) deltaX -= 1;
  if (deltaX < -0.5) deltaX += 1;
  return { point: to, angle: Math.atan2(mercatorY(to[1]) - mercatorY(from[1]), deltaX) * 180 / Math.PI };
}

async function labelPresentation(page: Page): Promise<{
  features: GeoJSON.Feature[];
  layer: { source?: string; layout?: Record<string, unknown>; paint?: Record<string, unknown> } | undefined;
}> {
  return page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getSource(id: string): { serialize(): { data?: unknown } } | undefined;
      getStyle(): { layers?: Array<{ id: string; source?: string; layout?: Record<string, unknown>; paint?: Record<string, unknown> }> };
    } }).appMap;
    const source = map.getSource('lift-labels')?.serialize().data as GeoJSON.FeatureCollection | undefined;
    return {
      features: source?.features ?? [],
      layer: map.getStyle().layers?.find((layer) => layer.id === 'lift-labels'),
    };
  });
}

async function restyle(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((resolve) => {
    const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    const style = map.getStyle();
    map.once('style.load', () => resolve());
    map.setStyle({ version: 8, glyphs: style.glyphs, sources: {},
      layers: [{ id: 'mp-paper', type: 'background', paint: { 'background-color': '#e8e5dc' } }] }, { diff: false });
  }));
  await expect.poll(async () => (await labelPresentation(page)).features.length, { timeout: 10_000 }).toBe(1);
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

test('lift labels keep red paint, midpoint angle, and upright alignment through theme and style reloads',
  async ({ page }, testInfo) => {
    await seedPreparedResort(page, { lifts: [angledLabelLift] });
    await page.getByRole('button', { name: /^Continue / }).click();
    await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
    await jumpTo(page, CENTER, 16);

    const expected = midpointAndAngle(angledLabelLift.points as [number, number][]);
    const expectLabelStyle = async () => {
      const presentation = await labelPresentation(page);
      expect(presentation.features).toHaveLength(1);
      expect(presentation.features[0]).toMatchObject({
        properties: { id: angledLabelLift.id, label: 'B - Angle Express' },
        geometry: { type: 'Point' },
      });
      const coordinates = (presentation.features[0].geometry as GeoJSON.Point).coordinates as [number, number];
      expect(coordinates[0]).toBeCloseTo(expected.point[0], 8);
      expect(coordinates[1]).toBeCloseTo(expected.point[1], 8);
      const angle = Number((presentation.features[0].properties as { angle?: unknown } | null)?.angle);
      expect(Number.isFinite(angle)).toBe(true);
      // Text rotation is periodic over 180 degrees: reversing a lift line
      // leaves the label parallel to the same midpoint segment.
      const parallel = ((angle - expected.angle + 90) % 180 + 180) % 180 - 90;
      expect(Math.abs(parallel)).toBeLessThan(0.5);
      expect(presentation.layer).toMatchObject({
        source: 'lift-labels',
        layout: {
          'symbol-placement': 'point',
          'text-field': ['get', 'label'],
          'text-rotate': ['get', 'angle'],
          'text-rotation-alignment': 'map',
          'text-pitch-alignment': 'map',
        },
        paint: {
          'text-color': '#d42027',
          'text-halo-color': '#ffffff',
          'text-halo-width': 2,
        },
      });
    };

    await expect.poll(async () => (await labelPresentation(page)).features.length).toBe(1);
    await expectLabelStyle();

    await page.getByRole('button', { name: 'Theme and map colors', exact: true }).click();
    const appearance = page.getByRole('dialog', { name: 'Theme and map colors', exact: true });
    await appearance.getByRole('button', { name: 'Dark', exact: true }).click();
    await expect(page.locator('html')).toHaveAttribute('data-theme', 'dark');
    await expectLabelStyle();

    await restyle(page);
    await expectLabelStyle();

    await page.evaluate(() => {
      const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
      map.jumpTo({ bearing: 45, pitch: 40 });
    });
    await expect.poll(async () => {
      const camera = await page.evaluate(() => {
        const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
        return { bearing: map.getBearing(), pitch: map.getPitch() };
      });
      return Math.abs(camera.bearing - 45) < 0.1 && Math.abs(camera.pitch - 40) < 0.1;
    }).toBe(true);
    await expectLabelStyle();
    await page.screenshot({ path: testInfo.outputPath('red-aligned-label.png'), fullPage: true });
  });
