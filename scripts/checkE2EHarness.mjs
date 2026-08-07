import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import path from 'node:path';

const require = createRequire(import.meta.url);
let cli;
try {
  const packageJson = require.resolve('@playwright/test/package.json');
  cli = path.join(path.dirname(packageJson), 'cli.js');
} catch {
  console.error('Install the pinned @playwright/test development dependency before checking the E2E harness.');
  process.exit(1);
}

const result = spawnSync(
  process.execPath,
  [cli, 'test', '--config=playwright.config.ts', '--project=negative-control'],
  {
    cwd: process.cwd(),
    env: {
      ...process.env,
      PLAYWRIGHT_NEGATIVE_CONTROL: '1',
      PW_TEST_HTML_REPORT_OPEN: 'never',
    },
    encoding: 'utf8',
  },
);

if (result.stdout) process.stdout.write(result.stdout);
if (result.stderr) process.stderr.write(result.stderr);

if (result.error) {
  console.error(`Unable to start Playwright: ${result.error.message}`);
  process.exit(1);
}

const output = `${result.stdout ?? ''}\n${result.stderr ?? ''}`;
if (result.status === 0) {
  console.error('E2E harness check failed: the deliberately failing test returned exit code 0.');
  process.exit(1);
}
if (!output.includes('E2E_NEGATIVE_CONTROL_EXPECTED_FAILURE')
    || !output.includes('E2E_NEGATIVE_CONTROL_SENTINEL')) {
  console.error('E2E harness check failed before the negative-control assertion executed.');
  process.exit(1);
}

console.log(`E2E harness correctly propagated the negative-control failure (exit ${result.status}).`);
