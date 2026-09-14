import { describe, expect, it } from 'vitest';
import { DualClockEngine } from './engine';
import { dualFixture } from './fixtures';

describe('dual-clock restoration inputs and geometry reuse', () => {
  it('refreshes operational resort state without rebuilding equivalent geometry', () => {
    const fixture = dualFixture(), engine = new DualClockEngine(fixture);
    const geometryRevision = engine.geometryRevision;
    const preparedLift = Object.values(engine.geometry())[0];
    const operational = structuredClone(fixture.resort);
    operational.ticketPriceCents = 12_500;
    operational.dailyDemand = 2_000;
    operational.edges = operational.edges.map(edge => edge.kind === 'lift' ? { ...edge, liftName: 'Renamed Lift' } : edge);
    engine.setResort(operational);
    expect(engine.geometryRevision).toBe(geometryRevision);
    expect(engine.state.nextTicketPriceCents).toBe(12_500);
    expect(engine.state.resortRevision).toBe(operational.revision);
    expect(Object.values(engine.geometry())[0]).toBe(preparedLift);
  });

  it.each([
    ['path', (fixture: ReturnType<typeof dualFixture>) => {
      const edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
      const path = edge.path as [number, number][];
      const mid: [number, number] = [(path[0]![0] + path[1]![0]) / 2 + 0.000001, (path[0]![1] + path[1]![1]) / 2];
      return { ...fixture.resort, edges: fixture.resort.edges.map(candidate => candidate.id === edge.id
        ? { ...candidate, path: [path[0], mid, path[1]] } : candidate) };
    }],
    ['trail geometry', (fixture: ReturnType<typeof dualFixture>) => ({ ...fixture.resort,
      trails: fixture.resort.trails.map(trail => ({ ...trail, brushWidthM: trail.brushWidthM + 1 })) })],
    ['snow width', (fixture: ReturnType<typeof dualFixture>) => fixture.resort],
    ['snow height', (fixture: ReturnType<typeof dualFixture>) => fixture.resort],
    ['snow bounds', (fixture: ReturnType<typeof dualFixture>) => fixture.resort],
  ])('rebuilds when %s geometry changes with a colliding revision', (_name, change) => {
    const fixture = dualFixture(), engine = new DualClockEngine(fixture), before = engine.geometryRevision;
    if (_name === 'snow width' || _name === 'snow height' || _name === 'snow bounds') {
      const snow = engine.snow!;
      engine.snow = _name === 'snow width' ? { ...snow, width: snow.width + 1 }
        : _name === 'snow height' ? { ...snow, height: snow.height + 1 }
        : { ...snow, bounds: { ...snow.bounds, east: snow.bounds.east + 0.000001 } };
    }
    engine.setResort(change(fixture) as typeof fixture.resort);
    expect(engine.geometryRevision).toBe(before + 1);
  });

});
