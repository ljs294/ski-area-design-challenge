import { expect, test } from '../support/deterministicApp';
import { jumpTo, pointAt, setCaptureTransients, sourceFeatureCount, visibilityOf } from '../support/mapProbe';
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

test('an imported pond can be designated for snowmaking and persisted', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.4965, 46.9063], 17);

  const lake = await pointAt(page, [-121.4965, 46.9063]);
  await page.mouse.click(lake.x, lake.y);
  await expect(page.getByText('Context Lake', { exact: true })).toBeVisible();
  await page.getByRole('checkbox', { name: 'Snowmaking pond' }).check();
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(1);

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(saved).toMatchObject({ schemaVersion: 11, snowmakingLakeIds: ['way/lake'],
    snowmakingNodes: [{ name: 'Context Lake Intake',
      source: { kind: 'lake', lakeId: 'way/lake' } }] });

  await page.getByRole('checkbox', { name: 'Snowmaking pond' }).uncheck();
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(0);
});

test('draws and persists a numbered snowmaking pipe network', async ({ page }) => {
  await seedPreparedResort(page);
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.495, 46.905], 16);

  await page.getByRole('button', { name: 'Snowmaking' }).click();
  await page.getByRole('button', { name: /Install snowmaking pipe/ }).click();
  const options = page.getByRole('group', { name: 'Snowmaking pipe options' });
  await expect(options).toBeVisible();
  await expect(options.getByRole('option')).toHaveCount(11);
  await expect(options.getByRole('checkbox', { name: 'Node snapping' })).not.toBeChecked();
  await options.getByRole('combobox', { name: 'Pipe diameter' }).selectOption('12');

  const start = await pointAt(page, [-121.496, 46.9045]);
  const end = await pointAt(page, [-121.4935, 46.906]);
  await page.mouse.click(start.x, start.y);
  await page.mouse.click(end.x, end.y);
  await page.getByRole('button', { name: 'Finish route' }).click();
  await page.getByRole('textbox', { name: 'Pipe name' }).fill('Summit Main');
  await page.getByRole('button', { name: 'Install pipe' }).click();
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(1);

  await page.mouse.move(end.x, end.y);
  await expect.poll(() => page.locator('.maplibregl-canvas')
    .evaluate((canvas) => canvas.style.cursor)).toBe('pointer');

  await page.getByRole('button', { name: 'Place one hydrant' }).click();
  const singleSnap = page.getByRole('checkbox', {
    name: 'Snap single hydrant to snowmaking network',
  });
  await expect(singleSnap).toBeVisible();
  await singleSnap.check();
  await expect(singleSnap).toBeChecked();
  await singleSnap.uncheck();
  const hydrant = await pointAt(page, [-121.4945, 46.905]);
  await page.mouse.click(hydrant.x, hydrant.y);
  await page.getByRole('button', { name: 'Place hydrant' }).click();
  await page.getByRole('button', { name: 'Done' }).click();
  await expect(page.getByRole('button', { name: /Hydrant 1/ })).toBeVisible();

  await page.getByRole('button', { name: 'Place hydrants along pipe' }).click();
  const pipeTarget = await pointAt(page, [-121.49475, 46.90525]);
  await page.mouse.click(pipeTarget.x, pipeTarget.y);
  await expect(page.getByText('Select run start', { exact: true })).toBeVisible();
  const runStart = await pointAt(page, [-121.49575, 46.90465]);
  await page.mouse.move(runStart.x, runStart.y);
  await expect.poll(() => page.evaluate(() => {
    const source = (window as unknown as { appMap: { getSource(id: string): {
      serialize(): { data?: { features?: { properties?: { kind?: string; label?: string } }[] } };
    } | undefined } }).appMap.getSource('snowmaking-network-draft');
    return source?.serialize().data?.features?.some((feature) =>
      feature.properties?.kind === 'endpoint' && feature.properties.label === 'S') ?? false;
  })).toBe(true);
  await page.mouse.click(runStart.x, runStart.y);
  await expect(page.getByText('Select run end', { exact: true })).toBeVisible();
  const runEnd = await pointAt(page, [-121.49375, 46.90585]);
  await page.mouse.click(runEnd.x, runEnd.y);
  await expect(page.getByText('Review hydrant run', { exact: true })).toBeVisible();
  await page.getByRole('spinbutton', { name: 'Hydrant position count' }).fill('4');
  await expect(page.getByRole('button', { name: 'Place 4 hydrants' })).toBeEnabled();
  await page.getByRole('button', { name: 'Place 4 hydrants' }).click();
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(6);
  await expect(page.getByRole('button', { name: /Hydrant 5/ })).toBeVisible();

  await page.getByRole('button', { name: '＋ Install snowguns' }).click();
  const gunType = page.getByRole('combobox', { name: 'Snowgun type' });
  await expect(gunType.getByRole('option')).toHaveCount(4);
  await page.mouse.click(hydrant.x, hydrant.y);
  await gunType.selectOption('HKD_ImpulseR5_20t');
  // A clear upper-right map location is well beyond the 50 ft hookup radius.
  await page.mouse.click(1000, 200);
  await expect(page.getByText('Plan total').locator('..')).toContainText('$15,000');
  await setCaptureTransients(page, true);
  await expect.poll(() => visibilityOf(page, 'snowmaking-gun-draft')).toBe('none');
  await setCaptureTransients(page, false);
  await expect.poll(() => visibilityOf(page, 'snowmaking-gun-draft')).toBe('visible');
  await page.getByRole('button', { name: 'Review 2 snowguns' }).click();
  await expect(page.getByText(/Disconnected guns will be built/)).toBeVisible();
  await page.getByRole('button', { name: /Build 2 snowguns/ }).click();
  // Pipe + five hydrants + two guns + the connected gun's hookup line.
  await expect.poll(() => sourceFeatureCount(page, 'snowmaking-network')).toBe(9);

  await page.getByRole('button', { name: 'Mountain Dashboards' }).click();
  await page.getByRole('group', { name: 'Mountain dashboards' })
    .getByRole('button', { name: 'Snowmaking', exact: true }).click();
  await expect(page.getByRole('checkbox', { name: 'Show snowgun types' })).not.toBeChecked();
  await expect(page.getByText('Catalog value').locator('..')).toContainText('$15,000');
  await expect(page.getByLabel('Warning: disconnected snowgun')).toBeVisible();
  await page.getByRole('button', { name: /Close snowmaking map/ }).click();

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(saved).toMatchObject({
    schemaVersion: 11,
    snowmakingPipes: [{ name: 'Summit Main', diameterIn: 12 }],
    snowmakingNodes: [
      { kind: 'hydrant', labelNumber: 1 }, { kind: 'hydrant', labelNumber: 2 },
      { kind: 'hydrant', labelNumber: 3 }, { kind: 'hydrant', labelNumber: 4 },
      { kind: 'hydrant', labelNumber: 5 },
    ],
    snowmakingNodeNextNumbers: { hydrant: 6, junction: 1, pump: 1 },
    snowguns: [
      { variantId: 'HKD_ImpulseR5_10s', hydrantId: expect.any(String) },
      { variantId: 'HKD_ImpulseR5_20t', hydrantId: null },
    ],
  });
});

test('analyzes a branched snowmaking system without persisting the scenario', async ({ page }) => {
  const analysisNodes = [
    nodes[0],
    { id: 'pump-primary', name: 'Primary Pump', kind: 'pump', labelNumber: 1,
      point: [-121.4968, 46.9042], elevM: 1010, createdAt: FIXED_TIME },
    { id: 'junction-1', name: 'Junction 1', kind: 'junction', labelNumber: 1,
      point: [-121.4963, 46.9045], elevM: 1010, createdAt: FIXED_TIME },
    { id: 'hydrant-1', name: 'Hydrant 1', kind: 'hydrant', labelNumber: 1,
      point: [-121.4958, 46.9049], elevM: 1020, createdAt: FIXED_TIME },
    { id: 'hydrant-2', name: 'Hydrant 2', kind: 'hydrant', labelNumber: 2,
      point: [-121.4958, 46.9041], elevM: 1000, createdAt: FIXED_TIME },
  ];
  const pipe = (id: string, name: string, from: typeof analysisNodes[number],
    to: typeof analysisNodes[number]) => ({
    id, name, diameterIn: 8,
    vertices: [
      { point: from.point, elevM: from.elevM, nodeId: from.id },
      { point: to.point, elevM: to.elevM, nodeId: to.id },
    ],
    lengthM: 100, verticalM: Math.abs((to.elevM ?? 0) - (from.elevM ?? 0)),
    createdAt: FIXED_TIME,
  });
  const analysisPipes = [
    pipe('pipe-suction', 'Suction', analysisNodes[0], analysisNodes[1]),
    pipe('pipe-trunk', 'Trunk', analysisNodes[1], analysisNodes[2]),
    pipe('pipe-north', 'North Branch', analysisNodes[2], analysisNodes[3]),
    pipe('pipe-south', 'South Branch', analysisNodes[2], analysisNodes[4]),
  ];
  const analysisGuns = [
    { id: 'gun-1', variantId: 'HKD_ImpulseR5_10s', point: analysisNodes[3].point,
      elevM: 1020, hydrantId: 'hydrant-1', createdAt: FIXED_TIME },
    { id: 'gun-2', variantId: 'HKD_ImpulseR5_10s', point: analysisNodes[4].point,
      elevM: 1000, hydrantId: 'hydrant-2', createdAt: FIXED_TIME },
  ];

  await seedPreparedResort(page, {
    dams: [dam], snowmakingNodes: analysisNodes, snowmakingPipes: analysisPipes,
    snowguns: analysisGuns,
    snowmakingNodeNextNumbers: { hydrant: 3, junction: 2, pump: 2 },
  });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });

  await page.getByRole('button', { name: 'Snowmaking' }).click();
  await page.getByRole('button', { name: 'Analyze snowmaking system' }).click();
  const analyzer = page.locator('.snowmaking-analysis-panel');
  await expect(analyzer).toBeVisible();

  for (const checkbox of await analyzer.locator('.snowmaking-analysis-checklist').first()
    .getByRole('checkbox').all()) await checkbox.check();
  for (const checkbox of await analyzer.locator('.snowmaking-analysis-checklist').nth(1)
    .getByRole('checkbox').all()) await checkbox.check();
  await analyzer.getByRole('checkbox', { name: 'Pump On' }).check();
  await analyzer.getByRole('spinbutton', { name: 'Primary Pump horsepower' }).fill('500');
  await analyzer.getByRole('spinbutton', {
    name: 'Wet-bulb temperature in Fahrenheit',
  }).fill('9');
  await analyzer.getByRole('button', { name: 'Check system' }).click();

  await expect(analyzer).toContainText('116 GPM');
  await expect(analyzer).toContainText('6,960 gal/hr');
  await expect(analyzer).toContainText('System ready');
  await expect(analyzer.getByText('Ready', { exact: true })).toHaveCount(2);

  await analyzer.getByRole('spinbutton', {
    name: 'Wet-bulb temperature in Fahrenheit',
  }).fill('14');
  await expect(analyzer).toContainText('Inputs changed. Check the system again');
  await page.getByRole('button', { name: 'Close system analyzer' }).click();

  await page.getByRole('button', { name: 'Analyze snowmaking system' }).click();
  const reopened = page.locator('.snowmaking-analysis-panel');
  await expect(reopened.getByRole('spinbutton', {
    name: 'Wet-bulb temperature in Fahrenheit',
  })).toHaveValue('28');
  await expect(reopened.locator('.snowmaking-analysis-checklist input:checked')).toHaveCount(0);
  for (const checkbox of await reopened.locator('.snowmaking-analysis-checklist').first()
    .getByRole('checkbox').all()) await checkbox.check();
  await expect(reopened.getByRole('checkbox', { name: 'Pump On' })).not.toBeChecked();
  await page.getByRole('button', { name: 'Close system analyzer' }).click();

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  const savedText = await page.evaluate(() => localStorage.getItem('gamesave:e2e-save') ?? '');
  expect(JSON.parse(savedText)).toMatchObject({ schemaVersion: 11 });
  expect(savedText).not.toContain('horsepowerHp');
  expect(savedText).not.toContain('selectedPipeIds');
});
