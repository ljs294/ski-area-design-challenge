import { describe, expect, it } from 'vitest';
import type { RepresentativeGuest } from '../types/dualClock';
import { createNeedState } from '../guestSimulation/needs';
import { dualFixture } from './fixtures';
import { DualClockEngine } from './engine';
import { deriveGuestSpeedZ, guestSpeedMultiplier, guestTravelDuration, truncatedStandardNormal } from './guestMovement';
import { enqueueTrailGuest, releaseTrailGuest } from './trailQueue';

function advance(engine: DualClockEngine, targetMs: number): void {
  const work = engine.advanceTo(targetMs);
  while (!work.next().done) { /* deterministic */ }
}

function representative(id: string, ordinal: number, nodeId: string, position: [number, number], at: string): RepresentativeGuest {
  return {
    id, ordinal, groupId: id, status: 'walking', nextPlan: 'Choose a lift', thought: '', satisfaction: 0.8,
    runs: 0, spendingCents: 0, trackingBeganAt: at, history: [], needs: createNeedState({ hunger: 0.2, thirst: 0.2 }),
    needsSecond: 0, personalBudgetCents: 5000, edgeId: null, route: [], routeIndex: 0, nodeId,
    started: 0, due: 0, admissionDay: at.slice(0, 10), ability: 0.5, lastPosition: [...position],
    speedZ: deriveGuestSpeedZ('dual-test', id),
  };
}

describe('dual-clock individual movement', () => {
  it('releases same-entrance arrivals immediately and then two movement seconds apart', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const engine = new DualClockEngine(fixture);
    engine.state.guests = [1, 2, 3].map(index => representative(`guest-${index}`, index, edge.from, [...edge.path[0]], fixture.at));
    engine.play();
    advance(engine, Date.parse(fixture.at) + 8 * 60_000);
    const guests = engine.state.guests.sort((a, b) => a.ordinal - b.ordinal);
    expect(guests.map(guest => guest.status)).toEqual(['skiing', 'skiing', 'skiing']);
    expect(guests.map(guest => guest.started)).toEqual([0, 2, 4]);
    expect(guests.every(guest => guest.trailQueueKey === undefined)).toBe(true);
  });

  it('keeps queue ordering invariant across small and large advancement chunks', () => {
    const make = () => {
      const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
      const engine = new DualClockEngine(fixture);
      engine.state.guests = [1, 2, 3].map(index => representative(`guest-${index}`, index, edge.from, [...edge.path[0]], fixture.at));
      engine.play();
      return engine;
    };
    const large = make(), small = make(), target = Date.parse(dualFixture(0).at) + 8 * 60_000;
    advance(large, target);
    const chunks = [17, 43, 29, 71, 11, 89, 37, 53, 61, 19];
    let elapsed = 0;
    for (const chunk of chunks) { elapsed += chunk; advance(small, Date.parse(dualFixture(0).at) + elapsed * 1000); }
    advance(small, target);
    expect(small.state.guests.map(guest => ({ id: guest.id, status: guest.status, started: guest.started, due: guest.due })))
      .toEqual(large.state.guests.map(guest => ({ id: guest.id, status: guest.status, started: guest.started, due: guest.due })));
    expect(small.state.trailQueues).toEqual(large.state.trailQueues);
  });

  it('retains the last release cooldown when an entrance becomes briefly empty', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const engine = new DualClockEngine(fixture);
    const first = representative('guest-1', 1, edge.from, [...edge.path[0]], fixture.at);
    engine.state.guests = [first]; engine.play();
    advance(engine, Date.parse(fixture.at) + 1 * 60_000);
    expect(first.status).toBe('skiing');
    const second = representative('guest-2', 2, edge.from, [...edge.path[0]], fixture.at);
    second.due = first.started + 0.5;
    engine.state.guests.push(second);
    advance(engine, Date.parse(fixture.at) + 4 * 60_000);
    expect(second.status).toBe('skiing');
    expect(second.started).toBe(first.started + 2);
  });

  it('drops retired waiters without reserving their never-released slots', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const engine = new DualClockEngine(fixture);
    const first = representative('guest-1', 1, edge.from, [...edge.path[0]], fixture.at);
    const retired = representative('guest-2', 2, edge.from, [...edge.path[0]], fixture.at);
    engine.state.guests = [first, retired]; engine.play();
    advance(engine, Date.parse(fixture.at) + 1 * 60_000);
    expect(retired.status).toBe('trail-queue');
    engine.state.guests = engine.state.guests.filter(guest => guest.id !== retired.id);
    const replacement = representative('guest-3', 3, edge.from, [...edge.path[0]], fixture.at);
    replacement.due = 1.6;
    engine.state.guests.push(replacement);
    advance(engine, Date.parse(fixture.at) + 4 * 60_000);
    expect(replacement.status).toBe('skiing');
    expect(replacement.started).toBe(2);
  });

  it('compacts a surviving waiter into a retired middle reservation', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const engine = new DualClockEngine(fixture);
    const first = representative('guest-1', 1, edge.from, [...edge.path[0]], fixture.at);
    const retired = representative('guest-2', 2, edge.from, [...edge.path[0]], fixture.at);
    const third = representative('guest-3', 3, edge.from, [...edge.path[0]], fixture.at);
    engine.state.guests = [first, retired, third];
    const queues = engine.state.trailQueues ??= {};
    enqueueTrailGuest(queues, first, edge, 0, 2);
    enqueueTrailGuest(queues, retired, edge, 0, 2);
    enqueueTrailGuest(queues, third, edge, 0, 2);
    releaseTrailGuest(first, queues, new Map([[edge.id, edge]]), 0, 2, () => true, (_, selectedEdge) => selectedEdge.travelTimeS);
    engine.state.guests = engine.state.guests.filter(guest => guest.id !== retired.id);
    const internal = engine as unknown as { reconcileTrailQueues: (now: number) => void };
    internal.reconcileTrailQueues(1);
    const queue = engine.state.trailQueues?.['run|top'];
    expect(queue?.entries.map(entry => [entry.guestId, entry.releaseMicroSecond])).toEqual([['guest-3', 2]]);
    expect(third.trailQueueReleaseMicroSecond).toBe(2);
    expect(third.due).toBe(2);
  });

  it('replans queued guests when their selected entrance closes', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const engine = new DualClockEngine(fixture);
    const first = representative('guest-1', 1, edge.from, [...edge.path[0]], fixture.at);
    const queued = representative('guest-2', 2, edge.from, [...edge.path[0]], fixture.at);
    engine.state.guests = [first, queued]; engine.play();
    advance(engine, Date.parse(fixture.at) + 1 * 60_000);
    expect(queued.status).toBe('trail-queue');
    const closed = structuredClone(fixture.resort);
    closed.revision++;
    closed.edges = closed.edges.map(candidate => candidate.id === edge.id ? { ...candidate, open: false } : candidate);
    engine.setResort(closed);
    expect(queued.status).toBe('walking');
    expect(queued.edgeId).toBeNull();
    expect(engine.state.trailQueues).toEqual({});
  });

  it('keeps independent trail entrances on independent clocks', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const branchTrail = { ...fixture.resort.trails[0], id: 'run-b', name: 'Branch Run' };
    const branch = { ...edge, id: 'trail-edge-b', trailId: 'run-b', trailName: 'Branch Run', from: 'top-b', to: 'base-b' };
    fixture.resort = { ...fixture.resort, edges: [...fixture.resort.edges, branch], trails: [...fixture.resort.trails, branchTrail] };
    const engine = new DualClockEngine(fixture);
    const first = representative('guest-1', 1, edge.from, [...edge.path[0]], fixture.at);
    const second = representative('guest-2', 2, branch.from, [...branch.path[0]], fixture.at);
    engine.state.guests = [first, second]; engine.play();
    advance(engine, Date.parse(fixture.at) + 1 * 60_000);
    expect(first.status).toBe('skiing');
    expect(second.status).toBe('skiing');
    expect(first.started).toBe(0);
    expect(second.started).toBe(0);
    expect(Object.keys(engine.state.trailQueues ?? {})).toEqual(['run|top', 'run-b|top-b']);
    expect(Object.values(engine.state.trailQueues ?? {}).every(queue => queue.entries.length === 0 && queue.nextReleaseMicroSecond === 2)).toBe(true);
  });

  it('continues through split segments of one trail without a second entrance wait', () => {
    const fixture = dualFixture(0), original = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const top = original.path[0], base = original.path[original.path.length - 1];
    const middle: [number, number] = [(top[0] + base[0]) / 2, (top[1] + base[1]) / 2];
    const first = { ...original, id: 'trail-edge-1', to: 'mid', path: [top, middle] as [number, number][], lengthM: 500, travelTimeS: 1 };
    const second = { ...original, id: 'trail-edge-2', from: 'mid', path: [middle, base] as [number, number][], lengthM: 500, travelTimeS: 1 };
    fixture.resort = { ...fixture.resort, edges: [fixture.resort.edges[0]!, first, second] };
    const engine = new DualClockEngine(fixture);
    const guest = representative('guest-1', 1, first.from, [...top], fixture.at);
    guest.status = 'skiing'; guest.edgeId = first.id; guest.started = 0; guest.due = 1; guest.lastTrailId = 'run';
    engine.state.guests = [guest]; engine.play();
    advance(engine, Date.parse(fixture.at) + 60_000);
    expect(guest.status).toBe('skiing');
    expect(guest.edgeId).toBe(second.id);
    expect(guest.started).toBe(1);
    expect(engine.state.trailQueues).toEqual({});
  });

  it('uses stable truncated keyed speed personality with positive duration', () => {
    const samples = Array.from({ length: 512 }, (_, index) => deriveGuestSpeedZ('speed-seed', `guest-${index}`));
    expect(samples.every(sample => sample >= -3 && sample <= 3)).toBe(true);
    const mean = samples.reduce((sum, sample) => sum + sample, 0) / samples.length;
    const deviation = Math.sqrt(samples.reduce((sum, sample) => sum + (sample - mean) ** 2, 0) / samples.length);
    expect(mean).toBeCloseTo(0, 1);
    expect(deviation).toBeGreaterThan(0.7);
    expect(deriveGuestSpeedZ('speed-seed', 'guest-1')).toBe(deriveGuestSpeedZ('speed-seed', 'guest-1'));
    expect(guestSpeedMultiplier(0.8, -3)).toBeGreaterThan(0);
    expect(guestTravelDuration({ lengthM: 900, travelTimeS: 300 }, 0.8, -3)).toBeGreaterThan(0);
    expect(guestTravelDuration({ lengthM: 900, travelTimeS: 300 }, 0.9, 0)).toBeLessThan(
      guestTravelDuration({ lengthM: 900, travelTimeS: 300 }, 0.2, 0));
    expect(Math.abs(truncatedStandardNormal('speed-seed', 'guest-2'))).toBeLessThanOrEqual(3);
  });

  it('hydrates an earlier schema-17 checkpoint with empty movement queues and preserves deadlines', () => {
    const fixture = dualFixture(0), engine = new DualClockEngine(fixture);
    const guest = representative('guest-1', 1, 'base', fixture.resort.portal!.lngLat as [number, number], fixture.at);
    guest.status = 'skiing'; guest.edgeId = 'trail-edge'; guest.started = 3; guest.due = 303;
    engine.state.guests = [guest];
    const checkpoint = engine.checkpoint();
    delete checkpoint.config.guestMovement;
    delete checkpoint.trailQueues;
    const restored = new DualClockEngine({ ...fixture, checkpoint });
    expect(restored.state.config.guestMovement?.version).toBe(1);
    expect(restored.state.trailQueues).toEqual({});
    expect(restored.state.guests[0]).toMatchObject({ started: 3, due: 303 });
  });

  it('round-trips queued release timestamps and speed personality', () => {
    const fixture = dualFixture(0), edge = fixture.resort.edges.find(candidate => candidate.kind === 'trail')!;
    const engine = new DualClockEngine(fixture);
    const first = representative('guest-1', 1, edge.from, [...edge.path[0]], fixture.at);
    const second = representative('guest-2', 2, edge.from, [...edge.path[0]], fixture.at);
    engine.state.guests = [first, second]; engine.play();
    advance(engine, Date.parse(fixture.at) + 60_000);
    const checkpoint = JSON.parse(JSON.stringify(engine.checkpoint()));
    const restored = new DualClockEngine({ ...fixture, checkpoint });
    expect(restored.state.trailQueues?.['run|top']?.entries[0]?.releaseMicroSecond).toBe(2);
    expect(restored.state.guests.map(guest => guest.speedZ)).toEqual(engine.state.guests.map(guest => guest.speedZ));
    advance(restored, Date.parse(fixture.at) + 4 * 60_000);
    expect(restored.state.guests.find(guest => guest.id === second.id)?.started).toBe(2);
  });
});
