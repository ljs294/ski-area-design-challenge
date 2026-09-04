/**
 * Concrete Phase 1A engine integration for the dependency-neutral checkpoint
 * layer.  The engine publishes its pending queue, lift queues, decision
 * ordinals, and plan projections in its snapshot; this adapter maps those
 * projections to the replay state and restores by deterministic replay from
 * the roster origin to the checkpoint tick.  It is intentionally explicit:
 * the engine is not asked to understand sidecars and no UI/save type crosses
 * this boundary.
 */

import {
  createDailyGuestRoster,
  createGuestSimulationEngine,
  type GuestSimulationEngine,
  type GuestSimulationEngineOptions,
  type GuestSimulationEngineSnapshot,
  type GuestSimulationNetwork,
} from './engine.ts';
import { guestEventPhaseRank } from './eventPhases.ts';
import type { EventCalendarStateProjection } from './eventCalendar.ts';
import type {
  CoherentTickSnapshotSource,
  GuestSimulationReplayEvent,
  GuestSimulationReplayState,
} from './replayPersistence.ts';
import {
  CoherentTickSnapshotBarrier,
  createGuestSimulationReplayState,
} from './replayPersistence.ts';
import type {
  Guest,
  GuestSimulationEnvironmentSnapshot,
  GuestState,
  ThoughtEvent,
} from './contracts.ts';

interface EngineSnapshotWithRuntimeData extends GuestSimulationEngineSnapshot {
  readonly network: GuestSimulationNetwork;
}

function engineSnapshot(engine: GuestSimulationEngine): EngineSnapshotWithRuntimeData {
  return engine.snapshot() as EngineSnapshotWithRuntimeData;
}

function replayEvents(events: readonly ThoughtEvent[]): readonly GuestSimulationReplayEvent[] {
  return events.map((event) => Object.freeze({ id: event.id, tick: event.tick, kind: event.kind, payload: event }));
}

function replayCalendar(snapshot: EngineSnapshotWithRuntimeData): EventCalendarStateProjection<unknown> {
  const pending = snapshot.pendingEvents;
  const maxInsertionSequence = pending.reduce((maximum, event) => Math.max(maximum, event.insertionSequence), -1);
  const generations = pending
    .map((event) => ({ entityId: event.ownerId, key: event.guestId, generation: event.generation }))
    .filter((token, index, tokens) => tokens.findIndex((candidate) => candidate.entityId === token.entityId
      && candidate.key === token.key) === index);
  const events = pending.map((event) => ({
    tick: event.tick,
    priority: event.phase === undefined ? 0 : guestEventPhaseRank(event.phase),
    entityId: event.ownerId,
    key: event.guestId,
    payload: event.payload,
    phase: event.phase,
    generation: event.generation,
    insertionSequence: event.insertionSequence,
  }));
  return {
    currentTick: snapshot.tick,
    nextInsertionSequence: maxInsertionSequence + 1,
    generations,
    events,
  };
}

/** Capture all fields needed to compare and replay a concrete Phase 1A engine. */
export function replayStateFromGuestSimulationEngine(engine: GuestSimulationEngine): GuestSimulationReplayState {
  const snapshot = engineSnapshot(engine);
  return createGuestSimulationReplayState({
    snapshot,
    config: engine.config,
    eventCalendar: replayCalendar(snapshot),
    events: replayEvents(snapshot.thoughtEvents),
    roster: engine.roster.guests.map((guest) => guest.id),
    rngOrdinals: snapshot.decisionOrdinals.map((entry) => ({ entityId: entry.guestId, domainTag: 'decision', ordinal: entry.ordinal })),
  });
}

export const captureGuestSimulationEngineReplayState = replayStateFromGuestSimulationEngine;

function baseGuest(guest: GuestState): Guest {
  return {
    id: guest.id,
    partyId: guest.partyId,
    ordinal: guest.ordinal,
    arrivalTick: guest.arrivalTick,
    plannedDepartureTick: guest.plannedDepartureTick,
    portalId: guest.portalId,
    preferences: guest.preferences,
    futurePartyId: guest.futurePartyId,
  };
}

function initialEnvironment(snapshot: GuestSimulationEngineSnapshot): GuestSimulationEnvironmentSnapshot {
  const startTick = snapshot.demandPlan.startTick;
  return Object.freeze({
    ...snapshot.environment,
    tick: startTick,
    conditions: Object.freeze({ ...snapshot.environment.conditions, tick: startTick }),
  });
}

export class GuestSimulationEngineRestoreError extends Error {
  constructor(message: string) {
    super(message);
    this.name = 'GuestSimulationEngineRestoreError';
  }
}

/**
 * Restore a concrete engine by deterministic replay from its saved roster.
 * The returned instance is the resumed engine; the old instance is untouched.
 * The final checksum is checked before the instance is handed to a caller.
 */
export function restoreGuestSimulationEngine(
  state: GuestSimulationReplayState,
  options: Pick<GuestSimulationEngineOptions, 'config'> = {},
): GuestSimulationEngine {
  const snapshot = engineSnapshotFromReplayState(state);
  const roster = createDailyGuestRoster({
    seed: snapshot.demandPlan.seed,
    guestCount: snapshot.guests.length,
    portals: snapshot.network.portals,
    startTick: snapshot.demandPlan.startTick,
    endTick: snapshot.demandPlan.endTick,
    demandPlan: snapshot.demandPlan,
  });
  // The deterministic roster generator is part of the engine's input.  A
  // mismatched roster is safer to reject than to silently replay another run.
  if (!roster.guests.every((guest, index) => guest.id === baseGuest(snapshot.guests[index]!).id
    && guest.partyId === snapshot.guests[index]!.partyId)) {
    throw new GuestSimulationEngineRestoreError('Checkpoint roster no longer matches the deterministic demand seed');
  }
  const engineOptions: GuestSimulationEngineOptions = {
    network: snapshot.network,
    roster,
    runId: snapshot.runId,
    config: state.config ?? options.config,
    environment: initialEnvironment(snapshot),
    conditionSnapshot: snapshot.conditionHistory?.[0]?.tick === snapshot.demandPlan.startTick
      ? snapshot.conditionHistory[0] : undefined,
    phase3: snapshot.phase3 ? { dayId: snapshot.phase3.economy.dayId,
      ticketPriceCents: snapshot.phase3.economy.ticketFinance?.ticketPriceCents ?? 10_000,
      demandForecast: snapshot.phase3.demandForecast ?? undefined,
      demandRealization: snapshot.phase3.demandRealization ?? undefined,
      openingReputation: snapshot.phase3.economy.openingReputation } : undefined,
  };
  const engine = createGuestSimulationEngine(engineOptions);
  for (const conditions of snapshot.conditionHistory?.slice(1) ?? []) {
    if (conditions.tick <= snapshot.tick) engine.applyConditionSnapshot(conditions);
  }
  const replayed = snapshot.tick === snapshot.demandPlan.startTick ? engine.snapshot() : engine.advanceTo(snapshot.tick);
  const legacyPhaseOneCheckpoint = !Array.isArray(snapshot.conditionHistory);
  if (!legacyPhaseOneCheckpoint && replayed.checksum !== snapshot.checksum) {
    throw new GuestSimulationEngineRestoreError(
      `Checkpoint replay checksum mismatch: expected ${snapshot.checksum}, got ${replayed.checksum}`,
    );
  }
  return engine;
}

function engineSnapshotFromReplayState(state: GuestSimulationReplayState): EngineSnapshotWithRuntimeData {
  const snapshot = state.snapshot as GuestSimulationEngineSnapshot;
  if (!snapshot.network || !Array.isArray(snapshot.network.portals)) {
    throw new GuestSimulationEngineRestoreError('Checkpoint does not contain a Phase 1A engine network');
  }
  return snapshot as EngineSnapshotWithRuntimeData;
}

export interface GuestSimulationEngineReplayAdapter {
  readonly source: CoherentTickSnapshotSource;
  capture(): GuestSimulationReplayState;
  restore(state: GuestSimulationReplayState, options?: Pick<GuestSimulationEngineOptions, 'config'>): GuestSimulationEngine;
}

export function createGuestSimulationEngineReplayAdapter(engine: GuestSimulationEngine): GuestSimulationEngineReplayAdapter {
  const source: CoherentTickSnapshotSource = {
    getCurrentTick: () => engine.currentTick,
    captureAuthoritativeState: () => replayStateFromGuestSimulationEngine(engine),
  };
  const barrier = new CoherentTickSnapshotBarrier(source);
  return {
    source,
    capture: () => barrier.capture(),
    restore: (state, options) => restoreGuestSimulationEngine(state, options),
  };
}
