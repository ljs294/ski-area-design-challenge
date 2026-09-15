import { spawnSync } from 'node:child_process';
import { createServer } from 'node:net';
import path from 'node:path';

const result = spawnSync(
  process.execPath,
  [path.join('scripts', 'runE2E.mjs'), '--project=negative-control'],
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

await new Promise((resolve, reject) => {
  const probe = createServer();
  probe.once('error', error => reject(new Error(`E2E runner left port 44173 occupied: ${error.message}`)));
  probe.listen(44173, '127.0.0.1', () => probe.close(error => error ? reject(error) : resolve()));
});

console.log(`E2E harness propagated the negative-control failure (exit ${result.status}) and released its managed server.`);
