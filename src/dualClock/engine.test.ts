import { describe, expect, it } from 'vitest';
import { DualClockEngine, validateDualCheckpoint } from './engine';
import { dualFixture, fixtureTerrain, fixtureHour } from './fixtures';
import { DUAL_SPEEDS } from './model';
import { defaultDualAmenities } from './amenities';
import { insidePolygon, prepareRoute, routePosition } from './geometry';
import { MAX_SNOW_DEPTH_M } from './snowAdd';

function advance(engine: DualClockEngine, target: number, headless = false) {
  const iterator = engine.advanceTo(target, headless); while (!iterator.next().done) { /* synchronous deterministic test */ }
}
describe('dual-clock runtime', () => {
  it('subdivides accounting at actual travel deadlines without issuing extra lift seats', () => {
    const fixture = dualFixture(1000);
    fixture.resort.edges = fixture.resort.edges.map(edge => edge.kind === 'lift'
      ? { ...edge, rideTimeS: 301, travelTimeS: 301 } : { ...edge, travelTimeS: 377 });
    const engine = new DualClockEngine(fixture), start = Date.parse(fixture.at);
    engine.play(); advance(engine, start + 300000);
    expect(engine.state.cohorts.find(c => c.id === 1)?.edgeId).toBe('lift-edge');
    const boarded = engine.state.flow.queues['lift-edge'].boarded;
    advance(engine, start + 301000);
    expect(engine.state.cohorts.find(c => c.id === 1)?.edgeId).toBe('trail-edge');
    expect(engine.state.flow.queues['lift-edge'].boarded).toBe(boarded);
    advance(engine, start + 677999); expect(engine.state.flow.completedRuns).toBe(0);
    advance(engine, start + 678000); expect(engine.state.flow.completedRuns).toBeGreaterThan(0);
  });
  it('holds guests at the base until a snow-covered return descent is viable', () => {
    const fixture = dualFixture(1000);
    fixture.snow!.depthM.fill(0); fixture.snow!.surface.fill(0);
    const engine = new DualClockEngine(fixture), start = Date.parse(fixture.at);
    engine.play(); advance(engine, start + 5 * 60000);
    expect(engine.state.flow.queues['lift-edge'].boarded).toBe(0);
    expect(engine.state.flow.queues['lift-edge']).toMatchObject({ guests: 0, riders: 0, serviceAvailable: false, serviceUnavailableReason: 'no-descent' });
    expect(engine.state.cohorts.every(cohort => cohort.status === 'choosing' && cohort.nodeId === 'base')).toBe(true);
    expect(engine.state.guests.every(guest => guest.status === 'walking' && guest.nodeId === 'base' && guest.edgeId === null)).toBe(true);
    expect(engine.state.signals.some(signal => signal.id === 'no-descent:lift-edge')).toBe(true);
    const before = { ...engine.state.clock };
    engine.snow!.depthM[0] = MAX_SNOW_DEPTH_M; engine.snow!.surface[0] = 1;
    engine.exposure.fill(3);
    expect(engine.addSnow(0.3)).toMatchObject({ requestedMeters: 0.3, affectedCells: fixture.snow!.depthM.length - 1, clippedCells: 1 });
    expect(engine.state.clock).toEqual(before);
    expect(engine.exposure[0]).toBe(3);
    expect(engine.exposure[1]).toBe(0);
    advance(engine, start + 6 * 60000);
    expect(engine.state.flow.queues['lift-edge'].boarded).toBeGreaterThan(0);
    expect(engine.state.flow.queues['lift-edge'].serviceAvailable).toBe(true);
    expect(engine.state.signals.some(signal => signal.id === 'no-descent:lift-edge')).toBe(false);
  });
  it('admits linked lifts only when their descent chain eventually returns to the portal', () => {
    const fixture = dualFixture(), originalLift = fixture.resort.edges.find(edge => edge.kind === 'lift')!, originalTrail = fixture.resort.edges.find(edge => edge.kind === 'trail')!;
    const base = originalLift.path[0]!, mid: [number, number] = [-121.995, 47.005], upperBase: [number, number] = [-121.99, 47.01], top = originalLift.path[1]!;
    const linkTrail = { ...fixture.resort.trails[0], id: 'link-run', name: 'Link Run' };
    const lowLift = { ...originalLift, id: 'low-lift', from: 'base', to: 'mid', path: [base, mid] as [[number, number], [number, number]] };
    const connector = { ...originalTrail, id: 'connector', from: 'mid', to: 'upper-base', trailId: 'link-run', trailName: 'Link Run', path: [mid, upperBase] as [number, number][] };
    const highLift = { ...originalLift, id: 'high-lift', from: 'upper-base', to: 'top', path: [upperBase, top] as [[number, number], [number, number]] };
    const descent = { ...originalTrail, id: 'descent', from: 'top', to: 'base', path: [top, base] as [number, number][] };
    fixture.resort = { ...fixture.resort, edges: [lowLift, connector, highLift, descent], trails: [fixture.resort.trails[0], linkTrail] };
    const engine = new DualClockEngine(fixture);
    expect(engine.state.flow.queues['low-lift']).toMatchObject({ serviceAvailable: true });
    expect(engine.state.flow.queues['high-lift']).toMatchObject({ serviceAvailable: true });
    const closedLoop = structuredClone(fixture.resort);
    closedLoop.revision++; closedLoop.edges = closedLoop.edges.map(edge => edge.id === 'descent' ? { ...edge, to: 'upper-base' } : edge);
    engine.setResort(closedLoop);
    expect(engine.state.flow.queues['low-lift']).toMatchObject({ serviceAvailable: false, serviceUnavailableReason: 'no-descent' });
    expect(engine.state.flow.queues['high-lift']).toMatchObject({ serviceAvailable: false, serviceUnavailableReason: 'no-descent' });
  });
  it('releases blocked lift queues while in-flight riders retain their journey', () => {
    const fixture = dualFixture(10000), engine = new DualClockEngine(fixture), start = Date.parse(fixture.at);
    engine.play(); advance(engine, start + 60000);
    expect(engine.state.cohorts.some(cohort => cohort.status === 'travel' && cohort.edgeId === 'lift-edge')).toBe(true);
    expect(engine.state.cohorts.some(cohort => cohort.status === 'queue' && cohort.edgeId === 'lift-edge')).toBe(true);
    const resort = structuredClone(fixture.resort);
    const boarded = engine.state.flow.queues['lift-edge'].boarded;
    resort.revision++; resort.edges = resort.edges.map(edge => edge.kind === 'trail' ? { ...edge, open: false } : edge);
    engine.setResort(resort);
    expect(engine.state.cohorts.some(cohort => cohort.status === 'queue')).toBe(false);
    expect(engine.state.flow.queues['lift-edge']).toMatchObject({ guests: 0, waitSeconds: 0, boarded, serviceAvailable: false, serviceUnavailableReason: 'no-descent' });
    expect(engine.state.flow.queues['lift-edge'].riders).toBeGreaterThan(0);
    advance(engine, start + 301000);
    expect(engine.state.cohorts.some(cohort => cohort.nodeId === 'top' && cohort.status === 'choosing')).toBe(true);
  });
  it('rechecks representative queues and preserves an in-flight representative lift journey', () => {
    const fixture = dualFixture(1000);
    fixture.resort.edges = fixture.resort.edges.map(edge => edge.kind === 'lift' ? { ...edge, rideTimeS: 10, travelTimeS: 10 } : edge);
    const start = Date.parse(fixture.at), engine = new DualClockEngine(fixture);
    engine.play(); advance(engine, start + 1000);
    const queued = engine.state.guests[0]!;
    expect(queued.status).toBe('lift-queue');
    const blocked = structuredClone(fixture.resort);
    blocked.revision++; blocked.edges = blocked.edges.map(edge => edge.kind === 'trail' ? { ...edge, open: false } : edge);
    engine.setResort(blocked);
    advance(engine, start + 481000);
    expect(queued).toMatchObject({ status: 'walking', nodeId: 'base', edgeId: null, nextPlan: 'Wait for a suitable route' });

    const ridingEngine = new DualClockEngine(fixture); ridingEngine.play(); advance(ridingEngine, start + 481000);
    const rider = ridingEngine.state.guests[0]!;
    expect(rider).toMatchObject({ status: 'lift-ride', edgeId: 'lift-edge', nodeId: 'base' });
    ridingEngine.setResort(blocked);
    expect(rider).toMatchObject({ status: 'lift-ride', edgeId: 'lift-edge', nodeId: 'base' });
    advance(ridingEngine, start + 900000);
    expect(rider).toMatchObject({ status: 'walking', edgeId: null, nodeId: 'top', nextPlan: 'Wait for a suitable route' });
  });
  it('retains boarding and rider counts when capacity drops to zero or a lift closes', () => {
    const fixture = dualFixture(10000), start = Date.parse(fixture.at), engine = new DualClockEngine(fixture);
    engine.play(); advance(engine, start + 60000);
    const before = { boarded: engine.state.flow.queues['lift-edge'].boarded, riders: engine.state.flow.queues['lift-edge'].riders };
    const capacityZero = structuredClone(fixture.resort);
    capacityZero.revision++; capacityZero.edges = capacityZero.edges.map(edge => edge.kind === 'lift' ? { ...edge, capacityPph: 0 } : edge);
    engine.setResort(capacityZero);
    expect(engine.state.flow.queues['lift-edge']).toMatchObject({ boarded: before.boarded, riders: before.riders, waitSeconds: 0,
      serviceAvailable: false, serviceUnavailableReason: 'capacity' });
    advance(engine, start + 120000);
    expect(engine.state.flow.queues['lift-edge'].boarded).toBe(before.boarded);
    const closed = structuredClone(capacityZero);
    closed.revision++; closed.edges = closed.edges.map(edge => edge.kind === 'lift' ? { ...edge, open: false } : edge);
    engine.setResort(closed);
    expect(engine.state.flow.queues['lift-edge']).toMatchObject({ boarded: before.boarded, riders: before.riders,
      serviceAvailable: false, serviceUnavailableReason: 'closed' });
  });
  it('tracks every repeat lift boarding per local day, rolls over at midnight, and marks legacy checkpoints partial', () => {
    const fixture = dualFixture(1000), engine = new DualClockEngine(fixture), start = Date.parse(fixture.at);
    engine.play(); advance(engine, start + 30 * 60000);
    const queue = engine.state.flow.queues['lift-edge'];
    expect(queue.dailyBoarded).toBe(queue.boarded);
    expect(queue.dailyBoarded).toBeGreaterThan(40);
    const old = engine.checkpoint(); delete old.dailyLiftBoardings;
    const restored = new DualClockEngine({ ...fixture, checkpoint: old });
    expect(restored.state.dailyLiftBoardings).toMatchObject({ accuracy: 'partial', trackingSince: old.clock.at, counts: {} });
    const atMidnight = dualFixture(); atMidnight.at = '2026-11-02T23:59:00.000Z';
    const rollover = new DualClockEngine(atMidnight);
    rollover.state.dailyLiftBoardings!.counts['lift-edge'] = 7;
    const saved = JSON.parse(JSON.stringify(rollover.checkpoint()));
    const resumed = new DualClockEngine({ ...atMidnight, checkpoint: saved });
    resumed.play(); advance(resumed, Date.parse(atMidnight.at) + 60000);
    expect(resumed.state.dailyLiftBoardings).toEqual({ date: '2026-11-03', counts: {}, accuracy: 'complete' });
  });
  it('constrains café spending by stock, access time, service capacity and affordability', () => {
    const fixture = dualFixture(1000);
    const amenity = { ...defaultDualAmenities('base')[0], inventory: 3, capacityPerHour: 60 };
    fixture.resort.amenities = [amenity];
    const engine = new DualClockEngine(fixture); engine.play();
    let foundService = false;
    for (let minute = 1; minute <= 120; minute++) {
      advance(engine, Date.parse(fixture.at) + minute * 60000);
      const sales = 3 - engine.state.amenityInventory[amenity.id];
      expect(engine.state.flow.amenityRevenueCents).toBe(sales * amenity.priceCents);
      expect(sales).toBeLessThanOrEqual(minute);
      for (const cohort of engine.state.cohorts.filter(c => c.status === 'amenity')) {
        foundService = true; expect(cohort.due - cohort.started).toBe(amenity.accessSeconds + amenity.serviceSeconds);
        expect(cohort.nodeId).toBe('base'); expect(cohort.budgetCents).toBe(5000 - amenity.priceCents);
      }
    }
    expect(foundService).toBe(true); expect(engine.state.amenityInventory[amenity.id]).toBe(0);
    const unaffordable = new DualClockEngine({ ...fixture, resort: { ...fixture.resort, amenities: [{ ...amenity, priceCents: 5001 }] } });
    advance(unaffordable, Date.parse(fixture.at) + 2 * 3600000);
    expect(unaffordable.state.flow.amenityRevenueCents).toBe(0);
  });
  it('keeps sample limits and selected-group tracking out of macro accounting', () => {
    const fixture = dualFixture(300), target = Date.parse(fixture.at) + 6 * 3600000;
    const direct = new DualClockEngine(fixture); direct.play(); advance(direct, target, true);
    const sampled = new DualClockEngine(fixture); sampled.state.config.representativeLimit = 12;
    sampled.play(); sampled.select(null, true);
    for (let minute = 1; minute <= 360; minute++) advance(sampled, Date.parse(fixture.at) + minute * 60000);
    expect(sampled.state.flow).toEqual(direct.state.flow);
    expect(sampled.snow).toEqual(direct.snow); expect(sampled.exposure).toEqual(direct.exposure);
    expect(sampled.state.guests.length).toBeLessThanOrEqual(13);
  });
  it('keeps admissions, capacity, traffic and money invariant across all presets and headless mode', () => {
    const fixture = dualFixture(300), target = Date.parse(fixture.at) + 2 * 3600000;
    const outputs = DUAL_SPEEDS.map(speed => {
      const engine = new DualClockEngine(fixture); engine.setSpeed(speed); engine.play(); advance(engine, target, speed === 64);
      expect(engine.state.flow.admitted).toBe(engine.state.flow.active + engine.state.flow.departed);
      expect(engine.state.flow.ticketRevenueCents).toBe(engine.state.flow.admitted * 10000);
      expect(engine.state.flow.queues['lift-edge'].boarded).toBeLessThanOrEqual(4800);
      return { flow: engine.state.flow, depth: engine.snow!.depthM, exposure: engine.exposure };
    });
    for (const output of outputs.slice(1)) expect(output).toEqual(outputs[0]);
  });
  it('preserves sub-centimeter depth and residual credits across frequent JSON checkpoints', () => {
    const fixture = dualFixture(100), target = Date.parse(fixture.at) + 3600000;
    const direct = new DualClockEngine(fixture); advance(direct, target);
    let resumed = new DualClockEngine(fixture);
    for (let time = Date.parse(fixture.at) + 60000; time <= target; time += 60000) {
      advance(resumed, time);
      const checkpoint = JSON.parse(JSON.stringify(resumed.checkpoint())); validateDualCheckpoint(checkpoint);
      resumed = new DualClockEngine({ ...fixture, checkpoint });
    }
    expect(resumed.snow).toEqual(direct.snow);
    expect(resumed.exposure).toEqual(direct.exposure);
    expect(resumed.state.flow).toEqual(direct.state.flow);
    expect(resumed.state.dailyLiftBoardings).toEqual(direct.state.dailyLiftBoardings);
  });
  it('retains a completed selected visit and ignores advisory warnings for playback', () => {
    const fixture = dualFixture(100), engine = new DualClockEngine(fixture);
    advance(engine, Date.parse(fixture.at) + 60 * 60000);
    const guest = engine.state.guests[0]; expect(guest).toBeDefined();
    engine.select(guest.id); engine.play();
    advance(engine, Date.parse(fixture.at) + 16 * 3600000, true);
    expect(engine.publication().selected?.status).toBe('departed');
    engine.signal({ id: 'thin-test', entityId: 'run', entityKind: 'trail', title: 'Thin', message: 'Thin snow',
      at: engine.state.clock.at, acknowledged: false, resolved: false, severity: 'advisory' });
    expect(engine.state.clock.paused).toBe(false);
    engine.signal({ id: 'critical', entityId: 'lift', entityKind: 'lift', title: 'Held', message: 'Held',
      at: engine.state.clock.at, acknowledged: false, resolved: false, severity: 'critical' });
    expect(engine.state.clock.paused).toBe(true);
  });
  it('uses the authored trail centerline for every guest', () => {
    const fixture = dualFixture(), edge = fixture.resort.edges[1], trail = fixture.resort.trails[0];
    const route = prepareRoute(edge, trail), positions = new Set<number>();
    for (let guest = 0; guest < 100; guest++) for (let step = 0; step <= 100; step++) {
      const point = routePosition(route, step / 100, `guest-${guest}`);
      expect(insidePolygon(point, trail.parts[0].polygon)).toBe(true);
      if (step === 50) positions.add(point[0]);
    }
    expect(positions.size).toBe(1);
  });
  it('keeps a route when a normal trail centerline terminates on its outer polygon boundary', () => {
    const fixture = dualFixture(), edge = fixture.resort.edges[1], trail = fixture.resort.trails[0];
    const [top, base] = edge.path as [[number, number], [number, number]], halfWidth = 0.0002;
    trail.parts[0].polygon = [[
      [top[0] - halfWidth, top[1]], [top[0] + halfWidth, top[1]],
      [base[0] + halfWidth, base[1]], [base[0] - halfWidth, base[1]], [top[0] - halfWidth, top[1]],
    ]];
    const route = prepareRoute(edge, trail);
    expect(route.length).toBeGreaterThan(900);
    expect(routePosition(route, 0.5, 'boundary-terminal')[1]).toBeGreaterThan(base[1]);
  });
  it('hides a centerline that crosses a polygon hole', () => {
    const fixture = dualFixture(), edge = fixture.resort.edges[1], trail = fixture.resort.trails[0], b = fixture.snow!.bounds;
    const dx = b.east - b.west, dy = b.north - b.south;
    trail.parts[0].polygon.push([[b.west + dx * 0.35, b.south + dy * 0.45], [b.west + dx * 0.65, b.south + dy * 0.45],
      [b.west + dx * 0.65, b.south + dy * 0.55], [b.west + dx * 0.35, b.south + dy * 0.55], [b.west + dx * 0.35, b.south + dy * 0.45]]);
    const route = prepareRoute(edge, trail);
    expect(route.length).toBe(0);
  });
  it('cancels staged weather without publishing half a snow grid or advancing either clock', () => {
    const fixture = dualFixture(100, 128); fixture.at = '2026-11-02T08:59:30.000Z';
    fixture.terrain = fixtureTerrain(fixture); fixture.weather = [fixtureHour('2026-11-02T08:00:00.000Z', -5, 1)];
    const engine = new DualClockEngine(fixture), before = engine.checkpoint();
    const target = '2026-11-02T09:02:00.000Z'; engine.beginAdvance({ destination: 'day', target });
    const work = engine.advanceTo(Date.parse(target), true); expect(work.next().done).toBe(false);
    work.return(); engine.cancelAdvance();
    expect(engine.state.clock.at).toBe(before.clock.at);
    expect(engine.state.clock.microSecond).toBe(before.clock.microSecond);
    expect(engine.checkpoint().snow).toEqual(before.snow);
    expect(engine.state.clock.paused).toBe(true);
  });
  it('requires explicit resume after resolving an interrupt and retains its destination', () => {
    const fixture = dualFixture(100), engine = new DualClockEngine(fixture), target = new Date(Date.parse(fixture.at) + 3600000).toISOString();
    engine.beginAdvance({ destination: 'day', target });
    const work = engine.advanceTo(Date.parse(target), true); work.next(); work.return();
    const signal = { id: 'critical', entityId: 'lift', entityKind: 'lift' as const, title: 'Hold', message: 'Hold',
      at: engine.state.clock.at, severity: 'critical' as const, acknowledged: false, resolved: false };
    engine.signal(signal); engine.signal({ ...signal, resolved: true });
    expect(engine.state.advance).toMatchObject({ request: { target }, state: 'suspended' });
    expect(engine.state.clock.paused).toBe(true);
    engine.resumeAdvance(); advance(engine, Date.parse(target), true);
    expect(engine.state.advance?.state).toBe('completed'); expect(engine.state.clock.paused).toBe(true);
  });
  it('promotes an aggregate guest immediately while paused without changing resort outcomes', () => {
    const fixture = dualFixture(100), engine = new DualClockEngine(fixture);
    engine.setSpeed(8); advance(engine, Date.parse(fixture.at) + 3600000, true);
    const before = structuredClone(engine.state.flow), clock = { ...engine.state.clock };
    engine.select(null, true);
    expect(engine.publication().selected?.trackingBeganAt).toBe(clock.at);
    expect(engine.publication().selected?.spendingCents).toBe(0);
    expect(engine.state.flow).toEqual(before); expect(engine.state.clock).toEqual(clock);
    expect(engine.publication().points).toHaveLength(0);
  });
  it('keeps an in-progress lane stable across editing and precise save/load', () => {
    const fixture = dualFixture(100), engine = new DualClockEngine(fixture);
    advance(engine, Date.parse(fixture.at) + 6 * 3600000);
    const guest = engine.state.guests.find(g => g.status === 'skiing')!; expect(guest).toBeDefined();
    const before = engine.publication().points.find(p => p.id === guest.id)!;
    const resort = structuredClone(fixture.resort); resort.revision++;
    resort.edges = resort.edges.map(edge => edge.kind === 'trail' ? { ...edge, path: edge.path.map(p => [p[0] + 0.00001, p[1]] as [number, number]) } : edge);
    engine.setResort(resort);
    expect(engine.publication().points.find(p => p.id === guest.id)).toEqual(before);
    const restored = new DualClockEngine({ ...fixture, resort, checkpoint: JSON.parse(JSON.stringify(engine.checkpoint())) });
    expect(restored.publication().points.find(p => p.id === guest.id)).toEqual(before);
  });
  it('rejects corrupt financial conservation and mismatched terrain instead of resetting', () => {
    const fixture = dualFixture(), checkpoint = new DualClockEngine(fixture).checkpoint();
    checkpoint.flow.active = 1;
    expect(() => validateDualCheckpoint(checkpoint)).toThrow(/checkpoint/);
    const invalidDaily = new DualClockEngine(fixture).checkpoint();
    invalidDaily.dailyLiftBoardings = { date: '2026-11-02', counts: { 'lift-edge': -1 }, accuracy: 'complete' };
    expect(() => validateDualCheckpoint(invalidDaily)).toThrow(/checkpoint/);
    const terrain = fixtureTerrain(fixture); terrain.bounds = { ...terrain.bounds!, east: terrain.bounds!.east + 1 };
    expect(() => new DualClockEngine({ ...fixture, terrain })).toThrow(/bounds/);
  });
});
