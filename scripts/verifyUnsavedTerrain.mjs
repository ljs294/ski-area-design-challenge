// E2E: terrain edits must only reach the disk when the player clicks Save.
//
// Builds a lift (which fells a ground-cover corridor), leaves without saving,
// and checks the reloaded package is back to its baseline; then repeats with a
// Save and checks the edit survives. window.appSaveState exposes the in-memory
// package checksums, because reading storage alone cannot tell a discarded
// edit from one that was never made.
//
//   node scripts/verifyUnsavedTerrain.mjs http://localhost:4173/ scratchpad
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const outDir = process.argv[3] ?? 'scratchpad';
const PREP_MS = 600_000;

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(String(e)));

const checks = [];
const check = (name, ok, detail = '') => {
  checks.push({ name, ok });
  console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? `  — ${detail}` : ''}`);
};

const saveState = () => page.evaluate(() => globalThis.appSaveState ?? null);
/**
 * Click without Playwright's post-click navigation wait. Anything that tears
 * down and rebuilds the map leaves that wait pending long after the click has
 * landed, which wedges the run for the full timeout on a click that worked.
 */
const hardClick = async (selector) => {
  const target = page.locator(selector).first();
  await target.waitFor({ state: 'visible', timeout: 30_000 });
  await target.evaluate((node) => node.click());
};
const waitStyle = async (global) => {
  await page.waitForFunction((k) => globalThis[k]?.isStyleLoaded?.(), global, { timeout: 30_000 });
  await page.waitForTimeout(1200);
};
/** The package is only usable once ingest has produced a cover grid. */
const waitPackage = () => page.waitForFunction(
  () => !!globalThis.appSaveState?.coverChecksum, null, { timeout: PREP_MS });

async function dismissLoadingScreen() {
  const loading = page.locator('.resort-loading');
  if (!(await loading.isVisible().catch(() => false))) return;
  const enterAnyway = page.getByRole('button', { name: 'Enter anyway' });
  await Promise.race([
    loading.waitFor({ state: 'detached', timeout: 120_000 }),
    enterAnyway.waitFor({ state: 'visible', timeout: 120_000 }),
  ]).catch(() => {});
  if (await enterAnyway.isVisible().catch(() => false)) await enterAnyway.click({ force: true });
  await loading.waitFor({ state: 'detached', timeout: 60_000 }).catch(() => {});
  await page.addStyleTag({
    content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
  });
}

// The dock circle toggles, so a blind click closes a dock that a previous step
// left open. Drive it from the state we actually need instead.
const addLiftBtn = page.locator('.lift-overview .lift-add-btn');
async function openLiftDock() {
  if (await addLiftBtn.isVisible().catch(() => false)) return;
  await page.click('.dock-circle-lifts');
  await addLiftBtn.waitFor({ state: 'visible', timeout: 15_000 });
}

/** Draw a lift across the resort; its corridor clearing is the cover edit. */
async function buildLift(ax, ay, bx, by) {
  await openLiftDock();
  await addLiftBtn.click();
  await page.waitForTimeout(300);
  await page.mouse.click(ax, ay);
  await page.waitForTimeout(400);
  await page.mouse.click(bx, by);
  await page.waitForSelector('.lift-panel', { timeout: 15_000 });
  await Promise.race([
    page.waitForSelector('text=Vertical', { timeout: 40_000 }),
    page.waitForSelector('.lift-link-btn', { timeout: 40_000 }),
  ]).catch(() => {});
  await page.click('.lift-panel .site-btn-primary');
  // The clearing runs in a worker behind the construction bug.
  await page.locator('.build-status-bug').waitFor({ state: 'detached', timeout: 120_000 })
    .catch(() => {});
  await page.waitForTimeout(2500);
}

const liftCount = async () => {
  await openLiftDock();
  return page.evaluate(() => {
  const title = document.querySelector('.lift-overview-title')?.textContent ?? '';
  const rows = document.querySelectorAll('.lift-row').length;
    const parsed = /\((\d+)\)/.exec(title);
    return parsed ? Number(parsed[1]) : rows;
  });
};

try {
  await page.goto(base, { waitUntil: 'load' });
  // Terrain packages live in IndexedDB, not localStorage: clearing only the
  // latter leaves a previous run's package behind and the run is not repeatable.
  await page.evaluate(async () => {
    localStorage.clear();
    const dbs = (await indexedDB.databases?.()) ?? [];
    await Promise.all(dbs.map(({ name }) => name && new Promise((resolve) => {
      const request = indexedDB.deleteDatabase(name);
      request.onsuccess = request.onerror = request.onblocked = () => resolve();
    })));
  });
  await page.reload({ waitUntil: 'load' });
  await waitStyle('menuMap');
  await page.click('.trail-slat >> text=New Game');
  await waitStyle('appMap');
  await page.click('.site-btn >> text=Select site');
  await page.waitForTimeout(300);
  await page.mouse.move(600, 380); await page.mouse.down();
  await page.mouse.move(780, 540, { steps: 15 }); await page.mouse.up();
  await page.waitForTimeout(400);
  await page.click('.site-btn >> text=View this area');
  await page.waitForSelector('.name-entry-input', { timeout: 8000 });
  await page.fill('.name-entry-input', 'Unsaved Terrain Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: PREP_MS });
  await waitPackage();
  await dismissLoadingScreen();

  const baseline = await saveState();
  console.log('baseline:', JSON.stringify(baseline));
  // The riskiest part of the change: a resort that was just saved must not
  // report itself dirty (the saved-design baseline is seeded from sanitized
  // state, not from the raw save file).
  check('a freshly saved resort reports no unsaved changes', baseline.unsaved === false,
    `unsaved=${baseline.unsaved}`);

  // ---- Build, then discard -------------------------------------------------
  await buildLift(500, 500, 720, 340);
  const built = await saveState();
  console.log('after build:', JSON.stringify(built));
  check('building marks the session unsaved', built.unsaved === true);
  const coverEdited = built.coverChecksum !== baseline.coverChecksum;
  check('the lift felled ground cover in memory', coverEdited,
    coverEdited ? '' : 'corridor was already clear — the terrain-dirty half of this run is inconclusive');
  check('terrain is flagged dirty', built.terrainDirty?.cover === true || coverEdited === false,
    JSON.stringify(built.terrainDirty));
  check('the menu shows the unsaved marker',
    await page.locator('.game-menu-dot').isVisible().catch(() => false));

  await page.click('.game-menu-btn');
  await hardClick('.hud-quit');
  const dialog = page.locator('.unsaved-panel');
  const prompted = await dialog.waitFor({ state: 'visible', timeout: 10_000 })
    .then(() => true, () => false);
  check('leaving with unsaved work raises the gate', prompted);
  await page.screenshot({ path: `${outDir}/unsaved-gate.png` });
  if (!prompted) throw new Error('No unsaved-changes dialog; the rest of the run cannot be trusted.');
  await hardClick('.unsaved-panel >> text=Discard changes');
  await page.waitForSelector('.main-menu', { timeout: 30_000 });
  await waitStyle('menuMap');

  await hardClick('.trail-slat >> text=Continue Game');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });
  await waitPackage();
  await dismissLoadingScreen();
  const afterDiscard = await saveState();
  console.log('after discard + reload:', JSON.stringify(afterDiscard));
  check('a discarded terrain edit is not on disk',
    afterDiscard.coverChecksum === baseline.coverChecksum,
    `${afterDiscard.coverChecksum} vs baseline ${baseline.coverChecksum}`);
  check('a discarded lift is not on disk either', (await liftCount()) === 0);

  // ---- Build, then save ----------------------------------------------------
  await buildLift(500, 500, 720, 340);
  await page.click('.game-menu-btn');
  await hardClick('.hud-save');
  await page.waitForFunction(() => globalThis.appSaveState?.unsaved === false,
    null, { timeout: 180_000 });
  const saved = await saveState();
  console.log('after save:', JSON.stringify(saved));
  check('saving clears the unsaved state', saved.unsaved === false);

  await page.click('.game-menu-btn');
  await hardClick('.hud-quit');
  const promptedAgain = await dialog.waitFor({ state: 'visible', timeout: 4000 })
    .then(() => true, () => false);
  check('leaving with nothing pending does not prompt', promptedAgain === false);
  await page.waitForSelector('.main-menu', { timeout: 30_000 });
  await waitStyle('menuMap');

  await hardClick('.trail-slat >> text=Continue Game');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });
  await waitPackage();
  await dismissLoadingScreen();
  const afterSave = await saveState();
  console.log('after save + reload:', JSON.stringify(afterSave));
  check('a saved terrain edit survives the reload',
    afterSave.coverChecksum === saved.coverChecksum,
    `${afterSave.coverChecksum} vs saved ${saved.coverChecksum}`);
  check('the saved lift survives the reload', (await liftCount()) === 1);
  await page.screenshot({ path: `${outDir}/unsaved-after-save.png` });
} catch (e) {
  check('run completed', false, e.message);
  // Where it actually stalled, since the message alone rarely says.
  await page.screenshot({ path: `${outDir}/unsaved-failure.png` }).catch(() => {});
}

console.log('=== ERRORS ===');
console.log(errors.join('\n') || '(none)');
const failed = checks.filter((c) => !c.ok);
console.log(`\n${checks.length - failed.length}/${checks.length} checks passed`);
await browser.close();
process.exit(failed.length ? 1 : 0);
