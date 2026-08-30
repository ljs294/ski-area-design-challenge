import { expect, test } from '@playwright/test';

async function runSimulation(page: import('@playwright/test').Page) {
  await page.getByRole('button', { name: 'Run Simulation' }).click();
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });
}

test('Jackson 2019 opens as a five-day Simulation truth view and applies one tuning draft', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Weather Model Lab' })).toBeVisible();
  await expect(page.getByLabel('Map showing 44.16729, -71.164239')).toBeVisible();
  await expect(page.getByText('Location ready')).toBeVisible();
  await runSimulation(page);

  await expect(page.getByRole('log', { name: 'Weather preparation activity' })).toContainText('Simulation completed');
  await expect(page.locator('.progress-overview')).toContainText('100%');
  await expect(page.locator('.step-progress')).toContainText('MERRA-2 hourly');
  await expect(page.getByRole('heading', { name: 'Simulation tuning' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Five-day Simulation weather' })).toBeVisible();
  await expect(page.locator('.forecast-days [role="tab"]')).toHaveCount(5);
  await expect(page.locator('.forecast-days [role="tab"]').first().locator('.forecast-card-simulation')).toContainText('Simulation');
  await expect(page.locator('.forecast-days [role="tab"]').first().locator('.forecast-card-simulation')).toContainText(/Wind .+\u00b0/);
  await expect(page.locator('.forecast-days [role="tab"]').first()).toContainText('Actual historical');
  await expect(page.locator('.forecast-series-toggles')).toContainText('Simulation weather');
  await expect(page.locator('.forecast-series-toggles')).toContainText('Actual historical weather');
  await expect(page.getByText('Simulation forecast', { exact: false })).toHaveCount(0);
  await expect(page.getByRole('button', { name: /Wet bulb/ })).toBeVisible();
  await page.getByRole('button', { name: /Precipitation/ }).click();
  await expect(page.locator('.forecast-chart')).toHaveAttribute('aria-labelledby', /simulation-tab/);
  await page.getByRole('button', { name: /Wind \/ gust/ }).click();
  await expect(page.locator('.forecast-crosshair .simulation-truth-readout')).toContainText(/Simulation .+gust .+\u00b0/);
  await page.locator('.unit-toggle').getByLabel('Metric').check();
  await expect(page.locator('.forecast-days [role="tab"]').first()).toContainText(/°/);
  await page.locator('.forecast-days [role="tab"]').nth(1).click();
  await expect(page.getByRole('heading', { name: /weather overview/ })).toBeVisible();

  await expect(page.getByText('Baseline truth hash')).toHaveCount(0);
  await expect(page.getByText('No Baseline is pinned')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Simulation scorecard' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Accessible daily comparison' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Daily condition and macro-state ribbons' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Markov-chain diagnostics' })).toBeVisible();

  const simulationRuns = page.locator('.status-console-entry').filter({ hasText: 'Sending Simulation run' });
  const runCount = await simulationRuns.count();
  const firstHash = await page.getByText('Simulation truth hash').locator('..').locator('strong').textContent();
  await page.getByLabel('Temperature response').fill('0.55');
  await expect(page.getByText('Draft has unapplied changes.')).toBeVisible();
  await page.waitForTimeout(400);
  await expect(simulationRuns).toHaveCount(runCount);
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(simulationRuns).toHaveCount(runCount + 1);
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });
  expect(await page.getByText('Simulation truth hash').locator('..').locator('strong').textContent()).not.toBe(firstHash);
});

test('cancels preparation even when cancellation races the create response', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByRole('checkbox', { name: 'Override elevation' }).check();
  await page.getByLabel('Elevation metres').fill('428');
  await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByRole('button', { name: 'Run Simulation' }).click();
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.locator('.status > span').filter({ hasText: /^Cancelled$/ })).toBeVisible();
  await expect(page.getByRole('log', { name: 'Weather preparation activity' })).toContainText('Cancellation requested');
  await expect(page.getByRole('button', { name: 'Run Simulation' })).toBeEnabled();
});

test('imports and applies drafts, pins one persistent Baseline, and exports compatible files', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByText('Location ready')).toBeVisible();
  await runSimulation(page);

  for (const name of ['Export tuning', 'Export JSON', 'Daily CSV', 'Hourly CSV']) {
    const [download] = await Promise.all([page.waitForEvent('download'), page.getByRole('button', { name }).click()]);
    expect(download.suggestedFilename()).toBeTruthy();
  }

  const imported = {
    version: 1, id: 'playwright-import', stormArrivalMultiplier: 1, macroDurationMultiplier: 1,
    conditionPersistenceMultiplier: 1.5, precipitationIntensityMultiplier: 1,
    warmIntrusionMultiplier: 1, coldOutbreakMultiplier: 1, temperatureVolatilityMultiplier: 1,
    temperatureAr1: 0.94, dewPointAr1: 0.95, hourlyNormalSmoothingRadius: 1,
    temperatureResponse: 0.6, windSeverityMultiplier: 1, extremeEventMultiplier: 1,
    forecastErrorMultiplier: 1,
  };
  const simulationRuns = page.locator('.status-console-entry').filter({ hasText: 'Sending Simulation run' });
  const count = await simulationRuns.count();
  await page.locator('input[type="file"]').setInputFiles({ name: 'tuning.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(imported)) });
  await expect(page.getByText('Imported tuning "playwright-import"')).toBeVisible();
  await expect(simulationRuns).toHaveCount(count);
  await page.getByRole('button', { name: 'Apply', exact: true }).click();
  await expect(simulationRuns).toHaveCount(count + 1);
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });

  await page.getByRole('button', { name: 'Pin current Simulation' }).click();
  await expect(page.getByText('Compatible with the current Simulation.')).toBeVisible();
  await expect(page.getByText('Baseline truth hash')).toBeVisible();
  await page.reload();
  await expect(page.getByRole('button', { name: 'Delete pinned Baseline' })).toBeVisible();
  await expect(page.getByText('Stored until a Simulation is run for comparison.')).toBeVisible();
  await page.getByRole('button', { name: 'Delete pinned Baseline' }).click();
  await expect(page.getByRole('button', { name: 'Delete pinned Baseline' })).toHaveCount(0);
});

test('coordinate fields move the display map and unsupported coverage blocks running', async ({ page }) => {
  await page.goto('/'); await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByLabel('Latitude').fill('0'); await page.getByLabel('Longitude').fill('0');
  await expect(page.getByLabel('Map showing 0, 0')).toBeVisible();
  await expect(page.getByText(/Fixture mode contains only/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Run Simulation' })).toBeDisabled();
});
