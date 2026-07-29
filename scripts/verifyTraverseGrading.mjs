// Browser-level check for benched traverse grading. Paints a run ACROSS the
// fall line — the case the grader used to give up on — and verifies the three
// things the feature promises:
//
//   1. the run grades rather than being refused, and Build stays enabled;
//   2. the contours it moved are highlighted in yellow, and clear again when
//      the box is unchecked;
//   3. those highlighted contours run SQUARE to the painted centreline, which
//      is the visible signature of a level bench.
//
// Run against a preview server with network access so the normal New Resort
// terrain preparation can complete:
//   node scripts/verifyTraverseGrading.mjs http://localhost:4173/
//
// Prefers a real GPU. Under SwiftShader a wide paint can stall the page's main
// thread for tens of seconds, which is why every wait here is generous and the
// buttons are driven through the DOM.
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
// Terrain-mesh rendering crashes SwiftShader; the checks below read the map's
// vector sources and the panel, so a flat hillshaded view is enough.
const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
page.setDefaultTimeout(150_000);
const errors = [];
page.on('pageerror', (error) => errors.push(String(error)));
page.on('console', (message) => { if (message.type() === 'error') errors.push(message.text()); });

const panelText = () => page.locator('.trail-panel').innerText();

// Half the painted stroke length, in screen pixels. Short enough that a stroke
// stays on reasonably uniform ground, long enough to be a real traverse.
const HALF_STROKE_PX = 70;

/**
 * Find somewhere on screen to paint a genuine traverse: steep enough that a run
 * across it must be benched, gentle enough that a 45° face can still daylight.
 * Returns the screen-space unit vector along the contour there.
 */
async function findTraverseSite(minSlope, maxSlope) {
  return page.evaluate(({ minSlope, maxSlope, halfStrokePx }) => {
    const map = globalThis.appMap;
    // Tight baseline: a wide one averages away the steep spots the grader will
    // trip over, and it is the steepest station that decides a stretch.
    const probePx = 20;
    const metersBetween = (a, b) => {
      const midLat = (a.lat + b.lat) / 2 * Math.PI / 180;
      return Math.hypot((b.lng - a.lng) * 111320 * Math.cos(midLat),
        (b.lat - a.lat) * 111320);
    };
    const sample = (center) => {
      const at = (x, y) => {
        const point = map.unproject([x, y]);
        return { point, z: map.queryTerrainElevation(point) };
      };
      const w = at(center[0] - probePx, center[1]), e = at(center[0] + probePx, center[1]);
      const n = at(center[0], center[1] - probePx), s = at(center[0], center[1] + probePx);
      if (![w.z, e.z, n.z, s.z].every(Number.isFinite)) return null;
      const dx = (e.z - w.z) / Math.max(1e-6, metersBetween(w.point, e.point));
      const dy = (s.z - n.z) / Math.max(1e-6, metersBetween(n.point, s.point));
      const slope = Math.hypot(dx, dy);
      if (slope < 1e-6) return null;
      // The contour runs perpendicular to the gradient.
      return { center, slope, along: [-dy / slope, dx / slope] };
    };
    const strokeSlopes = (center, along) => {
      const slopes = [];
      for (let step = -1; step <= 1; step += 0.125) {
        const probe = sample([center[0] + along[0] * halfStrokePx * step,
          center[1] + along[1] * halfStrokePx * step]);
        if (!probe) return null;
        slopes.push(probe.slope);
      }
      return slopes;
    };
    let best = null;
    let flattestRejected = Infinity;
    for (let x = 300; x <= 900; x += 50) for (let y = 240; y <= 700; y += 50) {
      const middle = sample([x, y]);
      if (!middle) continue;
      const slopes = strokeSlopes(middle.center, middle.along);
      if (!slopes) continue;
      const steepest = Math.max(...slopes);
      const gentlest = Math.min(...slopes);
      if (gentlest < minSlope || steepest > maxSlope) {
        if (gentlest >= minSlope) flattestRejected = Math.min(flattestRejected, steepest);
        continue;
      }
      const target = (minSlope + maxSlope) / 2;
      if (!best || Math.abs(steepest - target) < Math.abs(best.slope - target))
        best = { ...middle, slope: steepest, gentlestSlope: gentlest };
    }
    return best ?? { rejected: true, flattestRejected };
  }, { minSlope, maxSlope, halfStrokePx: HALF_STROKE_PX });
}

async function paintStroke(center, along, halfLengthPx) {
  const from = [center[0] - along[0] * halfLengthPx, center[1] - along[1] * halfLengthPx];
  const to = [center[0] + along[0] * halfLengthPx, center[1] + along[1] * halfLengthPx];
  await page.mouse.move(from[0], from[1]);
  await page.mouse.down();
  await page.mouse.move(to[0], to[1], { steps: 60 });
  await page.evaluate(() => window.dispatchEvent(new MouseEvent('mouseup', { bubbles: true })));
  await page.waitForFunction(() =>
    !document.querySelector('.trail-panel button.site-btn-primary')?.disabled,
    null, { timeout: 60_000 });
}

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

/**
 * Read the yellow highlight back off the map and measure how each segment lies
 * relative to the painted centreline. `along` is the trail direction in screen
 * space, so a segment square to the run has |cos| near zero.
 */
async function highlightGeometry(along) {
  return page.evaluate((along) => {
    const map = globalThis.appMap;
    if (!map.getLayer('graded-contour-lines')) return { count: 0, squareFraction: 0 };
    const features = map.queryRenderedFeatures({ layers: ['graded-contour-lines'] });
    const cosines = [];
    for (const feature of features) {
      const lines = feature.geometry.type === 'MultiLineString'
        ? feature.geometry.coordinates : [feature.geometry.coordinates];
      for (const line of lines) {
        for (let i = 1; i < line.length; i++) {
          const a = map.project(line[i - 1]), b = map.project(line[i]);
          const dx = b.x - a.x, dy = b.y - a.y;
          const length = Math.hypot(dx, dy);
          if (length < 0.5) continue;
          cosines.push(Math.abs((dx * along[0] + dy * along[1]) / length));
        }
      }
    }
    // Within 30° of square to the run.
    const square = cosines.filter((value) => value < Math.cos(Math.PI / 3)).length;
    return {
      count: cosines.length,
      squareFraction: cosines.length ? square / cosines.length : 0,
    };
  }, along);
}

/** Paint one traverse at `brushWidthM` and report what the grader makes of it. */
async function gradeTraverse(brushWidthM, center, along) {
  await clickWhenReady('.lift-add-btn');
  await page.waitForSelector('text=Paint ski run');
  // The brush slider steps in 2 m; snap to a notch it will accept.
  await page.locator('.trail-brush-slider')
    .fill(String(Math.max(8, Math.min(120, Math.ceil(brushWidthM / 2) * 2))));
  await paintStroke(center, along, HALF_STROKE_PX);
  await clickWhenReady('.trail-panel button.site-btn-primary');
  await page.waitForSelector('text=Review ski run', { timeout: 60_000 });

  await clickWhenReady('.trail-grade-terrain input');
  await page.waitForFunction(
    () => document.querySelector('.trail-grade-terrain input')?.checked &&
      !document.body.textContent?.includes('Calculating terrain grade'),
    null,
    { timeout: 90_000 }
  );
  await page.waitForTimeout(400);
  const text = await panelText();
  const blocked = await page.evaluate(() =>
    !!document.querySelector('.trail-panel button.site-btn-primary')?.disabled);
  const highlight = await highlightGeometry(along);

  // Uncheck and confirm the highlight goes away with the proposal.
  await clickWhenReady('.trail-grade-terrain input');
  await page.waitForFunction(
    () => !document.querySelector('.trail-grade-terrain input')?.checked,
    null, { timeout: 60_000 });
  await page.waitForTimeout(400);
  const clearedHighlight = await highlightGeometry(along);

  // The panel formats distance in the user's units, which default to imperial.
  const volume = (label) => {
    const match = text.match(new RegExp(`${label}\\s*([\\d.,]+)\\s*(yd³|m³)`));
    return match ? Number(match[1].replace(/,/g, '')) : null;
  };
  const ungraded = text.match(/([\d.,]+)\s*(ft|m)\b\s*of this run is steeper/);
  return {
    brushWidthM,
    blocked,
    cutVolume: volume('Cut'),
    fillVolume: volume('Fill'),
    groundCrossSlopePct: Number(text.match(/Hillside cross slope\s*([\d.]+)%/)?.[1] ?? NaN),
    ungradedM: ungraded
      ? Number(ungraded[1].replace(/,/g, '')) * (ungraded[2] === 'ft' ? 0.3048 : 1)
      : 0,
    highlight,
    clearedHighlight,
    panel: text.replace(/\s*\n\s*/g, ' | '),
  };
}

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
  await page.fill('.name-entry-input', 'Traverse Grading Resort');
  await page.click('text=Start Designing');
  await page.waitForSelector('.hud-resort', { timeout: 120_000 });
  // A New Game handoff can briefly transition from App's loading surface to
  // MapView's local warm-up surface. Drain both before touching the HUD.
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
  // Software WebGL can starve the loading-screen transition, and it can come
  // back after the app's own escape action has already fired. Entry is
  // authorized by this point either way, so keep the surface out of the way of
  // pointer-driven steps for good without mutating application state.
  await page.addStyleTag({
    content: '.resort-loading { pointer-events: none !important; opacity: 0 !important; }',
  });
  if (await page.evaluate(() => globalThis.appMap.getPitch() > 1)) {
    await page.evaluate(() => globalThis.appMap.jumpTo({ pitch: 0 }));
  }

  // Benchable sidehill: steeper than 12% needs a bench to be skiable, gentler
  // than 70% leaves the 45° faces somewhere to daylight.
  const site = await findTraverseSite(0.12, 0.70);
  if (!site || site.rejected)
    throw new Error('No benchable sidehill on screen to traverse; the flattest ' +
      `full-length traverse available still peaks at ${Math.round((site?.flattestRejected ?? Infinity) * 100)}% ` +
      'cross slope.');

  await clickWhenReady('.dock-circle-trails');
  const graded = await gradeTraverse(40, site.center, site.along);
  await page.screenshot({ path: 'verify-traverse-grading.png' });

  // 1. Grading is a tool, not a gate.
  if (graded.blocked)
    throw new Error(`Grading blocked the build: ${graded.panel}`);
  if (!(graded.cutVolume > 0) || !(graded.fillVolume > 0))
    throw new Error(`No cut/fill reported for a graded traverse: ${graded.panel}`);

  // 2. The edit is visible, and only while it is a proposal.
  if (graded.highlight.count === 0)
    throw new Error('Grading moved contours but highlighted none of them.');
  if (graded.clearedHighlight.count !== 0)
    throw new Error('Unchecking Grade terrain left the yellow highlight behind.');

  // 3. The bench is level, which shows up as contours square to the run. The
  //    hillside's own contours run ALONG a traverse, so before grading this
  //    fraction would be near zero.
  if (graded.highlight.squareFraction < 0.25)
    throw new Error('Only ' + Math.round(graded.highlight.squareFraction * 100) +
      '% of the moved contours run square to the centreline — the bench is not level.');

  console.log(JSON.stringify({ site, graded, errors }, null, 2));
  if (errors.length) process.exitCode = 1;
} catch (error) {
  console.error(error);
  await page.screenshot({ path: 'verify-traverse-grading-failure.png' }).catch(() => {});
  process.exitCode = 1;
} finally {
  await browser.close();
}
