import { spawn } from 'node:child_process';
import { once } from 'node:events';
import { createRequire } from 'node:module';
import path from 'node:path';
import { build, preview } from 'vite';
import { mkdtemp, rm } from 'node:fs/promises';
import { tmpdir } from 'node:os';
import { listenWeatherService } from '../weather-service/server.mjs';

const host = '127.0.0.1'; const port = 44174; const require = createRequire(import.meta.url);
const cli = path.join(path.dirname(require.resolve('@playwright/test/package.json')), 'cli.js');
const cacheDirectory = await mkdtemp(path.join(tmpdir(), 'weather-lab-e2e-'));
const weatherService = listenWeatherService({ mode: 'fixture', cacheDirectory, port: 0 });
await once(weatherService.server, 'listening');
const serviceAddress = weatherService.server.address();
if (!serviceAddress || typeof serviceAddress === 'string') throw new Error('Weather Lab fixture service did not expose a TCP port.');
process.env.VITE_WEATHER_SERVICE_URL = `http://${host}:${serviceAddress.port}`;
await build({ configFile: 'vite.config.weatherLab.ts' });
const server = await preview({ configFile: 'vite.config.weatherLab.ts', preview: { host, port, strictPort: true } });
let child;
try {
  child = spawn(process.execPath, [cli, 'test', '--config=playwright.weatherLab.config.ts'], { cwd: process.cwd(), windowsHide: true, stdio: 'inherit',
    env: { ...process.env, PLAYWRIGHT_MANAGED_SERVER: '1', PW_TEST_HTML_REPORT_OPEN: 'never' } });
  process.exitCode = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', (code, signal) => resolve(code ?? (signal ? 1 : 0))); });
} finally {
  child?.kill();
  server.httpServer.closeAllConnections?.();
  if (server.httpServer.listening) await new Promise((resolve, reject) => server.httpServer.close((error) => error ? reject(error) : resolve()));
  weatherService.server.closeAllConnections?.();
  if (weatherService.server.listening) await new Promise((resolve, reject) => weatherService.server.close((error) => error ? reject(error) : resolve()));
  await rm(cacheDirectory, { recursive: true, force: true });
}
