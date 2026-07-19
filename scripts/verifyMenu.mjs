// Drives the new main menu + New Game flow in a real Chromium (SwiftShader
// WebGL). Captures light + dark menu and the New Game map screen.
import { chromium } from 'playwright';

const base = process.argv[2] ?? 'http://localhost:4173/';
const outDir = process.argv[3] ?? 'scratchpad';

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });
const errors = [];
page.on('pageerror', (e) => errors.push(String(e)));
page.on('console', (m) => {
  if (m.type() === 'error') errors.push('[console] ' + m.text());
});

async function waitMap(global) {
  // The menu backdrop drifts continuously, so it never goes 'idle' / loaded().
  // Wait for the style + tiles instead, then let it settle.
  await page.waitForFunction(
    (g) => {
      const m = globalThis[g];
      return m && m.isStyleLoaded && m.isStyleLoaded() && (!m.areTilesLoaded || m.areTilesLoaded());
    },
    global,
    { timeout: 30000 }
  );
  await page.waitForTimeout(1500);
}

try {
  // --- Light menu ---
  await page.goto(base, { waitUntil: 'load', timeout: 30000 });
  await waitMap('menuMap');
  const slats = await page.$$eval('.slat-label', (els) => els.map((e) => e.textContent));
  const continueDisabled = await page.$eval('.trail-slat', (el) => el.disabled);
  console.log('SLATS:', JSON.stringify(slats));
  console.log('CONTINUE_DISABLED:', continueDisabled);
  await page.screenshot({ path: `${outDir}/menu-light.png` });

  // --- Dark menu (persist a dark theme, reload) ---
  await page.evaluate(() => localStorage.setItem('skiapp:settings', JSON.stringify({ theme: 'dark' })));
  await page.reload({ waitUntil: 'load' });
  await waitMap('menuMap');
  const bg = await page.evaluate(() => getComputedStyle(document.body).backgroundColor);
  const themeAttr = await page.evaluate(() => document.documentElement.dataset.theme);
  console.log('DARK_THEME_ATTR:', themeAttr, 'BODY_BG:', bg);
  await page.screenshot({ path: `${outDir}/menu-dark.png` });

  // --- New Game -> map picking screen ---
  await page.evaluate(() => localStorage.setItem('skiapp:settings', JSON.stringify({ theme: 'light' })));
  await page.reload({ waitUntil: 'load' });
  await waitMap('menuMap');
  await page.click('.trail-slat >> text=New Game');
  await waitMap('appMap');
  const hasHud = await page.$('.game-menu');
  const hasSite = await page.$('.site-control');
  console.log('NEWGAME_MENU:', !!hasHud, 'SITE_CONTROL:', !!hasSite);
  await page.screenshot({ path: `${outDir}/newgame.png` });

  // --- Open Settings modal from the top-right Menu dropdown ---
  await page.click('.game-menu-btn');
  await page.click('.hud-settings');
  await page.waitForSelector('.settings-panel', { timeout: 5000 });
  await page.screenshot({ path: `${outDir}/settings.png` });
} catch (e) {
  console.error('FAIL:', e.message);
}

console.log('===== PAGE ERRORS =====');
console.log(errors.join('\n') || '(none)');
await browser.close();
process.exit(errors.length ? 1 : 0);
