import { expect, it } from 'vitest';
import { DualClockEngine } from './engine';
import { advanceDestination } from './clock';
import { dualFixture } from './fixtures';

it.runIf(process.env.DUAL_CLOCK_SOAK === '1')('bounds retained visit, event and ledger state across three winters', () => {
  const engine = new DualClockEngine(dualFixture(300));
  const retainedBytes: number[] = [];
  const finish = () => {
    const work = engine.advanceTo(Date.parse(engine.state.advance!.request.target), true);
    while (!work.next().done) { /* opt-in long-run retention verification */ }
  };
  for (let winter = 0; winter < 3; winter++) {
    if (winter) { engine.beginAdvance({ destination: 'winter', target: advanceDestination(engine.state.clock, 'winter') }); finish(); }
    engine.select(null, true);
    engine.beginAdvance({ destination: 'season', target: engine.state.clock.winterEnd }); finish();
    const checkpoint = engine.checkpoint();
    expect(checkpoint.flow.active + checkpoint.flow.departed).toBe(checkpoint.flow.admitted);
    expect(checkpoint.cohorts).toHaveLength(0);
    expect(checkpoint.guests.length).toBeLessThanOrEqual(4);
    expect(checkpoint.guests.every(guest => guest.history.length <= 128)).toBe(true);
    expect(checkpoint.dailyLedgers.length).toBeLessThanOrEqual(366);
    expect(checkpoint.history.length).toBeLessThanOrEqual(256);
    expect(checkpoint.transitRoutes).toHaveLength(0);
    retainedBytes.push(JSON.stringify(checkpoint).length);
  }
  // The second and third years both have a full bounded ledger window.
  expect(retainedBytes[2]).toBeLessThan(retainedBytes[1] * 1.1);
}, 180000);
