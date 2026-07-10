// Verifies basemap polish: nav + scale controls, rotation enabled, basemap
// feature toggles, and place search.
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/';
const outDir = process.argv[3] ?? '.';
const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1000, height: 720 } });
page.on('pageerror', (e) => console.log('PAGEERROR:', e.message));
await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());
await page.waitForTimeout(1500);

let pass = true;
const expect = (c, m) => { console.log(`${c ? 'OK  ' : 'FAIL'} ${m}`); if (!c) pass = false; };
const layerVis = (id) => page.evaluate((id) => window.appMap.getLayoutProperty(id, 'visibility') ?? 'visible', id);
const firstRoadId = () =>
  page.evaluate(() =>
    window.appMap.getStyle().layers.find((l) => l['source-layer'] === 'transportation')?.id
  );

// Controls present.
expect((await page.locator('.search-box').count()) === 1, 'search box present');
expect((await page.locator('.maplibregl-ctrl-zoom-in').count()) === 1, 'nav zoom control present');
expect((await page.locator('.maplibregl-ctrl-compass').count()) === 1, 'compass (rotate) control present');
expect((await page.locator('.maplibregl-ctrl-scale').count()) >= 1, 'scale bar present');

// Rotation enabled (mouse + keyboard).
const rot = await page.evaluate(() => ({
  drag: window.appMap.dragRotate.isEnabled(),
  kb: window.appMap.keyboard.isEnabled(),
}));
expect(rot.drag && rot.kb, `rotation enabled (drag=${rot.drag}, keyboard=${rot.kb})`);

// Basemap section + feature toggles.
expect((await page.locator('.layer-section-title', { hasText: 'Basemap' }).count()) === 1, 'Basemap section shown');
for (const label of ['Water', 'Roads', 'Buildings', 'Labels']) {
  expect((await page.locator('.layer-row', { hasText: label }).count()) === 1, `toggle "${label}" present`);
}

// Toggle Roads off -> a transportation layer hides.
const roadId = await firstRoadId();
await page.locator('.layer-row', { hasText: 'Roads' }).locator('input').click();
expect((await layerVis(roadId)) === 'none', `Roads toggle hides transportation layer (${roadId})`);
await page.locator('.layer-row', { hasText: 'Roads' }).locator('input').click(); // restore

await page.screenshot({ path: `${outDir}/polish_default.png` });

// Place search (soft — network): fly to Aspen and expect the center to move west+south.
const before = await page.evaluate(() => window.appMap.getCenter());
await page.locator('.search-input').fill('Aspen, Colorado');
await page.locator('.search-btn').click();
await page.waitForTimeout(4000);
const after = await page.evaluate(() => window.appMap.getCenter());
const moved = Math.abs(after.lng - before.lng) > 1 || Math.abs(after.lat - before.lat) > 1;
expect(moved, `search flew the map (from ${before.lng.toFixed(2)},${before.lat.toFixed(2)} to ${after.lng.toFixed(2)},${after.lat.toFixed(2)})`);

console.log(pass ? 'PASS' : 'FAIL');
await browser.close();
process.exit(pass ? 0 : 1);
