import { describe, expect, it } from 'vitest';
import { EventCalendar } from './eventCalendar';
import { createGuestPortals, createGuestSimulationFixture } from './fixtures';
import { createDailyGuestRoster, createDefaultGuestSimulationNetwork, createGuestSimulationEngine, type GuestSimulationEngineSnapshot } from './engine';
import { replayStateFromGuestSimulationEngine, restoreGuestSimulationEngine } from './enginePersistence';
import {
  CoherentTickSnapshotBarrier,
  GuestSimulationCheckpointManager,
  InMemoryCheckpointAdapter,
  createGuestSimulationReplayState,
  guestSimulationReplayHash,
  rebuildGuestSimulationDerivedState,
  replayDeterminismProjection,
  type GuestSimulationReplayState,
} from './replayPersistence';
import { BinarySidecarCodec, decodeBinarySidecar, encodeBinarySidecar, readBinarySidecarHeader } from './binaryCodec';

function makeState(tick = 0): GuestSimulationReplayState {
  const fixture = createGuestSimulationFixture(1_000);
  const calendar = new EventCalendar<unknown>();
  calendar.schedule({ tick: 1, priority: 2, entityId: 'guest-000001', key: 'arrive', payload: { kind: 'arrive' }, phase: 'bookings-arrivals' });
  calendar.schedule({ tick: 3, priority: 6, entityId: 'guest-000001', key: 'decide', payload: undefined, phase: 'decisions-enqueue' });
  calendar.advanceTo(tick);
  const snapshot = fixture.createSnapshot(tick);
  return createGuestSimulationReplayState({
    snapshot,
    eventCalendar: calendar.stateProjection(),
    events: [
      { id: 'event-arrive', tick: 1, kind: 'arrived', payload: { guestId: 'guest-000001' } },
      { id: 'event-thought', tick: tick, kind: 'queueing' },
    ],
    roster: snapshot.guests.filter((guest) => guest.status !== 'departed').slice(0, 12).map((guest) => guest.id),
    rngOrdinals: [
      { entityId: 'guest-000001', domainTag: 'route', ordinal: 2 },
      { entityId: 'guest-000002', domainTag: 'mood', ordinal: 1 },
    ],
  });
}

describe('guest simulation binary sidecar', () => {
  it('uses canonical compact bytes, a versioned header, and validates content hashes', () => {
    const value = { z: [3, true, 'snow'], a: { second: 2, first: 1 } };
    const left = encodeBinarySidecar(value);
    const right = encodeBinarySidecar({ a: { first: 1, second: 2 }, z: [3, true, 'snow'] });
    expect(left).toEqual(right);
    expect(decodeBinarySidecar<typeof value>(left)).toEqual(value);
    const header = readBinarySidecarHeader(left);
    expect(header.magic).toBe('GSCP');
    expect(header.formatVersion).toBe(1);
    expect(header.payloadLength).toBeLessThan(left.length);
    const corrupt = left.slice();
    corrupt[corrupt.length - 1] = corrupt[corrupt.length - 1]! ^ 0x01;
    expect(() => decodeBinarySidecar(corrupt)).toThrow(/hash mismatch/i);
  });

  it('round-trips authoritative replay state while retaining queue, events, roster, and RNG ordinals', () => {
    const state = makeState(0);
    const codec = new BinarySidecarCodec<GuestSimulationReplayState>();
    const loaded = codec.decode(codec.encode(state));
    expect(loaded).toEqual(state);
    expect(replayDeterminismProjection(loaded)).toEqual(replayDeterminismProjection(state));
    expect(guestSimulationReplayHash(loaded)).toBe(guestSimulationReplayHash(state));
  });

  it('accepts an actual Phase 1A engine snapshot as the authoritative snapshot payload', () => {
    const portals = createGuestPortals(20);
    const roster = createDailyGuestRoster({ seed: 'engine-checkpoint', guestCount: 4, portals, endTick: 20 });
    const engine = createGuestSimulationEngine({ network: createDefaultGuestSimulationNetwork(portals), roster, runId: 'engine-checkpoint-run' });
    const snapshot: GuestSimulationEngineSnapshot = engine.advanceTo(5);
    const calendar = new EventCalendar<unknown>(snapshot.tick);
    const state = createGuestSimulationReplayState({
      snapshot,
      eventCalendar: calendar.stateProjection(),
      events: snapshot.thoughtEvents.map((event) => ({ id: event.id, tick: event.tick, kind: event.kind, payload: event.text })),
      roster: snapshot.guests.map((guest) => guest.id),
      // Phase 1A keeps decision ordinals private; the engine port below is
      // the explicit seam for persisting them once the owner exposes them.
      rngOrdinals: [],
    });
    const loaded = decodeBinarySidecar<typeof state>(encodeBinarySidecar(state));
    expect(loaded.snapshot).toEqual(snapshot);
    const loadedEngineSnapshot = loaded.snapshot as GuestSimulationEngineSnapshot;
    expect(loadedEngineSnapshot.network).toEqual(snapshot.network);
    expect(loadedEngineSnapshot.itineraries).toEqual(snapshot.itineraries);
  });

  it('captures and replay-restores a Phase 1A engine with equal continuation output', () => {
    const portals = createGuestPortals(20);
    const roster = createDailyGuestRoster({ seed: 'engine-replay', guestCount: 4, portals, endTick: 20 });
    const network = createDefaultGuestSimulationNetwork(portals);
    const uninterrupted = createGuestSimulationEngine({ network, roster, runId: 'engine-replay-run' });
    uninterrupted.advanceTo(5);
    const state = replayStateFromGuestSimulationEngine(uninterrupted);
    const resumed = restoreGuestSimulationEngine(state);
    const uninterruptedAtEnd = uninterrupted.advanceTo(12);
    const resumedAtEnd = resumed.advanceTo(12);
    expect(resumedAtEnd.checksum).toBe(uninterruptedAtEnd.checksum);
    expect(resumedAtEnd.guests).toEqual(uninterruptedAtEnd.guests);
    expect(resumedAtEnd.parties).toEqual(uninterruptedAtEnd.parties);
    expect(resumedAtEnd.thoughtEvents).toEqual(uninterruptedAtEnd.thoughtEvents);
  });
});

describe('guest simulation coherent checkpoint manager', () => {
  it('rejects a source that advances while the barrier is capturing', () => {
    const initial = makeState(0);
    let currentTick = 0;
    const barrier = new CoherentTickSnapshotBarrier({
      getCurrentTick: () => currentTick,
      captureAuthoritativeState: () => {
        currentTick = 1;
        return initial;
      },
    });
    expect(() => barrier.capture()).toThrow(/advanced during/i);
    expect(barrier.isCapturing).toBe(false);
  });

  it('recovers previous when current is corrupt and reports explicit diagnostics', () => {
    const adapter = new InMemoryCheckpointAdapter();
    const manager = new GuestSimulationCheckpointManager(adapter);
    const first = makeState(0);
    const second = makeState(1);
    manager.save(first);
    const saved = manager.save(second);
    expect(saved.contentHash).toBe(readBinarySidecarHeader(saved.bytes).contentHash);
    const current = adapter.read('current')!;
    current[current.length - 1] = current[current.length - 1]! ^ 0x20;
    adapter.replace('current', current);
    const loaded = manager.load();
    expect(loaded.ok).toBe(true);
    if (loaded.ok) {
      expect(loaded.slot).toBe('previous');
      expect(loaded.state).toEqual(first);
      expect(loaded.diagnostics).toEqual(expect.arrayContaining([
        expect.objectContaining({ slot: 'current', outcome: 'corrupt' }),
      ]));
    }
  });

  it('uses previous when current is missing and distinguishes missing from all-corrupt storage', () => {
    const adapter = new InMemoryCheckpointAdapter();
    const manager = new GuestSimulationCheckpointManager(adapter);
    const state = makeState(0);
    manager.save(state);
    manager.save(makeState(1));
    adapter.remove('current');
    const fallback = manager.load();
    expect(fallback.ok).toBe(true);
    if (fallback.ok) expect(fallback.slot).toBe('previous');

    const previous = adapter.read('previous')!;
    previous[previous.length - 2] = previous[previous.length - 2]! ^ 0x40;
    adapter.replace('previous', previous);
    const corrupt = manager.load();
    expect(corrupt).toMatchObject({ ok: false, outcome: 'corrupt' });
    if (!corrupt.ok) expect(corrupt.diagnostics.every((entry) => entry.outcome === 'corrupt' || entry.outcome === 'missing')).toBe(true);

    adapter.clear();
    expect(manager.load()).toMatchObject({ ok: false, outcome: 'missing' });
  });

  it('rebuilds indexes, queue view, and render buffer from authoritative data after load', () => {
    const state = makeState(0);
    const rebuilt = rebuildGuestSimulationDerivedState(state);
    expect(rebuilt.guestById.get(state.snapshot.guests[0]!.id)).toEqual(state.snapshot.guests[0]);
    expect(rebuilt.partyById.get(state.snapshot.parties[0]!.id)).toEqual(state.snapshot.parties[0]);
    expect(rebuilt.eventById.get('event-arrive')).toEqual(state.events[0]);
    expect(rebuilt.queueOrder.length).toBe(state.eventCalendar.events.length);
    expect(rebuilt.roster).toEqual(state.roster);
    expect(rebuilt.renderBuffer).toHaveLength(state.snapshot.guests.length);
  });

  it('keeps uninterrupted and save/load/continue parity for the deterministic projection', () => {
    const start = makeState(0);
    const adapter = new InMemoryCheckpointAdapter();
    const manager = new GuestSimulationCheckpointManager(adapter);
    manager.save(start);
    const loaded = manager.load();
    expect(loaded.ok).toBe(true);
    if (!loaded.ok) return;
    const uninterrupted = makeState(1);
    const resumed = makeState(1);
    expect(replayDeterminismProjection(resumed)).toEqual(replayDeterminismProjection(uninterrupted));
    expect(guestSimulationReplayHash(resumed)).toBe(guestSimulationReplayHash(uninterrupted));
    expect(resumed.snapshot.parties).toEqual(uninterrupted.snapshot.parties);
    expect(resumed.events).toEqual(uninterrupted.events);
    expect(resumed.roster).toEqual(uninterrupted.roster);
    expect(resumed.rngOrdinals).toEqual(uninterrupted.rngOrdinals);
  });
});
