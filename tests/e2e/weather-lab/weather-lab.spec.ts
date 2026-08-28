import { expect, test } from '@playwright/test';

test('Jackson 2019 tunes a paired candidate, reuses artifacts, and exposes daily/event diagnostics', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByRole('heading', { name: 'Weather Model Lab' })).toBeVisible();
  await expect(page.getByLabel('Map showing 44.16729, -71.164239')).toBeVisible();
  await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByRole('button', { name: 'Run weather comparison' }).click();
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('log', { name: 'Weather preparation activity' })).toContainText('Run completed');
  await expect(page.locator('.progress-overview')).toContainText('100%');
  await expect(page.locator('.step-progress')).toContainText('MERRA-2 hourly');
  await expect(page.locator('.step-progress')).toContainText('0% remaining');
  const first = await page.locator('.summary article').first().locator('strong').textContent();
  const baselineHash = await page.locator('.summary article').nth(1).locator('strong').textContent();
  await expect(page.getByRole('heading', { name: 'Candidate tuning' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Baseline vs candidate scorecard' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Accessible daily comparison' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Daily condition and macro-state ribbons' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Jan event timeline' })).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Markov-chain diagnostics' })).toBeVisible();
  await expect(page.locator('.weather-event-timeline')).toContainText('Storm');
  await expect(page.locator('.weather-event-timeline')).toContainText('Cold Snap');
  await expect(page.locator('.weather-event-timeline')).toContainText('Warm Up');
  await expect(page.locator('.weather-event-timeline')).toContainText('Dry Spell');
  await expect(page.getByText('Storm-severity agreement')).toBeVisible();
  await expect(page.getByRole('heading', { name: 'Monthly comparison' })).toBeVisible();
  await expect(page.getByText('Forecast issues').locator('..').locator('strong')).not.toHaveText('0');
  await expect(page.getByRole('button', { name: 'Export JSON' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Daily CSV' })).toBeEnabled();
  await expect(page.getByRole('button', { name: 'Hourly CSV' })).toBeEnabled();

  const candidateRuns = page.locator('.status-console-entry').filter({ hasText: 'Sending candidate run' });
  const runCount = await candidateRuns.count();
  await page.getByLabel('Temperature response').fill('0.55');
  await expect.poll(() => candidateRuns.count()).toBe(runCount + 1);
  await page.getByLabel('Temperature response').fill('0.6');
  await expect.poll(() => candidateRuns.count()).toBe(runCount + 2);
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });
  const tuned = await page.locator('.summary article').first().locator('strong').textContent();
  expect(tuned).not.toBe(first);
  expect(await page.locator('.summary article').nth(1).locator('strong').textContent()).toBe(baselineHash);

  await page.getByRole('button', { name: 'Run weather comparison' }).click();
  await expect(page.getByRole('button', { name: 'Run weather comparison' })).toBeDisabled();
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });
  await expect(page.getByRole('log', { name: 'Weather preparation activity' })).toContainText('Reusing cached climate and observation artifacts');
  expect(await page.locator('.summary article').first().locator('strong').textContent()).toBe(tuned);
});

test('cancels preparation even when cancellation races the create response', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByRole('checkbox', { name: 'Override elevation' }).check();
  await page.getByLabel('Elevation metres').fill('428');
  await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByRole('button', { name: 'Run weather comparison' }).click();
  await page.getByRole('button', { name: 'Cancel' }).click();
  await expect(page.locator('.status > span').filter({ hasText: /^Cancelled$/ })).toBeVisible();
  await expect(page.getByRole('log', { name: 'Weather preparation activity' })).toContainText('Cancellation requested');
  await expect(page.getByRole('button', { name: 'Run weather comparison' })).toBeEnabled();
});

test('imports, resets, repins, and downloads a completed comparison', async ({ page }) => {
  await page.goto('/');
  await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByRole('button', { name: 'Run weather comparison' }).click();
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });

  for (const name of ['Export tuning', 'Export JSON', 'Daily CSV', 'Hourly CSV']) {
    const [download] = await Promise.all([
      page.waitForEvent('download'),
      page.getByRole('button', { name }).click(),
    ]);
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
  const candidateRuns = page.locator('.status-console-entry').filter({ hasText: 'Sending candidate run' });
  let candidateCount = await candidateRuns.count();
  await page.locator('input[type="file"]').setInputFiles({
    name: 'tuning.json', mimeType: 'application/json', buffer: Buffer.from(JSON.stringify(imported)),
  });
  await expect(page.getByText('Imported tuning "playwright-import"')).toBeVisible();
  await expect.poll(() => candidateRuns.count()).toBe(candidateCount + 1);
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });

  candidateCount = await candidateRuns.count();
  await page.getByRole('button', { name: 'Reset to smoothed' }).click();
  await expect.poll(() => candidateRuns.count()).toBe(candidateCount + 1);
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });

  const baselineRuns = page.locator('.status-console-entry').filter({ hasText: 'Sending baseline run' });
  const baselineCount = await baselineRuns.count();
  await page.getByRole('button', { name: 'Re-pin historical baseline' }).click();
  await expect.poll(() => baselineRuns.count()).toBe(baselineCount + 1);
  await expect(page.locator('.status > span').filter({ hasText: /^Completed$/ })).toBeVisible({ timeout: 30_000 });
});

test('coordinate fields move the display map and unsupported coverage blocks running', async ({ page }) => {
  await page.goto('/'); await expect(page.getByText('Location ready')).toBeVisible();
  await page.getByLabel('Latitude').fill('0'); await page.getByLabel('Longitude').fill('0');
  await expect(page.getByLabel('Map showing 0, 0')).toBeVisible();
  await expect(page.getByText(/Fixture mode contains only/)).toBeVisible();
  await expect(page.getByRole('button', { name: 'Run weather comparison' })).toBeDisabled();
});
