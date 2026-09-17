import { defineConfig, devices } from '@playwright/test';

const PORT = 44175;

export default defineConfig({
  testDir: './tests/p1c',
  outputDir: 'test-results/p1c',
  fullyParallel: false,
  workers: 1,
  timeout: 90_000,
  expect: { timeout: 10_000 },
  reporter: process.env.CI ? 'line' : 'list',
  use: {
    baseURL: `http://127.0.0.1:${PORT}`,
    trace: 'retain-on-failure',
    screenshot: 'only-on-failure',
  },
  webServer: {
    command: `node ./node_modules/vite/bin/vite.js --config vite.config.p1c.ts --host 127.0.0.1 --port ${PORT} --strictPort`,
    url: `http://127.0.0.1:${PORT}/tests/p1c/harness.html`,
    reuseExistingServer: false,
    timeout: 60_000,
  },
  projects: [
    {
      name: 'p1c-browser',
      testMatch: '**/browser-design-storage.spec.ts',
      use: {
        ...devices['Desktop Chrome'],
        launchOptions: { args: ['--enable-unsafe-swiftshader', '--use-angle=swiftshader'] },
      },
    },
    {
      name: 'p1c-electron',
      testMatch: '**/electron-design-storage.spec.ts',
    },
  ],
});
