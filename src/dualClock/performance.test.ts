import { it, expect } from 'vitest';
import { cpus, totalmem } from 'node:os';
import { mkdirSync, writeFileSync } from 'node:fs';
import { DualClockEngine } from './engine';
import { dualFixture, fixtureTerrain, fixtureHour } from './fixtures';
import { defaultDualAmenities } from './amenities';

it.runIf(process.env.DUAL_CLOCK_BENCHMARK === '1')('measures prepared-weather aggregate advances including snow', () => {
  const input = dualFixture(10000, 512);
  input.snow!.bounds = { west: -120.02, east: -119.98, south: 44.98, north: 45.03 };
  input.terrain = fixtureTerrain(input);
  input.resort.amenities = defaultDualAmenities(input.resort.portal!.nodeId);
  const start = Date.parse(input.at);
  input.weather = Array.from({ length: 168 * 24 + 1 }, (_, i) => fixtureHour(new Date(start + i * 3600000).toISOString(), -5, i % 24 === 0 ? 1 : 0));
  const began = performance.now(), engine = new DualClockEngine(input), initialized = performance.now();
  engine.beginAdvance({ destination: 'week', target: new Date(start + 7 * 86400000).toISOString() });
  const week = engine.advanceTo(start + 7 * 86400000, true); while (!week.next().done) { /* domain CPU benchmark */ }
  const weekDone = performance.now();
  engine.beginAdvance({ destination: 'season', target: engine.state.clock.winterEnd });
  const winter = engine.advanceTo(Date.parse(engine.state.clock.winterEnd), true); while (!winter.next().done) { /* domain CPU benchmark */ }
  const winterDone = performance.now();
  const metrics = { cpu: cpus()[0].model, ramGiB: totalmem() / 2 ** 30, node: process.version,
    grid: '512x512', attendancePerDay: 10000, lifts: 1, trails: 1, amenityServices: 2, initializationMs: initialized - began, weekMs: weekDone - initialized,
    winterMs: winterDone - initialized, admitted: engine.state.flow.admitted, retainedGuests: engine.state.guests.length,
    cohorts: engine.state.cohorts.length, heapMiB: process.memoryUsage().heapUsed / 2 ** 20 };
  mkdirSync('test-results/dual-clock', { recursive: true });
  writeFileSync('test-results/dual-clock/domain-performance.json', JSON.stringify(metrics, null, 2));
  expect(metrics.weekMs).toBeLessThan(5000);
  expect(metrics.winterMs).toBeLessThan(60000);
  expect(engine.state.flow.admitted).toBe(engine.state.flow.active + engine.state.flow.departed);
}, 180000);
