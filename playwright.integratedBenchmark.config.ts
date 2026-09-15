import { defineConfig } from '@playwright/test';
import { integratedBenchmarkScenarios, resolveIntegratedBenchmarkConfiguration } from './scripts/integratedBenchmarkConfiguration.mjs';

const baseURL = process.env.INTEGRATED_BENCHMARK_BASE_URL ?? 'http://127.0.0.1:44582';
const warmupMs = Number(process.env.INTEGRATED_WARMUP_MS ?? 30_000);
const measurementMs = Number(process.env.INTEGRATED_MEASURE_MS ?? 120_000);
const scenario = resolveIntegratedBenchmarkConfiguration().scenarioName;
const scenarioMatrix = integratedBenchmarkScenarios;
const display = scenarioMatrix[scenario];
if (!display) throw new Error(`Unknown integrated benchmark scenario '${scenario}'.`);
const heavyDiagnostic = process.env.INTEGRATED_PROFILING === 'heavy';
const timeout = scenario === 'soak'
  ? Math.max(25 * 60_000, warmupMs + measurementMs + 180_000)
  : Math.max(6 * 60_000, warmupMs + measurementMs + 120_000);

export default defineConfig({
  testDir: './tests/e2e/performance',
  outputDir: process.env.INTEGRATED_PLAYWRIGHT_OUTPUT_DIR ?? 'test-results/integrated-benchmark/playwright',
  fullyParallel: false,
  workers: 1,
  retries: 0,
  timeout,
  expect: { timeout: 15_000 },
  forbidOnly: true,
  reporter: 'list',
  use: {
    headless: false,
    viewport: display.cssViewport,
    deviceScaleFactor: display.devicePixelRatio,
    serviceWorkers: 'block',
    trace: heavyDiagnostic ? 'on' : 'off',
    screenshot: 'only-on-failure',
    video: 'off',
  },
  projects: [
    {
      name: 'integrated-browser',
      testMatch: 'integrated-benchmark.spec.ts',
      use: {
        baseURL,
        browserName: 'chromium',
        launchOptions: {
          headless: false,
          args: ['--disable-software-rasterizer', ...(heavyDiagnostic
            ? ['--enable-precise-memory-info', '--js-flags=--expose-gc'] : [])],
        },
      },
    },
    {
      name: 'integrated-electron',
      testMatch: 'integrated-electron.spec.ts',
    },
  ],
});
