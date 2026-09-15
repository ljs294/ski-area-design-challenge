import { chromium } from '@playwright/test';
import { mkdir, writeFile } from 'node:fs/promises';
import path from 'node:path';
import process from 'node:process';
import { fileURLToPath } from 'node:url';
import { preview } from 'vite';
import { diagnosticArtifactErrors, startIntegratedDiagnosticProfile } from './integratedDiagnosticProfiler.mjs';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..');
const outputDir = path.join(root, 'test-results', 'integrated-benchmark', 'collector-smoke');
const runId = `collector-smoke-${new Date().toISOString().replaceAll(/[:.]/g, '-')}`;

async function closePreview(server) {
  if (!server) return;
  server.httpServer.closeAllConnections?.();
  if (server.httpServer.listening) await new Promise((resolve, reject) =>
    server.httpServer.close(error => error ? reject(error) : resolve()));
}

async function main() {
  await mkdir(outputDir, { recursive: true });
  let server, browser, stopProfile;
  try {
    server = await preview({ configFile: path.join(root, 'vite.config.integratedProfiling.ts'), root,
      preview: { host: '127.0.0.1', port: 44584, strictPort: true } });
    browser = await chromium.launch({ headless: false, args: ['--enable-precise-memory-info', '--js-flags=--expose-gc'] });
    const page = await browser.newPage({ viewport: { width: 1280, height: 720 }, deviceScaleFactor: 1 });
    await page.goto('http://127.0.0.1:44584/ski-area-design-challenge/', { waitUntil: 'load' });
    await page.getByRole('navigation', { name: 'Main menu' }).waitFor();
    stopProfile = await startIntegratedDiagnosticProfile(page, { outputDir, runId });
    await page.getByRole('button', { name: 'Settings', exact: true }).click();
    await page.waitForTimeout(100);
    await page.keyboard.press('Escape');
    await page.getByRole('button', { name: 'New Resort', exact: true }).click();
    await page.waitForTimeout(100);
    const profile = await stopProfile(); stopProfile = undefined;
    const errors = diagnosticArtifactErrors(profile.manifest.artifacts);
    if (errors.length) throw new Error(errors.join(' '));
    const result = { schemaVersion: 1, kind: 'collector-smoke', qualification: false, runId,
      diagnosticManifest: profile.manifestFile, artifacts: profile.manifest.artifacts, manifestArtifact: profile.artifact };
    const resultFile = path.join(outputDir, `${runId}.collector-smoke.json`);
    await writeFile(resultFile, `${JSON.stringify(result, null, 2)}\n`, 'utf8');
    process.stdout.write(`${resultFile}\n`);
  } finally {
    if (stopProfile) await stopProfile().catch(() => undefined);
    await browser?.close().catch(() => undefined);
    await closePreview(server).catch(() => undefined);
  }
}

main().catch(error => { process.stderr.write(`${error instanceof Error ? error.stack ?? error.message : String(error)}\n`); process.exitCode = 1; });
