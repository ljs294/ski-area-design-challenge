import { mkdirSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { expect, type Page } from '@playwright/test';

export type IntegratedFailureStage = 'launch' | 'fixture-installation' | 'boot' | 'source-readiness'
  | 'workload-readiness' | 'interaction' | 'measurement' | 'teardown';

export async function waitForTerminalLoadingOverlay(page: Page, timeout: number): Promise<void> {
  await expect.poll(async () => {
    const overlay = page.locator('.resort-loading');
    return overlay.evaluateAll(elements => elements.length === 0 || elements.every(element =>
      element.classList.contains('resort-loading-done') && getComputedStyle(element).pointerEvents === 'none'));
  }, { timeout, message: 'loading overlay must be absent or completed and non-intercepting' }).toBe(true);
}

export async function restoreSelectedCheckpoint(page: Page, timeout: number): Promise<void> {
  await page.reload({ waitUntil: 'load' });
  await page.getByRole('button', { name: /^Continue / }).click();
  await waitForTerminalLoadingOverlay(page, timeout);
  await expect(page.locator('.maplibregl-canvas')).toBeVisible({ timeout: 30_000 });
}

export async function waitForMapSourceReadiness(page: Page, requireImagery: boolean, timeout: number): Promise<void> {
  await expect.poll(() => page.evaluate(imagery => {
    const map = (globalThis as typeof globalThis & { appMap?: { getSource(id: string): unknown;
      isSourceLoaded(id: string): boolean; loaded(): boolean } }).appMap;
    return !!map && !!map.getSource('terrain-dem') && map.isSourceLoaded('terrain-dem')
      && !!map.getSource('snow') && map.isSourceLoaded('snow')
      && (!imagery || (!!map.getSource('satellite') && map.isSourceLoaded('satellite'))) && map.loaded();
  }, requireImagery), { timeout, message: 'terrain, snow, imagery, and map sources must be independently ready' }).toBe(true);
  await page.evaluate(() => new Promise<void>((resolve, reject) => {
    const map = (globalThis as typeof globalThis & { appMap?: { once(type: string, callback: () => void): void;
      triggerRepaint(): void } }).appMap;
    if (!map) { reject(new Error('Map runtime is unavailable.')); return; }
    map.once('render', resolve); map.triggerRepaint();
  }));
}

export function retainIntegratedFailure(resultsDir: string, runId: string, stage: IntegratedFailureStage,
  error: unknown, observations: Record<string, unknown>): string {
  mkdirSync(resultsDir, { recursive: true });
  const output = path.join(resultsDir, `${runId}.${stage}.failure.json`);
  const shaped = error && typeof error === 'object' ? error as { message?: unknown; stack?: unknown; cause?: unknown } : null;
  const message = error instanceof Error ? error.message : typeof shaped?.message === 'string' ? shaped.message : String(error);
  const errorDetail = shaped ? { ...(typeof shaped.stack === 'string' ? { stack: shaped.stack } : {}),
    ...(shaped.cause === undefined ? {} : { cause: String(shaped.cause) }) } : undefined;
  writeFileSync(output, `${JSON.stringify({ stage, error: message, ...(errorDetail ? { errorDetail } : {}),
    observations, recordedAt: new Date().toISOString() }, null, 2)}\n`);
  return output;
}

export async function runIntegratedStage<T>(resultsDir: string, runId: string, stage: IntegratedFailureStage,
  observations: Record<string, unknown>, action: () => Promise<T>): Promise<T> {
  try { return await action(); }
  catch (error) { retainIntegratedFailure(resultsDir, runId, stage, error, observations); throw error; }
}
