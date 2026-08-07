import { defineConfig, devices } from '@playwright/test';

const PREVIEW_PORT = 44173;
const PREVIEW_URL = `http://127.0.0.1:${PREVIEW_PORT}`;
const negativeControl = process.env.PLAYWRIGHT_NEGATIVE_CONTROL === '1';
const managedServer = process.env.PLAYWRIGHT_MANAGED_SERVER === '1';

const chromiumUse = {
  ...devices['Desktop Chrome'],
  launchOptions: {
    args: [
      '--enable-unsafe-swiftshader',
      '--use-gl=angle',
      '--use-angle=swiftshader',
    ],
  },
};

export default defineConfig({
  testDir: './tests/e2e',
  outputDir: 'test-results/e2e',
  fullyParallel: false,
  workers: 1,
  retries: process.env.CI ? 1 : 0,
  timeout: 30_000,
  expect: { timeout: 7_500 },
  forbidOnly: !!process.env.CI,
  reporter: process.env.CI ? 'line' : 'list',
  testIgnore: negativeControl ? [] : ['**/negative-control/**'],
  use: {
    baseURL: PREVIEW_URL,
    serviceWorkers: 'block',
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
    video: 'off',
  },
  // The negative control proves runner exit propagation and needs no app server.
  webServer: negativeControl || managedServer
    ? undefined
    : {
        command: `node ./node_modules/vite/bin/vite.js preview --host 127.0.0.1 --port ${PREVIEW_PORT} --strictPort`,
        url: PREVIEW_URL,
        reuseExistingServer: false,
        timeout: 60_000,
      },
  projects: negativeControl
    ? [
        {
          name: 'negative-control',
          testMatch: '**/negative-control/**/*.spec.ts',
        },
      ]
    : [
        {
          name: 'browser-smoke',
          testMatch: '**/browser-smoke/**/*.spec.ts',
          use: chromiumUse,
        },
        {
          name: 'feature-workflows',
          testMatch: '**/feature-workflows/**/*.spec.ts',
          use: chromiumUse,
        },
        {
          name: 'live-provider',
          testMatch: '**/live-provider/**/*.spec.ts',
          use: chromiumUse,
        },
        {
          name: 'electron-release',
          testMatch: '**/electron-release/**/*.spec.ts',
        },
      ],
});
