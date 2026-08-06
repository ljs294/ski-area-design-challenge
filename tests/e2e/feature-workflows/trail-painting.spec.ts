import { expect, test } from '../support/deterministicApp';
import { jumpTo, pointAt } from '../support/mapProbe';
import { seedPreparedResort } from '../support/preparedResort';

/**
 * The painting engine, observed through the panel it feeds. Unlike the other
 * workers it holds state — the canvas accumulates strokes — so this is the one
 * adapter whose contract cannot be read off a single request. Painting proves
 * the whole exchange: the engine initializes, the seed dab is replayed onto the
 * empty canvas, and each stroke comes back as a larger footprint in order.
 */

/** The lift whose top terminal anchors the run. Its higher end is a trailhead. */
const TOP: [number, number] = [-121.4942, 46.905];

const anchorLift = {
  id: 'lift-anchor',
  name: 'Anchor Double',
  liftClass: 'fixed-grip',
  chairSize: 2,
  points: [[-121.4958, 46.905], [-121.4942, 46.905]],
  endpointElevM: [1000, 1030],
  lengthM: 122,
  verticalM: 30,
  status: 'complete',
  createdAt: '2026-01-01T00:00:00.000Z',
};

test('painting from a lift terminal seeds an engine and grows the reported footprint', async ({ page }) => {
  await seedPreparedResort(page, { lifts: [anchorLift] });
  await page.getByRole('button', { name: 'Continue Game' }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 15_000 });
  await jumpTo(page, TOP, 17);

  await page.getByRole('button', { name: 'Ski runs' }).click();
  await page.getByRole('button', { name: /Create Trail/ }).click();
  await expect(page.getByText('Place Trailhead', { exact: true })).toBeVisible();

  // Anchoring starts an engine, replays the seed dab, and reports its area.
  const head = await pointAt(page, TOP);
  await page.mouse.move(head.x, head.y);
  await page.mouse.click(head.x, head.y);
  await expect(page.getByText('Create Trail', { exact: true })).toBeVisible();

  const paintedArea = page.locator('.trail-panel .readout-line .lift-stat-value');
  const finish = page.getByRole('button', { name: 'Finish' });
  await expect(finish).toBeDisabled();
  // Widening the brush before the first stroke restarts the engine outright.
  // The seed has to be replayed onto the replacement, at the new width, so a
  // measurable dab here is the proof that the ready-then-replay handshake ran.
  await page.locator('.trail-brush-slider').fill('120');
  await expect(paintedArea).not.toHaveText(/^~?0(\.0)?\s/, { timeout: 10_000 });
  const seeded = (await paintedArea.textContent())?.trim() ?? '';
  await expect(finish).toBeDisabled();

  // One stroke down the fall line. Its preview must land, or Finish stays
  // disabled and the painted area never moves off the seed dab.
  await page.mouse.move(head.x, head.y);
  await page.mouse.down();
  await page.mouse.move(head.x, head.y + 60, { steps: 6 });
  await page.mouse.move(head.x + 30, head.y + 120, { steps: 6 });
  await page.mouse.up();

  await expect(finish).toBeEnabled({ timeout: 10_000 });
  await expect(paintedArea).not.toHaveText(seeded);

  // Undo returns the canvas to the seed dab, which only an engine that kept
  // the stroke history in order can report.
  await page.getByRole('button', { name: 'Undo' }).click();
  await expect(paintedArea).toHaveText(seeded, { timeout: 10_000 });
  await expect(finish).toBeDisabled();
});
