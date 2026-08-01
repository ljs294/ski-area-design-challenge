// Browser-level check for the floating Trails roll-up and the free-standing "node"
// feature that lives inside it (TrailsPanel.tsx / MapView.tsx `nodeTool`).
//
// Verifies the things the feature promises:
//
//   1. the Trails button opens a left-aligned bottom-dock roll-up with its
//      three tools and Runs/Nodes/Paths sections;
//   2. a trailhead placement snaps to a graph target, seeds the brush, and
//      becomes the exact first centerline point;
//   3. a first stroke far from every lift top is rejected;
//   4. a node placed on top of a built run reports "Attached to" that run,
//      and shows up in `window.appNetwork` as a `kind === 'user-node'` node;
//   5. that node — and its anchor — survive a save + reload.
//
// Run against a preview server with network access so the normal New Resort
// terrain preparation can complete:
//   node scripts/verifyTrailNodes.mjs http://localhost:4173/
//
// Prefers a real GPU. Under SwiftShader a wide paint can stall the page's
// main thread for tens of seconds, which is why every wait here is generous
// and the buttons are driven through the DOM (see clickWhenReady).
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

/** Arm the paint-run brush from the Trails roll-up, paint one stroke, and
 *  Finish the footprint — stopping at the review panel so the caller can
 *  inspect the anchor before deciding whether to build or cancel. */
async function paintFootprint(from, to, exerciseSeedControls = false, anchorPoint = from) {
  await page.locator('.trails-tool >> text=Create Trail').click();
  await page.waitForSelector('text=Place Trailhead', { timeout: 30_000 });
  await page.mouse.click(from[0], from[1]);
  await page.waitForSelector('.trail-panel >> text=Create Trail', { timeout: 30_000 });
  await page.waitForFunction(() => !document.querySelector('.trail-panel .lift-link-btn')?.disabled);

  if (exerciseSeedControls) {
    await page.locator('.trail-panel .lift-link-btn >> text=Change trailhead').click();
    await page.waitForSelector('text=Place Trailhead');
    await page.mouse.click(from[0], from[1]);
    await page.waitForSelector('.trail-panel >> text=Create Trail');
    await page.waitForFunction(() => !document.querySelector('.trail-panel .lift-link-btn')?.disabled);
    await page.locator('.trail-brush-slider').fill('40');
    await page.waitForFunction(() => !document.querySelector('.trail-panel .lift-link-btn')?.disabled);

    // A user dab can be undone without removing the immutable seed.
    await page.mouse.move(from[0], from[1]);
    await page.mouse.down();
    await page.evaluate(() => window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })));
    await page.waitForFunction(() => !document.querySelector('.trail-panel button.site-btn-primary')?.disabled);
    await page.locator('.trail-panel .site-actions >> text=Undo').click();
    await page.waitForFunction(() => document.querySelector('.trail-panel button.site-btn-primary')?.disabled &&
      !document.querySelector('.trail-panel .lift-link-btn')?.disabled);
  }

  const drawStroke = async () => {
    await page.mouse.move(from[0], from[1]);
    await page.mouse.down();
    await page.mouse.move(to[0], to[1], { steps: 60 });
    await page.evaluate(() => window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })));
    await page.waitForFunction(() => !document.querySelector('.trail-panel button.site-btn-primary')?.disabled);
  };
  await drawStroke();

  if (exerciseSeedControls) {
    await page.locator('.trail-panel .site-actions >> text=Clear').click();
    await page.waitForFunction(() => document.querySelector('.trail-panel button.site-btn-primary')?.disabled &&
      !document.querySelector('.trail-panel .lift-link-btn')?.disabled);
    await drawStroke();

    // Erase directly over the anchor; the worker must repaint the protected seed.
    await page.locator('.trail-paint-modes >> text=Erase').click();
    await page.mouse.move(anchorPoint[0], anchorPoint[1]);
    await page.mouse.down();
    await page.evaluate(() => window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })));
    await page.waitForFunction(() => !document.querySelector('.trail-panel .lift-link-btn')?.disabled);
    const markerVisible = await page.evaluate(() => globalThis.appMap
      .getSource('trail-paint-preview')?._data?.features
      ?.some((feature) => feature.properties?.kind === 'trailhead'));
    if (!markerVisible) throw new Error('Erasing over the anchor removed the trailhead marker.');
    await page.locator('.trail-panel .site-actions >> text=Undo').click();
    await page.waitForFunction(() => !document.querySelector('.trail-panel .lift-link-btn')?.disabled);
  }
  await clickWhenReady('.trail-panel button.site-btn-primary'); // Finish
  await page.waitForSelector('text=Place Trail End', { timeout: 30_000 });
  await page.mouse.click(to[0], to[1]);
  // Skeletonization + terrain sampling run before the review panel appears.
  await page.waitForSelector('.trail-panel .trail-anchor-row', { timeout: 90_000 });
}

/** From the review panel reached by paintFootprint, flip to Complete and
 *  build the run for real (Planning runs never mark their edge `open`). */
async function completeAndBuild() {
  await page.locator('.trail-panel .lift-status-btn >> text=Complete').click();
  await page.waitForSelector('.trail-panel .site-actions >> text=Build run', { timeout: 90_000 });
  await clickWhenReady('.trail-panel button.site-btn-primary'); // Build run
  await page.waitForSelector('.trail-panel', { state: 'detached', timeout: 90_000 }).catch(() => {});
  await page.waitForTimeout(800);
}

try {
  // --- boot: new resort, same sequence as verifyNetworkMap.mjs -------------
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
  await page.fill('.name-entry-input', 'Trail Nodes Resort');
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

  // --- 1. the floating trails roll-up opens with its tools + sections ------
  await clickWhenReady('.dock-circle-trails');
  await page.waitForSelector('.dock-rollup.dock-trails[data-panel="trails"]', { timeout: 30_000 });

  const floatingLayout = await page.evaluate(() => {
    const panel = document.querySelector('.dock-rollup.dock-trails');
    const stack = document.querySelector('.dock-stack');
    const circles = document.querySelector('.dock-circles');
    const panelBox = panel.getBoundingClientRect();
    const stackBox = stack.getBoundingClientRect();
    const circlesBox = circles.getBoundingClientRect();
    return {
      leftAligned: Math.abs(panelBox.left - stackBox.left) < 2,
      aboveCircles: panelBox.bottom <= circlesBox.top + 1,
      boundedHeight: panelBox.height < window.innerHeight,
    };
  });
  if (!floatingLayout.leftAligned || !floatingLayout.aboveCircles || !floatingLayout.boundedHeight)
    throw new Error(`Trails menu is not a bounded, left-aligned floating roll-up: ${JSON.stringify(floatingLayout)}`);

  // Section headers and tool labels are uppercased in CSS, so innerText comes
  // back as "TOOLS" / "RUNS (0)". Compare case-insensitively.
  const norm = (s) => s.toLowerCase().replace(/\s+/g, ' ').trim();
  const toolLabels = (await page.locator('.trails-tool').allInnerTexts()).map(norm);
  for (const label of ['Create Trail', 'Place node', 'Draw path']) {
    if (!toolLabels.some((t) => t.includes(norm(label))))
      throw new Error(`Trails tool "${label}" missing from the roll-up: ${JSON.stringify(toolLabels)}`);
  }
  const sectionTitles = (await page.locator('.layer-section-title').allInnerTexts()).map(norm);
  for (const label of ['Tools', 'Runs (0)', 'Nodes (0)', 'Paths (0)']) {
    if (!sectionTitles.includes(norm(label)))
      throw new Error(`Section header "${label}" missing: ${JSON.stringify(sectionTitles)}`);
  }

  // --- 2. build a lift (same sequence as verifyNetworkMap.mjs) -------------
  await clickWhenReady('.dock-circle-lifts');
  await page.locator('.lift-add-btn >> text=Add ski lift').click();
  await page.mouse.click(700, 660);
  await page.mouse.click(700, 300);
  await page.locator('.lift-panel .lift-status-btn >> text=Complete').click();
  await page.waitForSelector('.lift-panel .site-actions >> text=Build lift', { timeout: 60_000 });
  await clickWhenReady('.lift-panel button.site-btn-primary');
  await page.waitForTimeout(800);

  await clickWhenReady('.dock-circle-trails');

  // Derive paint coordinates from where the built lift ACTUALLY projects on
  // screen, exactly as verifyNetworkMap.mjs does: the run's head has to land
  // inside the 60 m lift-top snap radius, and hardcoded
  // pixels drift with any camera change between placing the lift and now.
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
  const { top, base: liftBase, down, len, metresPerPx } = geometry;
  const side = [-down[1], down[0]]; // perpendicular to the lift line
  const along = (from, d, lateral) => [
    from[0] + down[0] * d + side[0] * lateral,
    from[1] + down[1] * d + side[1] * lateral,
  ];

  // --- 3. the first stroke snaps to the unload ------------------------------
  // Begin 15 m below the unload: comfortably inside the 60 m snap radius. The
  // first click is released, then painting resumes to verify pickup/continue.
  const headOffsetPx = Math.max(3, 15 / metresPerPx);
  const runLen = Math.min(len * 0.6, 200);
  await paintFootprint(along(top, headOffsetPx, 0), liftBase, true, top);

  const anchorSet = await page.locator('.trail-anchor-row[data-anchor="set"]')
    .isVisible().catch(() => false);
  if (!anchorSet)
    throw new Error('Run started near the lift unload did not retain its lift-top anchor.');
  const buildDisabledAtAnchor = await page.locator('.trail-panel button.site-btn-primary').isDisabled();
  if (buildDisabledAtAnchor)
    throw new Error('Build stayed disabled even though the anchor row read "set".');
  const exactHead = await page.evaluate(() => {
    const lift = globalThis.appNetwork.edges.find((e) => e.kind === 'lift');
    const spine = globalThis.appMap.getSource('trail-draft')?._data?.features
      ?.find((feature) => feature.properties?.kind === 'spine')?.geometry?.coordinates;
    return !!spine?.length && Math.abs(spine[0][0] - lift.path.at(-1)[0]) < 1e-12 &&
      Math.abs(spine[0][1] - lift.path.at(-1)[1]) < 1e-12;
  });
  if (!exactHead) throw new Error('Trail centerline station 0 is not the exact lift-top coordinate.');

  // Layers remains available beside an active Trails tool without cancelling it.
  await clickWhenReady('.dock-circle-layers');
  await page.waitForSelector('.dock-rollup.dock-layers');
  if (!(await page.locator('.dock-rollup.dock-trails .trail-panel').isVisible()))
    throw new Error('Opening Layers cancelled or hid the active Trails tool.');
  await clickWhenReady('.dock-circle-layers');
  await page.waitForSelector('.dock-rollup.dock-layers', { state: 'detached' });

  await completeAndBuild();

  // --- 4. a first stroke far from every lift top is rejected ----------------
  // Aim for ~700 m off to the side of the lift/run cluster — comfortably past
  // the 60 m lift-top snap radius — then clamp to the visible canvas so the
  // stroke still lands on prepared terrain.
  const farOffsetPx = 700 / metresPerPx;
  const clampX = (x) => Math.min(1320, Math.max(80, x));
  const clampY = (y) => Math.min(820, Math.max(80, y));
  const farBase = [clampX(top[0] - side[0] * farOffsetPx), clampY(top[1] - side[1] * farOffsetPx)];
  await page.locator('.trails-tool >> text=Create Trail').click();
  await page.mouse.click(farBase[0], farBase[1]);
  await page.waitForSelector('text=Choose a lift terminal or an existing trail centerline.');
  if (!(await page.getByText('Place Trailhead', { exact: true }).isVisible()))
    throw new Error('An invalid trailhead click unexpectedly opened the brush.');

  await page.locator('.trail-panel .settings-close-x').click();
  await page.waitForSelector('.trail-panel', { state: 'detached', timeout: 30_000 }).catch(() => {});
  await page.waitForTimeout(300);

  // --- 5. place a node on the built run -------------------------------------
  const nodeTargetPx = await page.evaluate(() => {
    const edge = globalThis.appNetwork.edges.find((e) => e.kind === 'trail');
    const pt = edge.path[Math.floor(edge.path.length / 2)];
    const p = globalThis.appMap.project({ lng: pt[0], lat: pt[1] });
    return [p.x, p.y];
  });

  await page.locator('.trails-tool >> text=Place node').click();
  await page.waitForSelector('.trail-panel .site-hint', { timeout: 30_000 });
  await page.mouse.click(nodeTargetPx[0], nodeTargetPx[1]);
  await page.waitForSelector('.trail-panel .name-entry-input', { timeout: 30_000 });

  const attachedText = (await page.locator('.trail-panel .readout-line')
    .filter({ hasText: 'Attached to' }).locator('.lift-stat-value').innerText()).trim();
  if (attachedText === 'Nothing' || attachedText === '')
    throw new Error(`Node dropped on the run's centerline reported no attachment: "${attachedText}"`);

  await clickWhenReady('.trail-panel .site-actions button.site-btn-primary'); // Place node
  await page.waitForSelector('.trail-panel', { state: 'detached', timeout: 30_000 }).catch(() => {});
  await page.waitForTimeout(500);

  const nodeCheck = await page.evaluate(() => {
    const net = globalThis.appNetwork;
    const userNode = net.nodes.find((n) => n.kind === 'user-node');
    return {
      hasUserNode: !!userNode,
      savedNodeId: userNode?.nodeIds?.[0] ?? null,
      nodeCount: net.nodes.length,
    };
  });
  if (!nodeCheck.hasUserNode || !nodeCheck.savedNodeId)
    throw new Error(`No kind==='user-node' node found in window.appNetwork after placing one: ${JSON.stringify(nodeCheck)}`);

  // --- 6. log diagnostics ---------------------------------------------------
  const diagnostics = await page.evaluate(() => globalThis.appNetwork.diagnostics);
  console.log('Network diagnostics:', JSON.stringify(diagnostics, null, 2));

  // --- 7. save, reload, continue — the node and its anchor persist ---------
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

  await clickWhenReady('.dock-circle-trails');
  await page.waitForSelector('.dock-rollup.dock-trails[data-panel="trails"]', { timeout: 30_000 });
  await page.waitForSelector(`[data-row-id="${nodeCheck.savedNodeId}"]`, { timeout: 30_000 });

  const reloadedNode = await page.evaluate(() => {
    const net = globalThis.appNetwork;
    const userNode = net.nodes.find((n) => n.kind === 'user-node');
    return { hasUserNode: !!userNode, diagnostics: net.diagnostics };
  });
  if (!reloadedNode.hasUserNode)
    throw new Error('The placed node did not survive a save + reload (window.appNetwork has no user-node).');

  // --- 8. screenshot + error gate -------------------------------------------
  await page.screenshot({ path: 'verify-trail-nodes.png' });
  console.log(JSON.stringify({ nodeCheck, diagnostics, reloadedNode, errors }, null, 2));
  if (errors.length) process.exitCode = 1;
} catch (error) {
  console.error(error);
  await page.screenshot({ path: 'verify-trail-nodes-failure.png' }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
