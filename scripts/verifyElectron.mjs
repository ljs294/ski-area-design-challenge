// Electron-level verification: launches the REAL electron binary (via Playwright)
// with the app's tightened webPreferences, pointed at a running dev server, and
// confirms the map renders in the actual desktop window.
//
// Usage: VITE_DEV_SERVER_URL=http://localhost:5175 node scripts/verifyElectron.mjs <screenshotPath>
import { _electron as electron } from 'playwright';

const shot = process.argv[2] ?? 'scratchpad/electron.png';

const app = await electron.launch({
  args: ['dist-electron/main.js'],
  env: { ...process.env },
});

let failed = false;
try {
  const win = await app.firstWindow({ timeout: 30000 });
  win.on('pageerror', (e) => console.error('PAGEERROR:', String(e)));
  await win.waitForFunction(
    () => window.appMap && window.appMap.loaded && window.appMap.loaded(),
    null,
    { timeout: 30000 }
  );
  await win.evaluate(
    () => new Promise((res) => {
      const m = window.appMap;
      if (m.areTilesLoaded && m.areTilesLoaded()) return res();
      m.once('idle', res);
      setTimeout(res, 8000);
    })
  );
  await win.waitForTimeout(500);
  await win.screenshot({ path: shot });
  console.log('Electron window rendered ✓  ->', shot);
} catch (e) {
  failed = true;
  console.error('ELECTRON VERIFY ERROR:', e.message);
}

await app.close();
process.exit(failed ? 1 : 0);
