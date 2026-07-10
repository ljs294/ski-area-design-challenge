// Regression check: slope, aspect, and ground cover are mutually exclusive —
// turning any one on switches the other two off.
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5175/';
const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1000, height: 700 } });
await page.goto(url, { waitUntil: 'load' });
await page.waitForFunction(() => window.appMap && window.appMap.loaded && window.appMap.loaded());

const ids = ['slope', 'aspect', 'groundcover'];
const vis = () =>
  page.evaluate((ids) => Object.fromEntries(ids.map((id) => [id, window.appMap.getLayoutProperty(id, 'visibility')])), ids);
const input = (label) => page.locator('.layer-row', { hasText: label }).locator('input');

let allPass = true;
for (const [label, expectOn] of [
  ['Slope angle', 'slope'],
  ['Aspect', 'aspect'],
  ['Ground cover', 'groundcover'],
]) {
  await input(label).click();
  const v = await vis();
  const pass = ids.every((id) => (id === expectOn ? v[id] === 'visible' : v[id] === 'none'));
  console.log(`${label} on -> ${JSON.stringify(v)}  ${pass ? 'OK' : 'FAIL'}`);
  if (!pass) allPass = false;
}

console.log(allPass ? 'PASS: 3-way exclusion works' : 'FAIL');
await browser.close();
process.exit(allPass ? 0 : 1);
