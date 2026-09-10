import { describe, expect, it } from 'vitest';
import { applyTrafficWear, thinTrailSignal } from './wear';
import { prepareFootprint, snowCellArea } from './geometry';
import { dualFixture, fixtureHour, fixtureTerrain } from './fixtures';
import { createNaturalSnowStepper, stepNaturalSnow } from '../snowSimulation';

describe('traffic wear', () => {
  it('spreads the same skier-distance over wider trails without creating extra loss', () => {
    const outputs = [1, 2].map(widthScale => {
      const input = dualFixture(), grid = input.snow!, trail = input.resort.trails[0], west = grid.bounds.west;
      grid.bounds.east = west + (grid.bounds.east - west) * widthScale;
      trail.parts[0].polygon = trail.parts[0].polygon.map(ring => ring.map(p => [west + (p[0] - west) * widthScale, p[1]]));
      const footprint = prepareFootprint(input.resort.edges[1], trail, grid)!;
      const result = applyTrafficWear(grid, new Float64Array(grid.depthM.length), footprint, 1e6);
      return { volume: result.trafficLossM3, depth: 0.5 - grid.depthM[10] };
    });
    expect(outputs[0].volume).toBeCloseTo(outputs[1].volume, 4);
    expect(outputs[0].depth).toBeCloseTo(outputs[1].depth * 2, 6);
  });
  it('preserves allocated distance for partial cells and resets exposure after fresh snow', () => {
    const input = dualFixture(), grid = input.snow!, trail = input.resort.trails[0], b = grid.bounds;
    trail.parts[0].polygon = trail.parts[0].polygon.map(ring => ring.map(p => [b.west + (p[0] - b.west) * 0.47, p[1]]));
    const footprint = prepareFootprint(input.resort.edges[1], trail, grid)!, exposure = new Float64Array(grid.depthM.length);
    const result = applyTrafficWear(grid, exposure, footprint, 1e6);
    expect(result.trafficLossM3).toBeCloseTo(150, 3);
    expect(exposure.reduce((sum, value) => sum + value * snowCellArea(grid) / 30, 0)).toBeCloseTo(1e6, 3);
    const step = createNaturalSnowStepper(fixtureTerrain(input), grid)(grid, fixtureHour(input.at, -5, 5), exposure);
    while (!step.next().done) { /* finish the resumable weather hour */ }
    expect(exposure.every(value => value === 0)).toBe(true);
  });
  it('loses five millimeters per thousand reference passages and hardens dry snow', () => {
    const input = dualFixture(), grid = input.snow!, fp = prepareFootprint(input.resort.edges[1], input.resort.trails[0], grid)!;
    const exposure = new Float64Array(grid.depthM.length);
    applyTrafficWear(grid, exposure, fp, 1000 * 1000);
    expect(grid.depthM[10]).toBeCloseTo(0.495, 6);
    expect(grid.surface[10]).toBe(4);
    expect(exposure[10]).toBeCloseTo(1000, 1);
  });
  it('conserves modeled loss across grid resolutions and does not improve ice', () => {
    const losses = [4, 8, 16].map(n => {
      const input = dualFixture(1000, n), grid = input.snow!;
      grid.surface.fill(5);
      const result = applyTrafficWear(grid, new Float64Array(n * n), prepareFootprint(input.resort.edges[1], input.resort.trails[0], grid)!, 1e6);
      expect([...grid.surface].every(s => s === 5)).toBe(true);
      return result.trafficLossM3;
    });
    expect(Math.max(...losses) - Math.min(...losses)).toBeLessThan(0.01);
  });
  it('bounds depth loss, reports trace cutoff separately, and uses warning hysteresis', () => {
    const input = dualFixture(), grid = input.snow!, fp = prepareFootprint(input.resort.edges[1], input.resort.trails[0], grid)!;
    grid.depthM.fill(0.021);
    const result = applyTrafficWear(grid, new Float64Array(grid.depthM.length), fp, 1e6);
    expect(Math.min(...grid.depthM)).toBe(0); expect(result.cutoffLossM3).toBeGreaterThan(0);
    expect(result.trafficLossM3 + result.cutoffLossM3).toBeCloseTo(0.021 * snowCellArea(grid) * grid.depthM.length, 2);
    const warning = thinTrailSignal(grid, fp, input.at)!;
    expect(warning.severity).toBe('advisory');
    grid.depthM.fill(0.5);
    expect(thinTrailSignal(grid, fp, input.at, warning)?.resolved).toBe(true);
  });
  it('resumable cached weather stepping matches the characterized natural snow model', () => {
    const input = dualFixture(), grid = input.snow!, terrain = fixtureTerrain(input);
    for (const hour of [fixtureHour(input.at, -5, 5), fixtureHour(input.at, 3, 2), fixtureHour(input.at, -8, 0)]) {
      const expected = stepNaturalSnow(grid, terrain, [hour]).grid;
      const iterator = createNaturalSnowStepper(terrain, grid)(grid, hour, new Float64Array(grid.depthM.length));
      let result = iterator.next(); while (!result.done) result = iterator.next();
      result.value.depthM.forEach((depth, i) => expect(depth).toBeCloseTo(expected.depthM[i], 6));
      expect(result.value.surface).toEqual(expected.surface);
    }
  });
});
