// Browser smoke check for Stage 6 of the snowmaking pipe-network feature: the
// auto-created intake node, the dock panel's node UI, the Layers-dock
// visibility toggle, and the "Mountain Dashboards" overlay's Snowmaking tab.
//
// Setup (through "build a standalone pond") mirrors verifyPondBuilder.mjs
// verbatim — see that script for the earthwork-bill assertions, which are
// NOT repeated here. This script only covers what stage 6 added on top of
// that: the node.
//
// Run against a preview server with network access so terrain preparation
// can complete:
//   node scripts/verifySnowmakingNodes.mjs http://localhost:4173/ scratchpad/verify-snowmaking-nodes.png
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const shot = process.argv[3] ?? 'scratchpad/verify-snowmaking-nodes.png';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(String(error)));

// This repo's documented Playwright gotcha: clicking something that can
// trigger a re-render/teardown sometimes wedges a normal page.click(). Prefer
// an evaluate-click for list rows and other elements that swap out on click.
const evalClick = async (locator) => locator.evaluate((element) => element.click());

try {
  // --- setup: identical to verifyPondBuilder.mjs, through a built pond -----
  await page.goto(base, { waitUntil: 'load' });
  await page.evaluate(() => localStorage.clear());
  await page.reload({ waitUntil: 'load' });
  await page.waitForFunction(() => globalThis.menuMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.trail-slat >> text=New Game');
  await page.waitForFunction(() => globalThis.appMap?.isStyleLoaded?.(), null, { timeout: 30_000 });
  await page.click('.site-btn >> text=Select site');
  await page.mouse.move(500, 300); await page.mouse.down();
  await page.mouse.move(820, 640, { steps: 20 }); await page.mouse.up();
  await page.click('.site-btn >> text=View this area');
  await page.fill('.name-entry-input', 'Snowmaking Node Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 420_000 });
  const loading = page.locator('.resort-loading');
  if (await loading.isVisible().catch(() => false)) {
    const enterAnyway = page.getByRole('button', { name: 'Enter anyway' });
    await Promise.race([
      loading.waitFor({ state: 'detached', timeout: 90_000 }),
      enterAnyway.waitFor({ state: 'visible', timeout: 90_000 }),
    ]).catch(() => {});
    if (await enterAnyway.isVisible().catch(() => false)) await enterAnyway.click({ force: true });
    await loading.waitFor({ state: 'detached', timeout: 60_000 }).catch(() => {});
  }
  await page.addStyleTag({
    content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
  });

  await page.evaluate(() => new Promise((resolve) => {
    const map = globalThis.appMap;
    map.easeTo({ zoom: 17.5, pitch: 0, bearing: 0, duration: 0 });
    map.once('idle', resolve);
    setTimeout(resolve, 8000);
  }));
  await page.click('.dock-circle-snowmaking');
  await page.waitForSelector('.dock-snowmaking');
  await page.click('.snowmaking-panel >> text=Build standalone pond');
  for (const [x, y] of [[590, 500], [650, 500], [650, 550], [590, 550]]) await page.mouse.click(x, y);
  await page.click('.snowmaking-panel >> text=Finish boundary');
  await page.waitForSelector('.snowmaking-panel >> text=Review standalone pond');

  const beforeDem = await page.evaluate(() => globalThis.appMap.getStyle().sources.dem?.tiles?.[0]);
  const pondName = 'Snowmaking Pond';
  await page.fill('.snowmaking-panel .name-entry-input', pondName);
  await page.click('.snowmaking-panel >> text=Build pond');
  await page.waitForSelector(`.snowmaking-panel >> text=${pondName}`, { timeout: 60_000 });
  await page.waitForFunction((url) => globalThis.appMap
    .getStyle().sources.dem?.tiles?.[0] !== url, beforeDem, { timeout: 60_000 });
  await page.waitForFunction(() => (globalThis.appMap.getStyle()
    .sources['player-standalone-ponds']?.data?.features?.length ?? 0) > 0,
  null, { timeout: 30_000 });

  const results = {};

  // Row lookups match the EXACT `.lift-row-name`, not the whole row's text —
  // a node's `.lift-row-summary` reads "Intake · <pond name>", so a plain
  // hasText(pondName) filter on the row would also match the node's own row
  // once the pond's name appears there too. Anchor to the name span instead.
  const escapeRegex = (s) => s.replace(/[.*+?^${}()|[\]\\]/g, '\\$&');
  const rowByExactName = (name) => page.locator('.snowmaking-panel .lift-row.lift-row-button')
    .filter({ has: page.locator('.lift-row-name', { hasText: new RegExp(`^${escapeRegex(name)}$`) }) });

  // --- 1. the intake node exists on the map ---------------------------------
  const readNodeFeatures = () => page.evaluate(() => globalThis.appMap
    .getStyle().sources['snowmaking-network']?.data?.features ?? []);
  await page.waitForFunction(() => (globalThis.appMap.getStyle()
    .sources['snowmaking-network']?.data?.features?.length ?? 0) === 1, null, { timeout: 30_000 });
  let features = await readNodeFeatures();
  if (features.length !== 1)
    throw new Error(`Expected exactly 1 intake node feature, found ${features.length}: ${JSON.stringify(features)}`);
  const intakeName = features[0].properties?.name;
  if (features[0].properties?.kind !== 'intake' || !intakeName?.endsWith(' Intake'))
    throw new Error(`Auto-created node is not a well-formed intake: ${JSON.stringify(features[0].properties)}`);
  results.autoNode = { name: intakeName, kind: features[0].properties?.kind };

  // --- 2. the node row appears in the dock panel overview -------------------
  // confirmPond() never sets selectedPondId, so the panel is ALREADY back on
  // the overview branch (SnowmakingControl.tsx's final `lift-overview` return)
  // the instant a pond finishes building — it is not left on the pond's own
  // detail. The overview root carries the `lift-overview` class; any other
  // branch (pond/dam/node detail) does not. Its `.settings-close-x` is wired
  // to two DIFFERENT handlers depending on branch: on a detail branch it goes
  // "back" to the overview (onClosePond/onCloseDam/onCloseNode), but on the
  // overview itself that same-looking button closes the WHOLE dock
  // (onClose={() => setOpenDock(null)}) — so only click it when a detail
  // branch is actually showing, never when already on the overview.
  const isOnOverview = () => page.locator('.snowmaking-panel.lift-overview').count()
    .then((n) => n > 0);
  const backToOverview = async () => {
    if (await isOnOverview()) return;
    const closeX = page.locator('.snowmaking-panel .settings-close-x');
    if (await closeX.isVisible().catch(() => false)) await evalClick(closeX);
    await page.waitForTimeout(300);
    if (!(await isOnOverview()))
      throw new Error('Closing the detail branch did not return to the snowmaking overview.');
  };
  await backToOverview();
  const overviewText = await page.locator('.snowmaking-panel').innerText();
  if (!overviewText.includes(intakeName))
    throw new Error(`Dock overview does not list the intake node "${intakeName}": ${overviewText}`);
  const nodeRow = rowByExactName(intakeName);
  if (await nodeRow.count() !== 1)
    throw new Error(`Expected exactly one node row for "${intakeName}", found ${await nodeRow.count()}.`);
  const nodeRowSummary = await nodeRow.locator('.lift-row-summary').innerText();
  if (!nodeRowSummary.includes('Intake') || !nodeRowSummary.includes(pondName))
    throw new Error(`Node row summary missing "Intake" and/or pond name: "${nodeRowSummary}"`);
  results.nodeRowSummary = nodeRowSummary;

  // --- 3. click the node row, assert detail branch, rename it ---------------
  await evalClick(nodeRow);
  await page.waitForSelector('.snowmaking-panel .lift-stats', { timeout: 10_000 });
  const detailBefore = await page.evaluate(() => {
    const panel = document.querySelector('.snowmaking-panel');
    const value = (label) => [...panel.querySelectorAll('.readout-line')]
      .find((row) => row.textContent?.startsWith(label))?.textContent ?? null;
    return { kind: value('Kind'), source: value('Source'), elevation: value('Elevation'),
      hasNameInput: !!panel.querySelector('.name-entry-input.lift-name-input'),
      hasDeleteBtn: !!panel.querySelector('.lift-delete-btn') };
  });
  if (!detailBefore.kind?.includes('Intake'))
    throw new Error(`Node detail Kind row wrong: ${JSON.stringify(detailBefore)}`);
  if (!detailBefore.source?.includes(pondName))
    throw new Error(`Node detail Source row wrong: ${JSON.stringify(detailBefore)}`);
  if (!detailBefore.elevation)
    throw new Error(`Node detail Elevation row missing: ${JSON.stringify(detailBefore)}`);
  if (!detailBefore.hasNameInput)
    throw new Error('Node detail is missing the editable name input.');
  if (detailBefore.hasDeleteBtn)
    throw new Error('Node detail unexpectedly has a delete/remove button (should be absent).');
  results.nodeDetailBefore = detailBefore;

  const newName = 'Top Gun Intake';
  const nameInput = page.locator('.snowmaking-panel .name-entry-input.lift-name-input');
  await nameInput.fill(newName);
  await page.waitForTimeout(300);
  const inputValue = await nameInput.inputValue();
  if (inputValue !== newName)
    throw new Error(`Name input did not accept the rename: "${inputValue}"`);
  await page.waitForFunction((expected) => globalThis.appMap.getStyle()
    .sources['snowmaking-network']?.data?.features?.[0]?.properties?.name === expected,
  newName, { timeout: 10_000 });
  const renamedFeature = (await readNodeFeatures())[0];
  if (renamedFeature?.properties?.name !== newName)
    throw new Error(`Rename did not propagate to the map source: ${JSON.stringify(renamedFeature?.properties)}`);
  results.renamedTo = renamedFeature.properties.name;

  // --- 4. untick "Snowmaking pond", assert node disappears ------------------
  await backToOverview(); // closes node detail -> overview (selectedPond was never set)
  const pondRow = rowByExactName(pondName);
  if (await pondRow.count() !== 1)
    throw new Error(`Expected exactly one pond row for "${pondName}", found ${await pondRow.count()}.`);
  await evalClick(pondRow);
  const snowmakingCheckbox = page.locator('.snowmaking-panel input[aria-label="Snowmaking pond"]');
  await snowmakingCheckbox.waitFor({ state: 'visible', timeout: 10_000 });
  if (!(await snowmakingCheckbox.isChecked()))
    throw new Error('Snowmaking pond checkbox was not checked by default.');
  await snowmakingCheckbox.click();
  await page.waitForFunction(() => (globalThis.appMap.getStyle()
    .sources['snowmaking-network']?.data?.features?.length ?? 0) === 0, null, { timeout: 10_000 });
  features = await readNodeFeatures();
  if (features.length !== 0)
    throw new Error(`Unchecking Snowmaking pond did not remove the intake node: ${JSON.stringify(features)}`);
  results.afterUncheck = { featureCount: features.length };

  // --- 5. re-tick it, assert node returns ------------------------------------
  await snowmakingCheckbox.click();
  await page.waitForFunction(() => (globalThis.appMap.getStyle()
    .sources['snowmaking-network']?.data?.features?.length ?? 0) === 1, null, { timeout: 10_000 });
  features = await readNodeFeatures();
  if (features.length !== 1 || features[0].properties?.kind !== 'intake' ||
    !features[0].properties?.name?.endsWith(' Intake'))
    throw new Error(`Re-checking Snowmaking pond did not recreate a well-formed intake node: ${JSON.stringify(features)}`);
  results.afterRecheck = { featureCount: features.length, name: features[0].properties?.name };

  // --- 6. toggle "Snowmaking network" in the Layers dock ---------------------
  await backToOverview();
  await page.click('.dock-circle-layers');
  await page.waitForSelector('.dock-rollup.dock-layers', { timeout: 10_000 });
  const layerRow = page.locator('.layer-row').filter({ hasText: 'Snowmaking network' });
  if (await layerRow.count() !== 1)
    throw new Error(`Expected exactly one "Snowmaking network" layer row, found ${await layerRow.count()}.`);
  const readVisibility = () => page.evaluate(() => {
    const v = globalThis.appMap.getLayoutProperty('snowmaking-nodes', 'visibility');
    return v === undefined ? 'visible' : v;
  });
  const visBefore = await readVisibility();
  if (visBefore !== 'visible')
    throw new Error(`Snowmaking layers were not visible by default: ${visBefore}`);
  const layerCheckbox = layerRow.locator('input[type="checkbox"]');
  if (!(await layerCheckbox.isChecked()))
    throw new Error('Snowmaking network layer-row checkbox was not checked by default.');
  await layerCheckbox.click();
  await page.waitForFunction(() => globalThis.appMap
    .getLayoutProperty('snowmaking-nodes', 'visibility') === 'none', null, { timeout: 10_000 });
  const visOff = await readVisibility();
  await layerCheckbox.click();
  await page.waitForFunction(() => {
    const v = globalThis.appMap.getLayoutProperty('snowmaking-nodes', 'visibility');
    return v === undefined || v === 'visible';
  }, null, { timeout: 10_000 });
  const visOn = await readVisibility();
  results.layerToggle = { visBefore, visOff, visOn };
  if (visOff !== 'none' || visOn !== 'visible')
    throw new Error(`Layers-dock toggle did not flip visibility as expected: ${JSON.stringify(results.layerToggle)}`);
  await page.click('.dock-circle-layers');
  await page.waitForSelector('.dock-rollup.dock-layers', { state: 'detached', timeout: 10_000 }).catch(() => {});

  // --- 7. Mountain Dashboards -> Snowmaking tab -------------------------------
  // .mountain-dashboards itself has no CSS rule (confirmed: no match in
  // app.css) and both its children (.dashboard-picker, .network-map) are
  // `position: fixed`, taking them out of flow — so the wrapper collapses to
  // a zero-size box. It renders correctly on screen (its fixed children paint
  // fine), but Playwright's strict `visible` check on that specific
  // zero-height wrapper element never resolves. Wait on the always-sized,
  // fixed-position picker instead of the collapsed wrapper.
  await page.click('.top-left-stack .site-btn >> text=Mountain Dashboards');
  await page.waitForSelector('.mountain-dashboards', { state: 'attached', timeout: 30_000 });
  await page.waitForSelector('.dashboard-picker', { state: 'visible', timeout: 30_000 });
  await page.locator('.dashboard-picker').getByRole('button', { name: 'Snowmaking', exact: true }).click();
  await page.waitForSelector('.network-map[aria-label="Snowmaking network map"]', { state: 'visible', timeout: 60_000 });

  const dashboardNodeCount = await page.locator('.snowmaking-dashboard-node').count();
  if (dashboardNodeCount !== 1)
    throw new Error(`Expected exactly 1 dashboard node, found ${dashboardNodeCount}.`);
  const inspectorStateInitial = await page.locator('.network-inspector').getAttribute('data-inspector');
  if (inspectorStateInitial !== 'summary')
    throw new Error(`Dashboard inspector did not start in summary state: "${inspectorStateInitial}"`);
  const nodesStatValue = await page.evaluate(() => {
    const stat = [...document.querySelectorAll('.network-inspector .network-stat')]
      .find((el) => el.querySelector('.network-stat-label')?.textContent === 'Nodes');
    return stat?.querySelector('.network-stat-value')?.textContent ?? null;
  });
  if (nodesStatValue !== '1')
    throw new Error(`Dashboard summary did not report Nodes -> 1 (got "${nodesStatValue}").`);

  // Click the node's <g> in the SVG. A real mouse click fails here: the <g>'s
  // bounding box spans both the circle AND its text label floating above it,
  // so the bbox center (where Playwright's hit-tested click lands) is empty
  // space between the two, which the background grid <rect> intercepts.
  // Dispatch the click event directly instead, bypassing hit-testing — the
  // handler only needs a real 'click' event to fire, and its e.stopPropagation
  // still runs, so the svg's own onClick (which would deselect) never sees it.
  const dashboardNode = page.locator('.snowmaking-dashboard-node').first();
  await dashboardNode.evaluate((el) =>
    el.dispatchEvent(new MouseEvent('click', { bubbles: true, cancelable: true, view: window })));
  await page.waitForFunction(() => document.querySelector('.network-inspector')
    ?.getAttribute('data-inspector') === 'node', null, { timeout: 10_000 });
  const inspectorStateNode = await page.locator('.network-inspector').getAttribute('data-inspector');
  const inspectorText = await page.locator('.network-inspector').innerText();
  // NOT newName ("Top Gun Intake") — steps 4-5 unchecked/rechecked the pond's
  // "Snowmaking pond" box, which by design discards that rename and recreates
  // a fresh node under the default "<pond name> Intake" name (asserted back
  // in results.afterRecheck.name). That current name is what should appear
  // here, not the since-superseded rename from step 3.
  const currentNodeName = results.afterRecheck.name;
  if (!inspectorText.includes(currentNodeName))
    throw new Error(`Dashboard node inspector did not show the current node "${currentNodeName}": ${inspectorText}`);
  if (!inspectorText.includes(pondName))
    throw new Error(`Dashboard node inspector did not show the Source pond "${pondName}": ${inspectorText}`);
  results.dashboard = { dashboardNodeCount, inspectorStateInitial, inspectorStateNode, inspectorText };

  await page.screenshot({ path: shot });

  // --- 8. close the dashboard, confirm normal game view returns -------------
  await page.click('.network-chrome-tl >> text=Close snowmaking map');
  await page.waitForSelector('.mountain-dashboards', { state: 'detached', timeout: 15_000 });
  const dockVisible = await page.locator('.dock-circle-snowmaking').isVisible();
  if (!dockVisible)
    throw new Error('Dock is not visible after closing the Mountain Dashboards overlay.');
  results.closedBackToGameView = true;

  // --- 9. page-error gate -----------------------------------------------------
  console.log(JSON.stringify({ results, pageErrors }, null, 2));
  if (pageErrors.length) throw new Error(`Page errors: ${pageErrors.join(' | ')}`);
} catch (error) {
  console.error(error);
  await page.screenshot({ path: shot }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
