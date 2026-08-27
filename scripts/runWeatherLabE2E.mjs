import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import { preview } from 'vite';

const host = '127.0.0.1'; const port = 44174; const require = createRequire(import.meta.url);
const cli = path.join(path.dirname(require.resolve('@playwright/test/package.json')), 'cli.js');
const server = await preview({ configFile: 'vite.config.weatherLab.ts', preview: { host, port, strictPort: true } });
let child;
try {
  child = spawn(process.execPath, [cli, 'test', '--config=playwright.weatherLab.config.ts'], { cwd: process.cwd(), windowsHide: true, stdio: 'inherit',
    env: { ...process.env, PLAYWRIGHT_MANAGED_SERVER: '1', PW_TEST_HTML_REPORT_OPEN: 'never' } });
  process.exitCode = await new Promise((resolve, reject) => { child.once('error', reject); child.once('exit', (code, signal) => resolve(code ?? (signal ? 1 : 0))); });
} finally {
  child?.kill();
  await new Promise((resolve, reject) => { server.httpServer.close((error) => error ? reject(error) : resolve()); server.httpServer.closeAllConnections?.(); });
}
