import { chromium } from 'playwright';
import { spawn } from 'node:child_process';
import { createServer } from 'node:net';
import { createRequire } from 'node:module';
import { mkdtemp, mkdir } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';

const profile = await mkdtemp(path.join(os.tmpdir(), 'mountain-menu-check-'));
const environment = { ...process.env, VITE_DEV_SERVER_URL: '', GRAPHICS_LAB: '', WEATHER_LAB: '' };
delete environment.ELECTRON_RUN_AS_NODE;
const server = createServer(); await new Promise((resolve) => server.listen(0, '127.0.0.1', resolve));
const port = server.address().port; await new Promise((resolve) => server.close(resolve));
const executable = createRequire(import.meta.url)('electron');
const child = spawn(executable, ['.', `--user-data-dir=${profile}`, `--remote-debugging-port=${port}`],
  { env: environment, windowsHide: true, stdio: 'ignore' });
let browser;
try {
  const deadline = Date.now() + 30000;
  while (true) {
    if (Date.now() > deadline || child.exitCode !== null) throw new Error('Electron debugging endpoint did not start');
    try { const response = await fetch(`http://127.0.0.1:${port}/json/version`); if (response.ok) break; } catch { /* starting */ }
    await new Promise((resolve) => setTimeout(resolve, 250));
  }
  browser = await chromium.connectOverCDP(`http://127.0.0.1:${port}`);
  const context = browser.contexts()[0];
  const page = context.pages()[0] ?? await context.waitForEvent('page', { timeout: 20000 });
  page.on('console', (message) => { if (message.type() === 'error') console.error(message.text()); });
  await page.waitForSelector('.trail-sign-new', { timeout: 20000 });
  await page.waitForSelector('.menu-backdrop-map[data-ready="true"]', { timeout: 30000 });
  await mkdir('test-results/menu-desktop', { recursive: true });
  await page.screenshot({ path: 'test-results/menu-desktop/menu.png' });
  console.log('Built Electron file:// menu loaded bundled ground cover successfully.');
  await page.getByRole('button', { name: 'Quit', exact: true }).click();
} finally { await browser?.close(); child.kill(); }
