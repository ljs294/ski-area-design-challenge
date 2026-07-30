// Browser-level check for the trail/lift network graph and its node map view.
// Builds a small resort — one lift plus two runs that cross — then verifies the
// things the feature promises:
//
//   1. the node map opens from the top-left and draws an element per edge,
//      with the crossing runs SPLIT into segments;
//   2. clicking the lift lists the runs it serves and dims everything it
//      cannot reach;
//   3. clicking a segment shows that segment's own rating / slope / acreage;
//   4. closing a run marks its segments closed, and the closure survives a
//      save + reload (it is persisted on the SavedTrail).
//
// Run against a preview server with network access so the normal New Resort
// terrain preparation can complete:
//   node scripts/verifyNetworkMap.mjs http://localhost:4173/
//
// Prefers a real GPU. Under SwiftShader a wide paint can stall the page's main
// thread for tens of seconds, which is why every wait here is generous and the
// buttons are driven through the DOM.
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1400, height: 900 } });
page.setDefaultTimeout(150_000);
const errors = [];
page.on('pageerror', (error) => errors.push(String(error)));
page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });

// Rasterizing a wide brush stroke can block the main thread long enough under
// software WebGL that a normal click never gets dispatched. Drive the button
// directly once Playwright has confirmed it is the right, enabled element.
async function clickWhenReady(selector) {
  const button = page.locator(selector);
  await button.waitFor({ state: 'visible', timeout: 60_000 });
  await page.waitForFunction((s) => !document.querySelector(s)?.disabled,
    selector, { timeout: 60_000 });
  await button.evaluate((element) => element.click());
}

/** Arm the brush, paint one stroke, Finish the footprint, then Build the run. */
async function paintRun(from, to) {
  await page.locator('.lift-add-btn >> text=Add ski run').click();
  await page.waitForSelector('.trail-panel', { timeout: 30_000 });
  await page.mouse.move(from[0], from[1]);
  await page.mouse.down();
  await page.mouse.move(to[0], to[1], { steps: 60 });
  await page.evaluate(() => window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })));
  await clickWhenReady('.trail-panel button.site-btn-primary'); // Finish
  // Skeletonization + terrain sampling run before the review panel appears.
  await page.waitForSelector('.trail-panel .lift-status-btn', { timeout: 90_000 });
  await page.locator('.trail-panel .lift-status-btn >> text=Complete').click();
  await page.waitForSelector('.trail-panel .site-actions >> text=Build run', { timeout: 90_000 });
  await clickWhenReady('.trail-panel button.site-btn-primary'); // Build run
  await page.waitForSelector('.trail-panel', { state: 'detached', timeout: 90_000 }).catch(() => {});
  await page.waitForTimeout(800);
}

/** Read the drawn node map back out of the DOM. */
const readMap = () => page.evaluate(() => {
  const edges = [...document.querySelectorAll('[data-edge-id]')];
  return {
    trailEdgeIds: edges.map((e) => e.getAttribute('data-edge-id')).filter((id) => id.startsWith('t:')),
    liftEdgeIds: edges.map((e) => e.getAttribute('data-edge-id')).filter((id) => id.startsWith('l:')),
    nodeCount: document.querySelectorAll('[data-node-id]').length,
    dimmed: edges.filter((e) => e.classList.contains('is-dimmed'))
      .map((e) => e.getAttribute('data-edge-id')),
    closed: edges.filter((e) => e.classList.contains('is-closed'))
      .map((e) => e.getAttribute('data-edge-id')),
    inspector: document.querySelector('.network-inspector')?.getAttribute('data-inspector') ?? null,
    inspectorText: document.querySelector('.network-inspector')?.innerText ?? '',
  };
});

const openNodeMap = async () => {
  if (await page.locator('.network-map').isVisible().catch(() => false)) return;
  await clickWhenReady('.top-left-stack .site-btn');
  await page.waitForSelector('.network-map', { timeout: 30_000 });
};

try {
  await page.goto(base, { waitUntil: 'load' });
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'load' });
  await page.waitForFunction(() => globalThis.menuMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.trail-slat >> text=New Game');
  await page.waitForFunction(() => globalThis.appMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.site-btn >> text=Select site');
  await page.mouse.move(520, 320); await page.mouse.down();
  await page.mouse.move(820, 620, { steps: 20 }); await page.mouse.up();
  await page.click('.site-btn >> text=View this area');
  await page.fill('.name-entry-input', 'Network Map Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });
  for (let pass = 0; pass < 2; pass++) {
    await page.waitForSelector('.resort-loading', { state: 'attached', timeout: 3_000 }).catch(() => {});
    const loading = page.locator('.resort-loading');
    if (!await loading.isVisible().catch(() => false)) break;
    const enterAnyway = page.getByRole('button', { name: 'Enter anyway' });
    await Promise.race([
      loading.waitFor({ state: 'detached', timeout: 90_000 }),
      enterAnyway.waitFor({ state: 'visible', timeout: 90_000 }),
    ]).catch(() => {});
    if (await enterAnyway.isVisible().catch(() => false)) await enterAnyway.click({ force: true });
    await loading.waitFor({ state: 'detached', timeout: 60_000 }).catch(() => {});
    await page.waitForTimeout(500);
  }
  await page.addStyleTag({
    content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
  });
  if (await page.evaluate(() => globalThis.appMap.getPitch() > 1)) {
    await page.evaluate(() => globalThis.appMap.jumpTo({ pitch: 0 }));
  }

  // --- build a tiny resort: a lift, then two runs that cross ---------------
  await clickWhenReady('.dock-circle-lifts');
  await page.locator('.lift-add-btn >> text=Add ski lift').click();
  await page.mouse.click(700, 660);
  await page.mouse.click(700, 300);
  // New lifts/runs default to Planning; the network only marks an edge `open`
  // once it is Complete, so build them for real.
  await page.locator('.lift-panel .lift-status-btn >> text=Complete').click();
  await page.waitForSelector('.lift-panel .site-actions >> text=Build lift', { timeout: 60_000 });
  await clickWhenReady('.lift-panel button.site-btn-primary');
  await page.waitForTimeout(800);

  await clickWhenReady('.dock-circle-trails');

  // Derive the paint coordinates from where the built lift ACTUALLY projects on
  // screen rather than from the pixels we clicked: the head of a run has to land
  // inside LIFT_SNAP_M (40 m) of the top terminal, and hardcoded pixels drift
  // with any camera change between placing the lift and painting.
  const geometry = await page.evaluate(() => {
    const lift = globalThis.appNetwork.edges.find((e) => e.kind === 'lift');
    const map = globalThis.appMap;
    const base = map.project({ lng: lift.path[0][0], lat: lift.path[0][1] });
    const top = map.project({ lng: lift.path[1][0], lat: lift.path[1][1] });
    const len = Math.hypot(base.x - top.x, base.y - top.y) || 1;
    // Unit vector pointing downhill (top → base) in screen space.
    const down = [(base.x - top.x) / len, (base.y - top.y) / len];
    const metresPerPx =
      lift.lengthM / Math.max(1, Math.hypot(base.x - top.x, base.y - top.y));
    return { top: [top.x, top.y], base: [base.x, base.y], down, len, metresPerPx };
  });

  const { top, down, len, metresPerPx } = geometry;
  // Perpendicular to the lift line, for fanning the two runs apart.
  const side = [-down[1], down[0]];
  const along = (from, d, lateral) => [
    from[0] + down[0] * d + side[0] * lateral,
    from[1] + down[1] * d + side[1] * lateral,
  ];
  // 15 m below the unload: comfortably inside the snap radius, and clear of the
  // thinning inset at the very start of the painted footprint.
  const headOffsetPx = Math.max(3, 15 / metresPerPx);
  const runLen = Math.min(len * 0.85, 320);

  // `served` drops away from the unload; `crosser` cuts across it lower down so
  // both runs must split at a shared junction.
  await paintRun(along(top, headOffsetPx, 0), along(top, headOffsetPx + runLen, runLen * 0.45));
  await paintRun(
    along(top, headOffsetPx + runLen * 0.25, runLen * 0.7),
    along(top, headOffsetPx + runLen * 0.9, -runLen * 0.1)
  );

  // --- 1. the node map draws the split network ----------------------------
  await openNodeMap();
  const drawn = await readMap();
  await page.screenshot({ path: 'verify-network-map.png' });

  if (drawn.liftEdgeIds.length !== 1)
    throw new Error(`Expected exactly 1 lift edge, drew ${drawn.liftEdgeIds.length}.`);
  if (drawn.trailEdgeIds.length < 4)
    throw new Error('Two crossing runs should split into at least 4 segments; drew ' +
      `${drawn.trailEdgeIds.length}: ${drawn.trailEdgeIds.join(', ')}`);
  if (drawn.nodeCount < 3)
    throw new Error(`Expected a junction plus terminals, drew ${drawn.nodeCount} nodes.`);

  // --- 2. clicking the lift reveals what it serves ------------------------
  await page.locator(`[data-edge-id="${drawn.liftEdgeIds[0]}"] .network-hit`).click({ force: true });
  await page.waitForTimeout(200);
  const picked = await readMap();
  if (picked.inspector !== 'lift')
    throw new Error(`Clicking the lift opened the "${picked.inspector}" inspector.`);
  // Labels are uppercased in CSS, so match case-insensitively.
  for (const field of [/ride time/i, /capacity/i, /people waiting/i, /wait time/i]) {
    if (!field.test(picked.inspectorText))
      throw new Error(`Lift inspector is missing ${field}: ${picked.inspectorText}`);
  }
  const servesCount = Number(picked.inspectorText.match(/serves directly\s*\((\d+)\)/i)?.[1] ?? 0);
  if (servesCount < 1)
    throw new Error(`The lift reported serving no runs: ${picked.inspectorText}`);
  if (picked.dimmed.length === 0)
    throw new Error('Selecting a lift dimmed nothing, so its reach is not readable.');

  // --- 3. a segment reports its own stats ---------------------------------
  await page.locator(`[data-edge-id="${drawn.trailEdgeIds[0]}"] .network-hit`).click({ force: true });
  await page.waitForTimeout(200);
  const segment = await readMap();
  if (segment.inspector !== 'trail')
    throw new Error(`Clicking a segment opened the "${segment.inspector}" inspector.`);
  for (const field of [/rating/i, /avg pitch/i, /max pitch/i, /acreage/i, /length/i]) {
    if (!field.test(segment.inspectorText))
      throw new Error(`Segment inspector is missing ${field}: ${segment.inspectorText}`);
  }

  // --- 4. closing a run sticks, and survives a reload ---------------------
  const closedTrailId = drawn.trailEdgeIds[0].split(':')[1];
  await page.locator('.network-inspector .seg-btn >> text=Closed').click();
  await page.waitForTimeout(300);
  const afterClose = await readMap();
  if (afterClose.closed.length === 0)
    throw new Error('Closing the run did not mark any segment closed.');

  await page.locator('.top-left-stack .site-btn').click();
  await page.waitForSelector('.network-map', { state: 'detached', timeout: 20_000 });
  await clickWhenReady('.game-menu-btn');
  await page.locator('.hud-save').click();
  await page.waitForTimeout(2000);
  await page.reload({ waitUntil: 'load' });
  await page.waitForFunction(() => globalThis.menuMap?.isStyleLoaded?.(), null, { timeout: 60_000 });
  await page.click('.trail-slat >> text=Continue Game');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });
  await page.addStyleTag({
    content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
  });
  await openNodeMap();
  const reloaded = await readMap();
  if (!reloaded.closed.some((id) => id.startsWith(`t:${closedTrailId}:`)))
    throw new Error('The closed run reopened after a save + reload — the condition did not persist.');

  console.log(JSON.stringify({ drawn, picked, segment, afterClose, reloaded, errors }, null, 2));
  if (errors.length) process.exitCode = 1;
} catch (error) {
  console.error(error);
  await page.screenshot({ path: 'verify-network-map-failure.png' }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
