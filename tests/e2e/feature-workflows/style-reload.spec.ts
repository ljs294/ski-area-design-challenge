import { expect, test } from '../support/deterministicApp';
import { countSourceUpdates, jumpTo, layerIds, pointAt, sourceFeatureCount,
  sourceUpdateCounts, visibilityOf } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

/**
 * The map contribution registry, observed through the live style. The unit
 * tests in `src/app/mapContribution.test.ts` prove the declared orders and the
 * guards derived from them; these prove the registry actually drives the map:
 * every family is installed in the declared order, a light↔dark restyle rebuilds
 * that order and the player's hidden layers exactly, and a click lands on the
 * family that picks first.
 */

/**
 * A low and a high layer from each family's own block, listed in the declared
 * bottom-to-top paint order. Asserting a span per family — rather than one
 * representative — catches a family that installs its own layers out of order
 * as well as a family that lands in the wrong place in the stack.
 */
const FAMILY_BLOCKS: { family: string; first: string; last: string }[] = [
  { family: 'analysis', first: 'cover-fill', last: 'contour-labels' },
  { family: 'site-boundary', first: 'site-mask-fill', last: 'site-box-line-solid' },
  { family: 'road', first: 'road-pavement', last: 'road-draft-vertices' },
  { family: 'dam', first: 'dam-embankment-fill', last: 'dam-preview-points' },
  { family: 'pond', first: 'standalone-pond-fill', last: 'standalone-pond-preview-points' },
  { family: 'ski-node-path', first: 'path-casing', last: 'path-draft-pick' },
  { family: 'trail', first: 'trail-fill', last: 'trail-labels' },
  { family: 'lift', first: 'lift-line-casing', last: 'lift-labels' },
  { family: 'snowmaking', first: 'snowmaking-pipe-casing', last: 'snowmaking-pipe-hit' },
];

/** Every layer a hit guard names. A missing id would silently stop guarding. */
const HIT_LAYERS = [
  'snowmaking-gun-hit',
  'snowmaking-node-hit',
  'snowmaking-pipe-hit',
  'lift-line-hit',
  'lift-terminals',
  'trail-hit',
  'dam-hit',
  'dam-pond-hit',
  'standalone-pond-hit',
  'road-hit',
  'local-water-line-hit',
  'local-water-fill',
];

/** Assert the declared paint order, given the live style's layer list. */
function expectDeclaredOrder(ids: string[]): void {
  for (const block of FAMILY_BLOCKS) {
    expect(ids, `${block.family} installs ${block.first}`).toContain(block.first);
    expect(ids, `${block.family} installs ${block.last}`).toContain(block.last);
    expect(ids.indexOf(block.first), `${block.family} keeps its own order`)
      .toBeLessThan(ids.indexOf(block.last));
  }
  for (let i = 1; i < FAMILY_BLOCKS.length; i += 1) {
    const below = FAMILY_BLOCKS[i - 1];
    const above = FAMILY_BLOCKS[i];
    expect(ids.indexOf(below.last), `${above.family} draws over ${below.family}`)
      .toBeLessThan(ids.indexOf(above.first));
  }
  for (const layerId of HIT_LAYERS) {
    expect(ids, `${layerId} backs a hit guard`).toContain(layerId);
  }
}

/** Explicitly rebuild the style; theme changes now update paint in place. */
async function restyle(page: Page): Promise<void> {
  await page.evaluate(() => new Promise<void>((resolve) => {
    const map = (window as unknown as { appMap: import('maplibre-gl').Map }).appMap;
    const style = map.getStyle();
    map.once('style.load', () => resolve());
    // A fresh basemap exercises registry restoration, without serializing custom renderer layers.
    map.setStyle({ version: 8, glyphs: style.glyphs, sources: {},
      layers: [{ id: 'mp-paper', type: 'background', paint: { 'background-color': '#e8e5dc' } }] }, { diff: false });
  }));
  // The restyle drops every added layer and reinstalls the registry on
  // style.load; wait for the topmost family to come back before asserting.
  await expect
    .poll(async () => (await layerIds(page)).includes('snowmaking-pipe-hit'), { timeout: 10_000 })
    .toBe(true);
}

test('a restyle reinstalls every map family in the declared order and keeps hidden layers hidden', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  expectDeclaredOrder(await layerIds(page));
  await expect.poll(() => sourceFeatureCount(page, 'local-context')).toBe(2);
  await expect.poll(() => sourceFeatureCount(page, 'roads')).toBeGreaterThan(0);

  // Hide analysis, structure, and shared analysis/road-family descriptors.
  await page.getByRole('button', { name: 'Layers' }).click();
  await page.getByRole('checkbox', { name: 'Contours' }).uncheck();
  await page.getByRole('checkbox', { name: 'Ski trails' }).uncheck();
  await page.getByRole('checkbox', { name: 'Roads', exact: true }).uncheck();
  expect(await visibilityOf(page, 'contour-lines')).toBe('none');
  expect(await visibilityOf(page, 'trail-fill')).toBe('none');
  expect(await visibilityOf(page, 'road-pavement')).toBe('none');
  expect(await visibilityOf(page, 'road-yellow-centerline')).toBe('none');
  expect(await visibilityOf(page, 'road-white-divider')).toBe('none');
  expect(await visibilityOf(page, 'lift-line-casing')).not.toBe('none');

  await restyle(page);

  expectDeclaredOrder(await layerIds(page));
  await expect.poll(() => sourceFeatureCount(page, 'local-context')).toBe(2);
  await expect.poll(() => sourceFeatureCount(page, 'roads')).toBeGreaterThan(0);
  expect(await visibilityOf(page, 'contour-lines')).toBe('none');
  expect(await visibilityOf(page, 'trail-fill')).toBe('none');
  expect(await visibilityOf(page, 'road-pavement')).toBe('none');
  expect(await visibilityOf(page, 'road-yellow-centerline')).toBe('none');
  expect(await visibilityOf(page, 'road-white-divider')).toBe('none');
  expect(await visibilityOf(page, 'lift-line-casing')).not.toBe('none');
  await expect(page.getByRole('checkbox', { name: 'Contours' })).not.toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'Ski trails' })).not.toBeChecked();
  await expect(page.getByRole('checkbox', { name: 'Roads', exact: true })).not.toBeChecked();

  // Leaving the resort runs the map teardown; the deterministic fixture fails
  // the test on any uncaught exception it raises.
  await page.getByRole('button', { name: 'Menu' }).click();
  await page.getByRole('menuitem', { name: 'Main Menu' }).click();
  await expect(page.getByRole('navigation', { name: 'Main menu' })).toBeVisible();
});

test('imported and player-built paved roads open the same read-only detail', async ({ page }) => {
  await seedPreparedResort(page, { roads: [{
    id: 'road/seed', name: 'Seed Road', roadType: 'two-lane', widthM: 7,
    points: [[-121.497, 46.902], [-121.493, 46.902]], lengthM: 305,
    terrainGraded: true, createdAt: '2026-01-01T00:00:00.000Z',
  }] });
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.495, 46.905], 12);
  await countSourceUpdates(page, ['roads']);

  const imported = await pointAt(page, [-121.495, 46.905]);
  await page.mouse.click(imported.x, imported.y);
  const importedDetail = page.locator('.road-detail');
  await expect(importedDetail.getByText('Context Road', { exact: true })).toBeVisible();
  await expect(importedDetail.getByText('Lane estimate', { exact: true })).toBeVisible();
  await importedDetail.getByRole('button', { name: 'Close', exact: true }).click();

  await jumpTo(page, [-121.495, 46.902], 16);
  const built = await pointAt(page, [-121.495, 46.902]);
  await page.mouse.click(built.x, built.y);
  const builtDetail = page.locator('.road-detail');
  await expect(builtDetail.getByText('Seed Road', { exact: true })).toBeVisible();
  await expect(builtDetail.getByText('Player-built', { exact: true })).toBeVisible();
  await expect(builtDetail.locator('input')).toHaveCount(0);
  expect((await sourceUpdateCounts(page)).roads).toBe(0);
});

// A lift laid straight across the middle of a run, so one click point sits on
// both families and another sits on the run alone.
const CROSSING: [number, number] = [-121.495, 46.905];
const RUN_ONLY: [number, number] = [-121.495, 46.9055];

const crossingLift = {
  id: 'lift-crossing',
  name: 'Crossing Double',
  liftTypeId: 'fixed-grip-double',
  points: [[-121.4958, 46.905], [-121.4942, 46.905]],
  endpointElevM: [1000, 1030],
  lengthM: 122,
  verticalM: 30,
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

const crossingRun = {
  id: 'trail-crossing',
  name: 'Crossed Run',
  parts: [{
    polygon: [[
      [-121.4965, 46.9042], [-121.4935, 46.9042],
      [-121.4935, 46.9058], [-121.4965, 46.9058], [-121.4965, 46.9042],
    ]],
    centerline: [[-121.495, 46.9058], [-121.495, 46.9042]],
    centerlineElevM: [1030, 1000],
  }],
  brushWidthM: 40,
  areaM2: 40000,
  lengthM: 178,
  verticalM: 30,
  avgSlopeDeg: 9,
  maxSlopeDeg: 12,
  difficulty: 'blue',
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

test('a click where a lift crosses a run picks the lift, and the run alone picks the run', async ({ page }) => {
  await seedPreparedResort(page, { lifts: [crossingLift], trails: [crossingRun] });
  await page.getByRole('button', { name: /^Continue /  }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await jumpTo(page, CROSSING, 16);

  // Hovering a pickable family marks the cursor; leaving every family clears it.
  const crossing = await pointAt(page, CROSSING);
  // Three pixels is outside the 3 px visual casing but inside the 8 px hit line.
  const liftHitPoint = { x: crossing.x, y: crossing.y + 3 };
  await page.mouse.move(liftHitPoint.x, liftHitPoint.y);
  await expect
    .poll(() => page.locator('.maplibregl-canvas').evaluate((el) => el.style.cursor))
    .toBe('pointer');

  await page.mouse.click(liftHitPoint.x, liftHitPoint.y);
  await expect(page.locator('.dock-lifts')).toBeVisible();
  await expect(page.getByText('Crossing Double')).toBeVisible();
  await expect(page.locator('.dock-trails')).toHaveCount(0);

  const runOnly = await pointAt(page, RUN_ONLY);
  await page.mouse.click(runOnly.x, runOnly.y);
  await expect(page.locator('.dock-trails')).toBeVisible();
  await expect(page.getByText('Crossed Run')).toBeVisible();
  await expect(page.locator('.dock-lifts')).toHaveCount(0);
});
