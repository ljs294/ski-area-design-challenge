// Visual verification harness for the MapLibre map (spike + all future phases).
// Drives a real Chromium (with SwiftShader WebGL), waits for the map to finish
// loading, captures console logs + page errors, and writes a screenshot.
//
// Usage:
//   node scripts/verifyMap.mjs <url> <screenshotPath> [readyGlobal]
// readyGlobal defaults to "spikeMap"; the harness waits until
//   window[readyGlobal] exists and window[readyGlobal].loaded() is true,
// then for a map 'idle' event, so tiles have actually settled.
import { chromium } from 'playwright';

const url = process.argv[2] ?? 'http://localhost:5174/spike.html';
const shot = process.argv[3] ?? 'scratchpad/verify.png';
const readyGlobal = process.argv[4] ?? 'spikeMap';

const browser = await chromium.launch({
  args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'],
});
const page = await browser.newPage({ viewport: { width: 1200, height: 900 } });

const consoleLines = [];
page.on('console', (m) => consoleLines.push(`[${m.type()}] ${m.text()}`));
const pageErrors = [];
page.on('pageerror', (e) => pageErrors.push(String(e)));

let failed = false;
try {
  await page.goto(url, { waitUntil: 'load', timeout: 30000 });
  // Wait for the map object + full load, then an idle (tiles settled).
  await page.waitForFunction(
    (g) => globalThis[g] && globalThis[g].loaded && globalThis[g].loaded(),
    readyGlobal,
    { timeout: 30000 }
  );
  await page.evaluate(
    (g) => new Promise((res) => {
      const m = globalThis[g];
      if (m.areTilesLoaded && m.areTilesLoaded()) return res();
      m.once('idle', res);
      setTimeout(res, 8000); // safety cap
    }),
    readyGlobal
  );
  await page.waitForTimeout(500);
} catch (e) {
  failed = true;
  console.error('WAIT ERROR:', e.message);
}

await page.screenshot({ path: shot });

console.log('===== CONSOLE =====');
console.log(consoleLines.join('\n') || '(none)');
console.log('===== PAGE ERRORS =====');
console.log(pageErrors.join('\n') || '(none)');
console.log('===== SCREENSHOT =====');
console.log(shot);

await browser.close();
process.exit(failed || pageErrors.length ? 1 : 0);
