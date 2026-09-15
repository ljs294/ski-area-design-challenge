import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';
import process from 'node:process';
import { createServer } from 'vite';

const require = createRequire(import.meta.url);
const cli = path.join(path.dirname(require.resolve('@playwright/test/package.json')), 'cli.js');
const caseArgument = process.argv.slice(2).find(argument => argument.startsWith('--case='));
const playwrightArguments = process.argv.slice(2).filter(argument => argument !== caseArgument);
const server = await createServer({
  configFile: 'vite.config.snowReproducer.ts',
  server: { host: '127.0.0.1', port: 44619, strictPort: true },
});
let child;

try {
  await server.listen();
  child = spawn(process.execPath, [cli, 'test', '--config=playwright.snowReproducer.config.ts', ...playwrightArguments], {
    cwd: process.cwd(), windowsHide: true, stdio: 'inherit',
    env: { ...process.env, ...(caseArgument ? { SNOW_REPRO_CASES: caseArgument.slice('--case='.length) } : {}),
      PLAYWRIGHT_MANAGED_SERVER: '1', PW_TEST_HTML_REPORT_OPEN: 'never' },
  });
  const deadline = setTimeout(() => child?.kill(), 240_000);
  const exitCode = await new Promise((resolve, reject) => {
    child.once('error', reject);
    child.once('exit', (code, signal) => resolve(code ?? (signal ? 1 : 0)));
  });
  clearTimeout(deadline);
  process.exitCode = exitCode;
} finally {
  child?.kill();
  await server.close();
}
