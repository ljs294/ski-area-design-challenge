import { expect, test } from '../support/deterministicApp';
import type { Page } from '@playwright/test';
import { countSourceUpdates, jumpTo, pointAt, setCaptureTransients, sourceFeatureCount,
  sourceUpdateCounts } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';
import { installWorkerProbe, workerEntries } from '../support/workerProbe';

/**
 * The painting engine, observed through the panel it feeds. Unlike the other
 * workers it holds state — the canvas accumulates strokes — so this is the one
 * adapter whose contract cannot be read off a single request. Painting proves
 * the whole exchange: the engine initializes, the seed dab is replayed onto the
 * empty canvas, and each stroke comes back as a larger footprint in order.
 */

/** The lift whose top terminal anchors the run. Its higher end is a trailhead. */
const TOP: [number, number] = [-121.4942, 46.905];
const TAIL: [number, number] = [-121.4942, 46.9035];

const anchorLift = {
  id: 'lift-anchor',
  name: 'Anchor Double',
  liftTypeId: 'fixed-grip-double',
  points: [[-121.4958, 46.905], [-121.4942, 46.905]],
  endpointElevM: [1000, 1030],
  lengthM: 122,
  verticalM: 30,
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

const destinationLift = {
  id: 'lift-destination',
  name: 'Destination Double',
  liftTypeId: 'fixed-grip-double',
  points: [TAIL, [-121.4928, 46.9035]],
  endpointElevM: [980, 1020],
  lengthM: 106,
  verticalM: 40,
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

async function paintToReview(page: Page): Promise<void> {
  await page.getByRole('button', { name: 'Ski runs' }).click();
  await page.getByRole('button', { name: /Create Trail/ }).click();
  const head = await pointAt(page, TOP);
  const tail = await pointAt(page, TAIL);
  await page.mouse.click(head.x, head.y);
  await expect(page.getByText('Create Trail', { exact: true })).toBeVisible();
  await expect.poll(async () => (await workerEntries(page, 'trailPaint.worker')).length).toBe(1);
  const finish = page.getByRole('button', { name: 'Finish', exact: true });
  await page.mouse.move(head.x, head.y);
  await page.mouse.down();
  await page.mouse.move(tail.x, tail.y, { steps: 16 });
  await page.mouse.up();
  await expect(finish).toBeEnabled({ timeout: 10_000 });
  await finish.click();
  await expect(page.getByText('Place Trail End', { exact: true })).toBeVisible();
  await page.mouse.click(tail.x, tail.y);
  await expect(page.getByText('Review ski run', { exact: true })).toBeVisible({ timeout: 10_000 });
}

test('painting from a lift terminal seeds an engine and grows the reported footprint', async ({ page }) => {
  await installWorkerProbe(page);
  await seedPreparedResort(page, { lifts: [anchorLift] }, { contourSegmentCount: 5_000 });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, TOP, 17);

  await page.getByRole('button', { name: 'Ski runs' }).click();
  await page.getByRole('button', { name: /Create Trail/ }).click();
  await expect(page.getByText('Place Trailhead', { exact: true })).toBeVisible();

  // Anchoring starts an engine, replays the seed dab, and reports its area.
  const head = await pointAt(page, TOP);
  await page.evaluate(() => new Promise<void>((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
  await countSourceUpdates(page, ['contours', 'trails', 'trail-draft', 'trail-paint-preview']);
  // The first pointer event lets MapLibre finish any deferred initial source
  // publication. Measure the sustained hover path after that warm-up.
  await page.mouse.move(head.x - 120, head.y);
  await page.evaluate(() => new Promise<void>((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
  const beforeHover = await sourceUpdateCounts(page);
  await page.mouse.move(head.x, head.y, { steps: 30 });
  await page.evaluate(() => new Promise<void>((resolve) =>
    requestAnimationFrame(() => requestAnimationFrame(() => resolve()))));
  const afterHover = await sourceUpdateCounts(page);
  expect(afterHover.contours).toBe(beforeHover.contours);
  expect(afterHover.trails).toBe(beforeHover.trails);
  expect(afterHover['trail-draft']).toBe(beforeHover['trail-draft']);
  await page.mouse.click(head.x, head.y);
  await expect(page.getByText('Create Trail', { exact: true })).toBeVisible();
  await expect.poll(async () => (await workerEntries(page, 'trailPaint.worker')).length).toBe(1);

  const paintedArea = page.locator('.trail-panel .readout-line .lift-stat-value');
  const finish = page.getByRole('button', { name: 'Finish' });
  await expect(finish).toBeDisabled();
  // Widening the brush before the first stroke restarts the engine outright.
  // The seed has to be replayed onto the replacement, at the new width, so a
  // measurable dab here is the proof that the ready-then-replay handshake ran.
  await page.locator('.trail-brush-slider').fill('120');
  await expect.poll(async () => workerEntries(page, 'trailPaint.worker')).toMatchObject([
    { terminationCount: 1 },
    { terminationCount: 0 },
  ]);
  await expect(paintedArea).not.toHaveText(/^~?0(\.0)?\s/, { timeout: 10_000 });
  const seeded = (await paintedArea.textContent())?.trim() ?? '';
  await expect(finish).toBeDisabled();

  // One stroke down the fall line. Its preview must land, or Finish stays
  // disabled and the painted area never moves off the seed dab.
  const beforeStroke = await sourceUpdateCounts(page);
  await page.mouse.move(head.x, head.y);
  await page.mouse.down();
  await page.mouse.move(head.x, head.y + 60, { steps: 6 });
  await page.mouse.move(head.x + 30, head.y + 120, { steps: 6 });
  await page.evaluate(() => new Promise<void>((resolve) => requestAnimationFrame(() => resolve())));
  const duringStroke = await sourceUpdateCounts(page);
  expect(duringStroke.contours).toBe(beforeStroke.contours);
  expect(duringStroke.trails).toBe(beforeStroke.trails);
  expect(duringStroke['trail-draft']).toBe(beforeStroke['trail-draft']);
  expect(duringStroke['trail-paint-preview']).toBeGreaterThan(beforeStroke['trail-paint-preview']);
  await page.mouse.up();

  await expect(finish).toBeEnabled({ timeout: 10_000 });
  await expect(paintedArea).not.toHaveText(seeded);

  // Undo returns the canvas to the seed dab, which only an engine that kept
  // the stroke history in order can report.
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(paintedArea).toHaveText(seeded, { timeout: 10_000 });
  await expect(finish).toBeDisabled();

  await page.getByRole('button', { name: 'Close' }).click();
  await expect.poll(async () => workerEntries(page, 'trailPaint.worker')).toMatchObject([
    { terminationCount: 1 },
    { terminationCount: 1 },
  ]);
});

test('review retains a grade failure and commits trail topology coherently', async ({ page }) => {
  await installWorkerProbe(page, { failPostFor: 'terrainGrade.worker' });
  await seedPreparedResort(page, { lifts: [anchorLift, destinationLift] });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.4942, 46.90425], 17);

  await paintToReview(page);
  const name = page.locator('.trail-panel .lift-name-input');
  await name.fill('Atomic Glade');
  await expect.poll(() => sourceFeatureCount(page, 'trail-draft')).toBeGreaterThan(0);
  await setCaptureTransients(page, true);
  await expect.poll(() => sourceFeatureCount(page, 'trail-draft')).toBe(0);
  await setCaptureTransients(page, false);
  await expect.poll(() => sourceFeatureCount(page, 'trail-draft')).toBeGreaterThan(0);

  const grade = page.getByRole('checkbox');
  await expect(grade).toBeEnabled({ timeout: 10_000 });
  await grade.check();
  await expect(page.getByText('Terrain grading could not start. Try again.', { exact: true }))
    .toBeVisible();
  await expect(page.getByText('Review ski run', { exact: true })).toBeVisible();
  await grade.uncheck();
  await page.getByRole('button', { name: 'Add to plan' }).click();
  await expect.poll(() => sourceFeatureCount(page, 'trails')).toBeGreaterThan(0);

  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(saved.schemaVersion).toBe(15);
  expect(saved.trails).toHaveLength(1);
  expect(saved.trails[0]).toMatchObject({ name: 'Atomic Glade', status: 'planning',
    terrainGraded: false, anchor: { kind: 'lift', liftId: 'lift-anchor', end: 'top' } });
  const segment = saved.trails[0].parts[0].segments[0];
  expect(segment.fromJunctionId).toBeTruthy();
  expect(segment.toJunctionId).toBeTruthy();
  expect(saved.junctions.map((junction: { id: string }) => junction.id)).toEqual(
    expect.arrayContaining([segment.fromJunctionId, segment.toJunctionId]));
});

test('graded trail confirmation is atomic and survives best-effort cover failure', async ({ page }) => {
  await installWorkerProbe(page, { failPostFor: 'coverEdit.worker' });
  await seedPreparedResort(page, { lifts: [anchorLift, destinationLift] });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, [-121.4942, 46.90425], 17);
  await paintToReview(page);

  await page.locator('.trail-panel .lift-name-input').fill('Graded Glade');
  await page.getByRole('button', { name: 'Complete', exact: true }).click();
  const grade = page.getByRole('checkbox');
  await expect(grade).toBeEnabled({ timeout: 10_000 });
  await grade.check();
  const build = page.getByRole('button', { name: 'Build run', exact: true });
  await expect(build).toBeEnabled({ timeout: 15_000 });
  await build.evaluate((button) => { button.click(); button.click(); });

  await expect.poll(() => sourceFeatureCount(page, 'trails')).toBeGreaterThan(0);
  await expect.poll(async () => (await workerEntries(page, 'coverEdit.worker')).length).toBe(1);
  await page.getByRole('button', { name: /^Menu/ }).click();
  await page.locator('.hud-save').click();
  await expect.poll(() => page.evaluate(() =>
    (window as unknown as { appSaveState: { unsaved: boolean } }).appSaveState.unsaved,
  )).toBe(false);
  const saved = await page.evaluate(() =>
    JSON.parse(localStorage.getItem('gamesave:e2e-save') ?? 'null'));
  expect(saved.trails).toHaveLength(1);
  expect(saved.trails[0]).toMatchObject({ name: 'Graded Glade', status: 'complete',
    terrainGraded: true });
  expect(saved.trails[0].parts[0].segments).toHaveLength(1);
});
