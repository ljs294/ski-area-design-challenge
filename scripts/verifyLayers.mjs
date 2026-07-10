// Enable a specific set of layers via the panel, then screenshot + report state.
// Usage: node scripts/verifyLayers.mjs <url> <shot> "<comma,separated,labels to enable>"
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/';
const shot = process.argv[3] ?? 'scratchpad/layers.png';
const wantOn = (process.argv[4] ?? '').split(',').map((s) => s.trim()).filter(Boolean);

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(String(e)));
page.on('console', (m) => {
  const t = m.text();
  if (t.includes('[worldcover]') || t.includes('[slope]') || t.includes('bad tile')) console.log(`  ${t}`);
});

await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());
const settle = () =>
  page.evaluate(
    () => new Promise((res) => {
      const m = window.appMap;
      if (m.areTilesLoaded()) return res();
      m.once('idle', res);
      setTimeout(res, 12000);
    })
  );
await settle();

// Set each row to desired state.
const rows = page.locator('.layer-row');
const count = await rows.count();
for (let i = 0; i < count; i++) {
  const row = rows.nth(i);
  const label = (await row.textContent())?.trim() ?? '';
  const checked = await row.locator('input').isChecked();
  const shouldBeOn = wantOn.some((w) => label.includes(w));
  if (checked !== shouldBeOn) await row.locator('input').click();
}
await settle();
await page.waitForTimeout(1500); // extra repaint margin for the slower recolor protocol

const vis = await page.evaluate(() => {
  const ids = ['hillshade', 'contour-lines', 'slope', 'aspect', 'groundcover'];
  const m = window.appMap;
  return Object.fromEntries(ids.map((id) => [id, m.getLayer(id) ? m.getLayoutProperty(id, 'visibility') : 'missing']));
});
console.log('visibility:', JSON.stringify(vis));
console.log('pageerrors:', errors.length ? errors.join('\n') : '(none)');

await page.screenshot({ path: shot });
await browser.close();
process.exit(errors.length ? 1 : 0);
