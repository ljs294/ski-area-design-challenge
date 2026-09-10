import { expect, it } from 'vitest';
import { dualFixture } from '../dualClock/fixtures';
import { DualSnowPublisher, applyDualSnowPatch } from './dualSnowPublication';

it('publishes absolute dirty cells and invalidates the baseline after a superseded reply', () => {
  const grid = dualFixture().snow!, publisher = new DualSnowPublisher();
  const initial = publisher.frame(grid).snow!;
  expect(publisher.frame(grid)).toEqual({});
  grid.depthM[10] -= 0.00001; grid.surface[10] = 2;
  const patch = publisher.frame(grid).snowPatch!;
  expect(patch.width * patch.height).toBe(1);
  expect(applyDualSnowPatch(initial, patch)).toEqual(grid);
  expect(initial.surface[10]).toBe(1);
  publisher.invalidate(); expect(publisher.frame(grid).snow).toEqual(grid);
});
