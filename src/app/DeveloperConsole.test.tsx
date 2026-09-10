import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { advanceSummerToSeptember, createClock } from '../../time-engine/src/timeEngine';
import { addSnowConsoleLines, DeveloperConsole } from './DeveloperConsole';

describe('DeveloperConsole', () => {
  it('waits for an authoritative snow commit before producing success output', async () => {
    let resolve: ((value: { requestedMeters: number; affectedCells: number; clippedCells: number }) => void) | undefined;
    const pending = addSnowConsoleLines(0.5, () => new Promise((done) => { resolve = done; }));
    let settled = false; void pending.then(() => { settled = true; });
    await Promise.resolve();
    expect(settled).toBe(false);
    resolve!({ requestedMeters: 0.5, affectedCells: 24, clippedCells: 3 });
    await expect(pending).resolves.toEqual([
      'Added 50 cm of fresh snow across 24 terrain cells.',
      '3 cells reached the snow-depth limit. The simulation is paused; game time did not advance.',
    ]);
  });

  it('surfaces unavailable or rejected snow controls without a success line', async () => {
    await expect(addSnowConsoleLines(0.5, undefined)).rejects.toThrow('Snow controls are not ready.');
    await expect(addSnowConsoleLines(0.5, async () => { throw new Error('Cancel advance first.'); }))
      .rejects.toThrow('Cancel advance first.');
  });

  it('exposes a development-only in-game console affordance', () => {
    const clock = advanceSummerToSeptember(createClock()).clock;
    const html = renderToStaticMarkup(<DeveloperConsole clock={clock} skip={vi.fn()} />);
    expect(html).toContain('&gt;_ DEV');
    expect(html).toContain('Open developer console (` or F10)');
  });
});
