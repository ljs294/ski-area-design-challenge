// Browser smoke check for the dam workflow: dam a stream, read the embankment
// and its earthwork bill, confirm the ground-change preview is on the map, then
// build it and confirm the terrain package was regraded into a berm. Run
// against a preview server with network access so terrain preparation can
// complete:
// node scripts/verifyDamBuilder.mjs http://localhost:4173/ scratchpad/dam.png
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const shot = process.argv[3] ?? 'scratchpad/dam.png';
const browser = await chromium.launch({ args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] });
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const pageErrors = [];
page.on('pageerror', (error) => pageErrors.push(String(error)));

const gradedContourCount = () => page.evaluate(() => globalThis.appMap
  .getStyle().sources['graded-contours']?.data?.features?.length ?? 0);

try {
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
  await page.fill('.name-entry-input', 'Dam Builder Resort');
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

  const frame = async (path, camera) => {
    await page.evaluate((view) => new Promise((resolve) => {
      const map = globalThis.appMap;
      map.easeTo({ ...view, duration: 0 });
      map.once('idle', resolve);
      setTimeout(resolve, 8000);
    }), camera);
    await page.waitForTimeout(1500);
    if (path) await page.screenshot({ path });
  };
  await frame(null, { zoom: 15.5, pitch: 0, bearing: 0 });

  // Frame the longest stream in the box, so the bank clicks below always have
  // somewhere to land regardless of where the site drag put the camera. Local
  // context is drawn over the whole perimeter ring, so streams have to be
  // clipped to the play box first — the dam tool refuses a bank outside it.
  const streams = await page.evaluate(() => {
    const box = globalThis.appTerrainBounds;
    if (!box) return { count: 0, kinds: ['no appTerrainBounds'] };
    const inBox = ([lng, lat]) => lng >= box.west && lng <= box.east &&
      lat >= box.south && lat <= box.north;
    const data = globalThis.appMap.getStyle().sources['local-context']?.data;
    const features = data?.features ?? [];
    const lines = features.filter((feature) =>
      feature.properties?.kind === 'water-line' && feature.geometry?.type === 'LineString' &&
      feature.geometry.coordinates.some(inBox));
    if (!lines.length) return { count: 0, kinds: [...new Set(features
      .map((feature) => feature.properties?.kind))] };
    const inside = (line) => line.geometry.coordinates.filter(inBox);
    const longest = lines.reduce((best, line) =>
      inside(line).length > inside(best).length ? line : best);
    const coords = inside(longest);
    return { count: lines.length, center: coords[Math.floor(coords.length / 2)] };
  });
  if (!streams.count)
    throw new Error(`No stream in the play box to dam. Local context holds: ${JSON.stringify(streams.kinds)}`);
  // Zoomed out enough that a decent stretch of the creek is on screen; the
  // survey below can only offer vertices it can project into the viewport.
  await frame(null, { center: streams.center, zoom: 14.5, pitch: 0, bearing: 0 });

  // Every stream vertex on screen, with a perpendicular pair of bank clicks
  // measured in real metres. The second endpoint snaps to the first one's
  // contour, so which pair produces a contained basin is terrain-dependent —
  // try them in order.
  const survey = await page.evaluate(() => {
    const map = globalThis.appMap;
    const box = globalThis.appTerrainBounds;
    const inBox = ([lng, lat]) => lng >= box.west && lng <= box.east &&
      lat >= box.south && lat <= box.north;
    const data = map.getStyle().sources['local-context']?.data;
    const lines = (data?.features ?? []).filter((feature) =>
      feature.properties?.kind === 'water-line' && feature.geometry?.type === 'LineString');
    const out = [];
    for (const line of lines) {
      const coords = line.geometry.coordinates;
      for (let i = 1; i < coords.length - 1; i++) {
        const [lng, lat] = coords[i];
        if (!inBox(coords[i])) continue;
        const metersPerLng = 111320 * Math.cos(lat * Math.PI / 180);
        const dx = (coords[i + 1][0] - coords[i - 1][0]) * metersPerLng;
        const dy = (coords[i + 1][1] - coords[i - 1][1]) * 111320;
        const span = Math.hypot(dx, dy);
        if (span < 5) continue;
        const nx = -dy / span, ny = dx / span;
        const bank = (reach) => [lng + nx * reach / metersPerLng, lat + ny * reach / 111320];
        const onScreen = (p) => p.x > 60 && p.y > 60 && p.x < 1140 && p.y < 840;
        // Tight pairs first: on a creek in a ravine a 25 m offset is already
        // tens of metres up the wall, which snaps to nothing and prices a dam
        // taller than anything a resort builds.
        for (const [firstM, secondM] of [[8, -12], [10, -16], [12, -20], [15, -25],
          [-8, 12], [-10, 16], [-12, 20], [-15, 25], [20, -32], [25, -45], [-25, 45]]) {
          // Both banks have to be inside the play box, not just on screen: the
          // perimeter ring is visible but the dam tool will not build on it.
          const from = bank(firstM), to = bank(secondM);
          if (!inBox(from) || !inBox(to)) continue;
          const a = map.project(from), b = map.project(to);
          if (!onScreen(a) || !onScreen(b)) continue;
          out.push({ stream: line.properties?.name ?? line.properties?.id ?? 'unnamed',
            a: [a.x, a.y], b: [b.x, b.y] });
        }
      }
    }
    return { streams: lines.length, candidates: out };
  });
  if (!survey.candidates.length)
    throw new Error(`No dammable stream on screen (${survey.streams} water lines in the box).`);

  await page.click('.dock-circle-infrastructure');
  await page.waitForSelector('.dock-infrastructure');
  const review = page.locator('.infrastructure-panel >> text=Review snowmaking pond');
  const attempts = [];
  let used = null;
  const warningText = page.locator('.infrastructure-panel .lift-warning').first();
  const armDam = page.locator('.infrastructure-panel .lift-add-btn', { hasText: 'Build dam' });
  for (const candidate of survey.candidates.slice(0, 60)) {
    if (!await armDam.isVisible().catch(() => false)) {
      await page.click('.dock-circle-infrastructure');
      await page.waitForSelector('.dock-infrastructure');
    }
    await armDam.click();
    await page.waitForSelector('.infrastructure-panel >> text=New dam');
    await page.mouse.click(...candidate.a);
    await page.mouse.move(...candidate.b);
    await page.waitForTimeout(200);
    await page.mouse.click(...candidate.b);
    // The analysis runs in a worker, so the panel settles on the review or on a
    // warning some time after the click. `isVisible` does not wait, so sampling
    // it here reads the still-analysing panel and scores every alignment a miss.
    const settled = await Promise.race([
      review.waitFor({ state: 'visible', timeout: 25_000 }).then(() => 'review', () => null),
      warningText.waitFor({ state: 'visible', timeout: 25_000 }).then(() => 'warning', () => null),
    ]);
    if (settled === 'review') { used = candidate; break; }
    const warning = settled === 'warning'
      ? await warningText.textContent().catch(() => 'unreadable warning')
      : 'no verdict in 25s';
    attempts.push(warning);
    console.error(`  ${attempts.length}. ${candidate.stream}: ${warning}`);
    // Escape, not the Cancel button: a click has to wait out scheduled
    // navigation, and a re-analysing panel can hold that open past the timeout.
    await page.keyboard.press('Escape');
    await page.waitForSelector('.infrastructure-panel >> text=New dam',
      { state: 'detached', timeout: 10_000 }).catch(() => {});
  }
  if (!used) {
    const tally = attempts.reduce((counts, reason) =>
      ({ ...counts, [reason]: (counts[reason] ?? 0) + 1 }), {});
    console.error(JSON.stringify(tally, null, 2));
    throw new Error(`No alignment of ${survey.candidates.length} candidates produced a pond.`);
  }

  const readReview = () => page.evaluate(() => {
    const panel = document.querySelector('.infrastructure-panel');
    const value = (label) => [...panel.querySelectorAll('.readout-line')]
      .find((row) => row.textContent?.startsWith(label))?.textContent ?? null;
    return { cut: value('Cut'), fill: value('Fill'), balance: value('Balance'),
      damCrest: value('Dam crest elevation'), fullPool: value('Full pool elevation'),
      embankment: value('Embankment length'), maxHeight: value('Maximum height'),
      volume: value('Pond volume'), hint: panel.textContent.includes('Highlighted contours') };
  });
  const designed = await readReview();
  if (!designed.cut || !designed.fill || !designed.balance)
    throw new Error('Dam review did not report cut, fill, and balance.');
  if (!designed.damCrest || !designed.embankment)
    throw new Error('Dam review did not report the embankment holding the pool back.');

  // The pre-build ground preview: the graded contour layer must be carrying the
  // lines this dam would move, and the draft must be drawing the earthwork.
  const previewContours = await gradedContourCount();
  if (previewContours === 0)
    throw new Error('The dam review did not highlight the contours it would move.');
  const draftKinds = await page.evaluate(() => (globalThis.appMap.getStyle()
    .sources['dam-draft']?.data?.features ?? []).map((feature) => feature.properties?.kind));
  if (!draftKinds.includes('embankment') || !draftKinds.includes('crest'))
    throw new Error(`The dam draft is missing its embankment: ${JSON.stringify(draftKinds)}`);

  const beforeDem = await page.evaluate(() => globalThis.appMap.getStyle().sources.dem?.tiles?.[0]);
  await page.fill('.infrastructure-panel .name-entry-input', 'Verified Dam');
  await page.click('.infrastructure-panel >> text=Build dam');
  await page.waitForSelector('.infrastructure-panel >> text=Verified Dam', { timeout: 60_000 });
  await page.waitForFunction((url) => globalThis.appMap
    .getStyle().sources.dem?.tiles?.[0] !== url, beforeDem, { timeout: 60_000 });

  const built = await page.evaluate(() => {
    const data = globalThis.appMap.getStyle().sources['player-dams']?.data;
    const features = data?.features ?? [];
    const embankment = features.find((feature) => feature.properties?.kind === 'embankment');
    const pond = features.find((feature) => feature.properties?.kind === 'pond');
    const ring = pond?.geometry?.coordinates?.[0] ?? [];
    return {
      kinds: features.map((feature) => feature.properties?.kind),
      embankmentRing: embankment?.geometry?.coordinates?.[0]?.length ?? 0,
      center: ring.length
        ? [ring.reduce((sum, point) => sum + point[0], 0) / ring.length,
          ring.reduce((sum, point) => sum + point[1], 0) / ring.length] : null,
    };
  });
  if (!built.kinds.includes('embankment') || !built.kinds.includes('crest'))
    throw new Error(`The built dam is not drawn as an embankment: ${JSON.stringify(built.kinds)}`);
  if (!built.center) throw new Error('The impounded pond is not on the map.');

  // Committing turns the proposal into terrain, so the highlight must clear.
  const afterContours = await gradedContourCount();
  if (afterContours !== 0)
    throw new Error(`The grade highlight survived the build (${afterContours} features).`);

  const damCamera = { center: built.center, zoom: 16.5, pitch: 0, bearing: 0 };
  built.rendered = 0;
  for (let attempt = 0; attempt < 4 && built.rendered === 0; attempt++) {
    await frame(shot, damCamera);
    built.rendered = await page.evaluate(() => globalThis.appMap
      .queryRenderedFeatures({ layers: ['dam-embankment-fill', 'dam-crest-fill'] }).length);
  }
  if (built.rendered === 0) throw new Error('The embankment did not render.');

  await page.waitForSelector('.build-status-bug', { state: 'detached', timeout: 120_000 })
    .catch(() => {});
  // The 3D view is where an embankment that failed to tie into the abutments
  // would show up as a wall standing off the hillside.
  await page.click('.map-3d-toggle, button:has-text("3D")').catch(() => {});
  await frame(shot.replace(/\.png$/, '-3d.png'),
    { center: built.center, zoom: 16, pitch: 65, bearing: 30 });

  console.log(JSON.stringify({ stream: used.stream, tried: attempts.length, designed,
    previewContours, built, pageErrors }, null, 2));
  if (pageErrors.length) throw new Error(`Page errors: ${pageErrors.join(' | ')}`);
} catch (error) {
  console.error(error);
  await page.screenshot({ path: shot }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
