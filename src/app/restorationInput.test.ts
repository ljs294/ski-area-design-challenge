import { describe, expect, it } from 'vitest';
import { buildSkiNetwork } from '../network';
import { defaultDualAmenities } from '../dualClock/amenities';
import { dualFixture, fixtureTerrain } from '../dualClock/fixtures';
import { canonicalResortSimulationInput, resortSimulationInputKey } from './resortSimulationInput';
import { restorationSnowInput } from './useResortSimulation';

describe('restoration input boundaries', () => {
  it('builds identical canonical initial and live resort inputs', () => {
    const fixture = dualFixture();
    const initial = canonicalResortSimulationInput({ ...fixture.resort,
      amenities: defaultDualAmenities(fixture.resort.portal?.nodeId ?? null) });
    const live = canonicalResortSimulationInput({
      revision: fixture.resort.revision, edges: fixture.resort.edges, trails: fixture.resort.trails,
      portal: fixture.resort.portal, ticketPriceCents: fixture.resort.ticketPriceCents,
      amenities: defaultDualAmenities(fixture.resort.portal?.nodeId ?? null),
    });
    expect(initial.dailyDemandByWeekday).toEqual([1300, 900, 900, 900, 900, 900, 1300]);
    expect(resortSimulationInputKey(initial)).toBe(resortSimulationInputKey(live));
    expect(initial).toEqual(live);
    expect(buildSkiNetwork([], [], { nodes: [], paths: [], junctions: [] }).edges).toEqual([]);
  });

  it('uses checkpoint snow exactly once and falls back to bare snow only when absent', () => {
    const fixture = dualFixture(), terrain = fixtureTerrain(fixture);
    expect(restorationSnowInput(terrain, fixture.snow)).toBeNull();
    expect(restorationSnowInput(terrain, null)).not.toBeNull();
  });
});
