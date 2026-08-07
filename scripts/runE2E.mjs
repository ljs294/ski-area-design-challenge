import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import process from 'node:process';
import { preview } from 'vite';

const HOST = '127.0.0.1';
const PORT = 44173;
const require = createRequire(import.meta.url);
const packageJson = require.resolve('@playwright/test/package.json');
const cli = path.join(path.dirname(packageJson), 'cli.js');

const server = await preview({
  preview: { host: HOST, port: PORT, strictPort: true },
});

let child;

async function closeServer() {
  await new Promise((resolve, reject) => {
    server.httpServer.close((error) => error ? reject(error) : resolve());
    server.httpServer.closeAllConnections?.();
  });
}

try {
  child = spawn(
    process.execPath,
    [cli, 'test', '--config=playwright.config.ts', ...process.argv.slice(2)],
    {
      cwd: process.cwd(),
      env: {
        ...process.env,
        PLAYWRIGHT_MANAGED_SERVER: '1',
        PW_TEST_HTML_REPORT_OPEN: 'never',
      },
      stdio: 'inherit',
      windowsHide: true,
    },
  );

  const exitCode = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', (code, signal) => resolve(code ?? (signal ? 1 : 0)));
  });
  process.exitCode = exitCode;
} finally {
  child?.kill();
  await closeServer();
}
