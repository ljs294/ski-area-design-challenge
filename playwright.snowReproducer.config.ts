import { defineConfig } from '@playwright/test';

export default defineConfig({ testDir: './tests/e2e/performance', testMatch: 'snow-reproducer.spec.ts', workers: 1,
  retries: 0, timeout: 180_000, reporter: 'list', outputDir: 'test-results/snow-reproducer/playwright',
  webServer: process.env.PLAYWRIGHT_MANAGED_SERVER === '1' ? undefined : {
    command: 'node ./node_modules/vite/bin/vite.js --config vite.config.snowReproducer.ts',
    url: 'http://127.0.0.1:44619', reuseExistingServer: false, timeout: 60_000 },
  use: { baseURL: 'http://127.0.0.1:44619', viewport: { width: 1920, height: 1080 }, deviceScaleFactor: 1,
    trace: 'retain-on-failure', screenshot: 'only-on-failure', video: 'off' } });
