import { defineConfig, devices } from '@playwright/test';

const port = 44174;
const managedServer = process.env.PLAYWRIGHT_MANAGED_SERVER === '1';
export default defineConfig({
  testDir: './tests/e2e/weather-lab', workers: 1, retries: process.env.CI ? 1 : 0, timeout: 60_000,
  reporter: process.env.CI ? 'line' : 'list',
  use: { ...devices['Desktop Chrome'], baseURL: `http://127.0.0.1:${port}`, serviceWorkers: 'block', trace: 'retain-on-failure',
    launchOptions: { args: ['--enable-unsafe-swiftshader', '--use-gl=angle', '--use-angle=swiftshader'] } },
  webServer: managedServer ? undefined : { command: `node ./node_modules/vite/bin/vite.js preview --config vite.config.weatherLab.ts --host 127.0.0.1 --port ${port} --strictPort`,
    url: `http://127.0.0.1:${port}`, reuseExistingServer: false, timeout: 60_000 },
});
