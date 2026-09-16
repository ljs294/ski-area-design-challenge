import { afterEach, describe, expect, test, vi } from 'vitest';
import { mkdirSync, readFileSync, rmSync } from 'node:fs';
import path from 'node:path';
import { retainIntegratedFailure, runIntegratedStage, waitForTerminalLoadingOverlay } from './integratedRuntime';

afterEach(() => vi.restoreAllMocks());

describe('integrated runtime finalization', () => {
  test.each([
    ['absent', []],
    ['completed', [{ classList: { contains: () => true } }]],
  ])('accepts an %s terminal loading overlay', async (_name, elements) => {
    const evaluateAll = vi.fn(async (callback: (values: typeof elements) => boolean) => {
      vi.stubGlobal('getComputedStyle', () => ({ pointerEvents: 'none' }));
      return callback(elements);
    });
    const page = { locator: () => ({ evaluateAll }) };
    await waitForTerminalLoadingOverlay(page as never, 50);
    expect(evaluateAll).toHaveBeenCalled();
  });

  test('retains the named failure stage and observations', () => {
    const directory = path.resolve('test-results/integrated-benchmark/runtime-helper-test');
    mkdirSync(directory, { recursive: true });
    try {
      const file = retainIntegratedFailure(directory, 'installation-failure', 'fixture-installation',
        new Error('fixture rejected'), { fixture: 'jackson' });
      expect(JSON.parse(readFileSync(file, 'utf8'))).toMatchObject({ stage: 'fixture-installation',
        error: 'fixture rejected', observations: { fixture: 'jackson' } });
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });

  test('retains installation failure evidence when the installation action rejects', async () => {
    const directory = path.resolve('test-results/integrated-benchmark/runtime-install-failure-test');
    mkdirSync(directory, { recursive: true });
    try {
      await expect(runIntegratedStage(directory, 'install', 'fixture-installation', { fixture: 'jackson' },
        async () => { throw new Error('fixture integrity failed'); })).rejects.toThrow('fixture integrity failed');
      expect(JSON.parse(readFileSync(path.join(directory, 'install.fixture-installation.failure.json'), 'utf8'))).toMatchObject({
        stage: 'fixture-installation', error: 'fixture integrity failed', observations: { fixture: 'jackson' } });
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });

  test('retains primary and teardown failures as separate artifacts', () => {
    const directory = path.resolve('test-results/integrated-benchmark/runtime-multiple-failure-test');
    mkdirSync(directory, { recursive: true });
    try {
      retainIntegratedFailure(directory, 'run', 'measurement', new Error('primary'), {});
      retainIntegratedFailure(directory, 'run', 'teardown', new Error('close'), {});
      expect(JSON.parse(readFileSync(path.join(directory, 'run.measurement.failure.json'), 'utf8')).error).toBe('primary');
      expect(JSON.parse(readFileSync(path.join(directory, 'run.teardown.failure.json'), 'utf8')).error).toBe('close');
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });

  test('preserves Playwright TestError-shaped message and stack fields', () => {
    const directory = path.resolve('test-results/integrated-benchmark/runtime-shaped-error-test');
    mkdirSync(directory, { recursive: true });
    try {
      const file = retainIntegratedFailure(directory, 'run', 'boot', { message: 'overlay stalled', stack: 'stack-lines' }, {});
      expect(JSON.parse(readFileSync(file, 'utf8'))).toMatchObject({ error: 'overlay stalled',
        errorDetail: { stack: 'stack-lines' } });
    } finally { rmSync(directory, { recursive: true, force: true }); }
  });
});
