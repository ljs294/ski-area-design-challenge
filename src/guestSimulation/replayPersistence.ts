/**
 * Persistence and replay primitives for the guest simulation.
 *
 * This module stores only authoritative simulation state.  Maps, indexes,
 * queue views, and render buffers are intentionally derived again after a
 * load.  Nothing here knows about GameSave, Electron, IndexedDB, React, or a
 * browser.  A desktop/browser adapter can implement CheckpointStorageAdapter
 * and choose a stronger BinaryContentHasher when it is available.
 */

import type {
  GuestId,
  GuestSimulationSnapshot,
  GuestState,
  PartyId,
  PartyState,
  SimulatedSecond,
} from './contracts.ts';
import { isGuestSimulationSnapshot, isSimulatedSecond } from './contracts.ts';
import { assertGuestSimulationConfig, type GuestSimulationConfig } from './config.ts';
import type {
  CalendarProjectionEvent,
  EventCalendarStateProjection,
  EventGenerationToken,
} from './eventCalendar.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';
import {
  BinarySidecarCodec,
  type BinaryCodecOptions,
  decodeBinarySidecar,
  encodeBinarySidecar,
  portableContentHash,
  readBinarySidecarHeader,
} from './binaryCodec.ts';

export const GUEST_SIMULATION_REPLAY_STATE_VERSION = 1 as const;
export type GuestSimulationReplayStateVersion = typeof GUEST_SIMULATION_REPLAY_STATE_VERSION;

/** One event emitted by the authoritative simulation, in dispatch order. */
export interface GuestSimulationReplayEvent {
  readonly id: string;
  readonly tick: SimulatedSecond;
  readonly kind: string;
  readonly payload?: unknown;
}

/** The keyed RNG cursor that must survive a save/load boundary. */
export interface GuestSimulationRngOrdinal {
  readonly entityId: string;
  readonly domainTag: string;
  readonly ordinal: number;
}

/**
 * The complete replay input.  `snapshot` contains the contract-level guest,
 * party, environment, and thought state.  The remaining fields are the
 * mutable simulation machinery that cannot be reconstructed from that
 * snapshot alone.
 */
export interface GuestSimulationReplayState {
  readonly version: GuestSimulationReplayStateVersion;
  readonly snapshot: GuestSimulationSnapshot;
  /** Optional immutable tuning used when the host needs replay-origin restore. */
  readonly config?: GuestSimulationConfig;
  readonly eventCalendar: EventCalendarStateProjection<unknown>;
  readonly events: readonly GuestSimulationReplayEvent[];
  readonly roster: readonly GuestId[];
  readonly rngOrdinals: readonly GuestSimulationRngOrdinal[];
}

export interface GuestSimulationReplayStateInput {
  readonly snapshot: GuestSimulationSnapshot;
  readonly config?: GuestSimulationConfig;
  readonly eventCalendar: EventCalendarStateProjection<unknown>;
  readonly events?: readonly GuestSimulationReplayEvent[];
  readonly roster?: readonly GuestId[];
  readonly rngOrdinals?: readonly GuestSimulationRngOrdinal[];
}

export type ReplayState = GuestSimulationReplayState;
export type ReplayEvent = GuestSimulationReplayEvent;
export type RngOrdinal = GuestSimulationRngOrdinal;

export type ReplayStateValidationCode = 'invalid-state' | 'invalid-calendar' | 'invalid-event' | 'invalid-rng-ordinal';

export class ReplayStateValidationError extends Error {
  readonly code: ReplayStateValidationCode;

  constructor(code: ReplayStateValidationCode, message: string) {
    super(message);
    this.name = 'ReplayStateValidationError';
    this.code = code;
  }
}

function freezeArray<T>(values: readonly T[]): readonly T[] {
  return Object.freeze([...values]);
}

function freezeEvent(event: GuestSimulationReplayEvent): GuestSimulationReplayEvent {
  return Object.freeze({ ...event });
}

function freezeOrdinal(ordinal: GuestSimulationRngOrdinal): GuestSimulationRngOrdinal {
  return Object.freeze({ ...ordinal });
}

function compareText(left: string, right: string): number {
  return left < right ? -1 : left > right ? 1 : 0;
}

function isNonEmptyString(value: unknown): value is string {
  return typeof value === 'string' && value.length > 0;
}

function assertNonNegativeSafeInteger(value: unknown, label: string, code: ReplayStateValidationCode): asserts value is number {
  if (!Number.isSafeInteger(value) || (value as number) < 0) throw new ReplayStateValidationError(code, `${label} must be a non-negative safe integer`);
}

function assertCalendarEvent(value: unknown, index: number): asserts value is CalendarProjectionEvent<unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new ReplayStateValidationError('invalid-calendar', `Calendar event ${index} is not an object`);
  }
  const event = value as Partial<CalendarProjectionEvent<unknown>>;
  assertNonNegativeSafeInteger(event.tick, `Calendar event ${index} tick`, 'invalid-calendar');
  if (!Number.isSafeInteger(event.priority)) {
    throw new ReplayStateValidationError('invalid-calendar', `Calendar event ${index} priority must be a safe integer`);
  }
  if (!isNonEmptyString(event.entityId) || typeof event.key !== 'string') {
    throw new ReplayStateValidationError('invalid-calendar', `Calendar event ${index} has an invalid identity`);
  }
  assertNonNegativeSafeInteger(event.generation, `Calendar event ${index} generation`, 'invalid-calendar');
  assertNonNegativeSafeInteger(event.insertionSequence, `Calendar event ${index} insertion sequence`, 'invalid-calendar');
  if (event.phase !== undefined && typeof event.phase !== 'string') {
    throw new ReplayStateValidationError('invalid-calendar', `Calendar event ${index} phase must be a string when present`);
  }
}

function assertGeneration(value: unknown, index: number): asserts value is EventGenerationToken {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new ReplayStateValidationError('invalid-calendar', `Calendar generation ${index} is not an object`);
  }
  const token = value as Partial<EventGenerationToken>;
  if (!isNonEmptyString(token.entityId) || typeof token.key !== 'string') {
    throw new ReplayStateValidationError('invalid-calendar', `Calendar generation ${index} has an invalid identity`);
  }
  assertNonNegativeSafeInteger(token.generation, `Calendar generation ${index}`, 'invalid-calendar');
}

function assertEventCalendar(value: unknown): asserts value is EventCalendarStateProjection<unknown> {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new ReplayStateValidationError('invalid-calendar', 'eventCalendar must be an object');
  }
  const calendar = value as Partial<EventCalendarStateProjection<unknown>>;
  assertNonNegativeSafeInteger(calendar.currentTick, 'eventCalendar.currentTick', 'invalid-calendar');
  assertNonNegativeSafeInteger(calendar.nextInsertionSequence, 'eventCalendar.nextInsertionSequence', 'invalid-calendar');
  if (!Array.isArray(calendar.generations) || !Array.isArray(calendar.events)) {
    throw new ReplayStateValidationError('invalid-calendar', 'eventCalendar generations and events must be arrays');
  }
  calendar.generations.forEach(assertGeneration);
  calendar.events.forEach(assertCalendarEvent);
}

function cloneEventCalendar(calendar: EventCalendarStateProjection<unknown>): EventCalendarStateProjection<unknown> {
  return Object.freeze({
    currentTick: calendar.currentTick,
    nextInsertionSequence: calendar.nextInsertionSequence,
    generations: freezeArray(calendar.generations.map((token) => Object.freeze({ ...token }))),
    events: freezeArray(calendar.events.map((event) => Object.freeze({ ...event }))),
  });
}

function assertEvents(value: unknown): asserts value is readonly GuestSimulationReplayEvent[] {
  if (!Array.isArray(value)) throw new ReplayStateValidationError('invalid-event', 'events must be an array');
  const ids = new Set<string>();
  value.forEach((entry, index) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      throw new ReplayStateValidationError('invalid-event', `Replay event ${index} is not an object`);
    }
    const event = entry as Partial<GuestSimulationReplayEvent>;
    if (!isNonEmptyString(event.id) || !isSimulatedSecond(event.tick) || !isNonEmptyString(event.kind)) {
      throw new ReplayStateValidationError('invalid-event', `Replay event ${index} has an invalid id, tick, or kind`);
    }
    if (ids.has(event.id)) throw new ReplayStateValidationError('invalid-event', `Replay event id ${event.id} is duplicated`);
    ids.add(event.id);
  });
}

function assertRngOrdinals(value: unknown): asserts value is readonly GuestSimulationRngOrdinal[] {
  if (!Array.isArray(value)) throw new ReplayStateValidationError('invalid-rng-ordinal', 'rngOrdinals must be an array');
  const keys = new Set<string>();
  value.forEach((entry, index) => {
    if (!entry || typeof entry !== 'object' || Array.isArray(entry)) {
      throw new ReplayStateValidationError('invalid-rng-ordinal', `RNG ordinal ${index} is not an object`);
    }
    const ordinal = entry as Partial<GuestSimulationRngOrdinal>;
    if (!isNonEmptyString(ordinal.entityId) || !isNonEmptyString(ordinal.domainTag)) {
      throw new ReplayStateValidationError('invalid-rng-ordinal', `RNG ordinal ${index} has an invalid key`);
    }
    assertNonNegativeSafeInteger(ordinal.ordinal, `RNG ordinal ${index}`, 'invalid-rng-ordinal');
    const key = `${ordinal.entityId.length}:${ordinal.entityId}|${ordinal.domainTag.length}:${ordinal.domainTag}`;
    if (keys.has(key)) throw new ReplayStateValidationError('invalid-rng-ordinal', `RNG ordinal ${key} is duplicated`);
    keys.add(key);
  });
}

export function assertGuestSimulationReplayState(value: unknown): asserts value is GuestSimulationReplayState {
  if (!value || typeof value !== 'object' || Array.isArray(value)) {
    throw new ReplayStateValidationError('invalid-state', 'Replay state must be an object');
  }
  const state = value as Partial<GuestSimulationReplayState>;
  if (state.version !== GUEST_SIMULATION_REPLAY_STATE_VERSION) {
    throw new ReplayStateValidationError('invalid-state', `Unsupported replay state version ${String(state.version)}`);
  }
  if (!isGuestSimulationSnapshot(state.snapshot)) {
    throw new ReplayStateValidationError('invalid-state', 'Replay state contains an invalid simulation snapshot');
  }
  if (state.config !== undefined) {
    try {
      assertGuestSimulationConfig(state.config);
    } catch (error) {
      throw new ReplayStateValidationError('invalid-state', error instanceof Error ? error.message : 'Replay state contains an invalid config');
    }
  }
  assertEventCalendar(state.eventCalendar);
  assertEvents(state.events);
  if (!Array.isArray(state.roster) || state.roster.some((guestId) => !isNonEmptyString(guestId))) {
    throw new ReplayStateValidationError('invalid-state', 'Replay roster must contain non-empty guest ids');
  }
  assertRngOrdinals(state.rngOrdinals);
  const guestIds = new Set(state.snapshot.guests.map((guest) => guest.id));
  for (const guestId of state.roster) {
    if (!guestIds.has(guestId)) throw new ReplayStateValidationError('invalid-state', `Replay roster references unknown guest ${guestId}`);
  }
  if (state.eventCalendar.currentTick !== state.snapshot.tick) {
    throw new ReplayStateValidationError('invalid-state', 'Calendar and snapshot ticks do not form one coherent barrier');
  }
}

export function isGuestSimulationReplayState(value: unknown): value is GuestSimulationReplayState {
  try {
    assertGuestSimulationReplayState(value);
    return true;
  } catch {
    return false;
  }
}

function defaultRoster(snapshot: GuestSimulationSnapshot): readonly GuestId[] {
  return freezeArray(snapshot.guests
    .filter((guest) => guest.status !== 'departed')
    .slice()
    .sort((left, right) => left.ordinal - right.ordinal)
    .map((guest) => guest.id));
}

function canonicalOrdinals(ordinals: readonly GuestSimulationRngOrdinal[]): readonly GuestSimulationRngOrdinal[] {
  return freezeArray(ordinals
    .map(freezeOrdinal)
    .slice()
    .sort((left, right) => compareText(left.entityId, right.entityId)
      || compareText(left.domainTag, right.domainTag)));
}

/** Create an immutable state suitable for a coherent checkpoint. */
export function createGuestSimulationReplayState(input: GuestSimulationReplayStateInput): GuestSimulationReplayState {
  const state: GuestSimulationReplayState = {
    version: GUEST_SIMULATION_REPLAY_STATE_VERSION,
    snapshot: input.snapshot,
    eventCalendar: cloneEventCalendar(input.eventCalendar),
    events: freezeArray((input.events ?? []).map(freezeEvent)),
    roster: freezeArray(input.roster ?? defaultRoster(input.snapshot)),
    rngOrdinals: canonicalOrdinals(input.rngOrdinals ?? []),
    ...(input.config === undefined ? {} : { config: input.config }),
  };
  assertGuestSimulationReplayState(state);
  return Object.freeze(state);
}

/** The total key of each live queue entry, in dispatch order. */
export function replayQueueOrder(stateOrCalendar: GuestSimulationReplayState | EventCalendarStateProjection<unknown>): readonly string[] {
  const calendar = 'eventCalendar' in stateOrCalendar ? stateOrCalendar.eventCalendar : stateOrCalendar;
  assertEventCalendar(calendar);
  return freezeArray(calendar.events.map((event) =>
    `${event.tick}|${event.priority}|${event.entityId.length}:${event.entityId}|${event.key.length}:${event.key}|${event.insertionSequence}`));
}

export const queueOrderFromReplayState = replayQueueOrder;

/** A compact projection used by deterministic parity tests and adapters. */
export interface GuestSimulationReplayDeterminismProjection {
  readonly snapshotChecksum: string;
  readonly calendarChecksum: string;
  readonly queueOrder: readonly string[];
  readonly parties: readonly PartyState[];
  readonly events: readonly GuestSimulationReplayEvent[];
  readonly roster: readonly GuestId[];
  readonly rngOrdinals: readonly GuestSimulationRngOrdinal[];
}

export function replayDeterminismProjection(state: GuestSimulationReplayState): GuestSimulationReplayDeterminismProjection {
  assertGuestSimulationReplayState(state);
  return Object.freeze({
    snapshotChecksum: state.snapshot.checksum,
    calendarChecksum: eventCalendarChecksum(state.eventCalendar),
    queueOrder: replayQueueOrder(state),
    parties: freezeArray(state.snapshot.parties),
    events: freezeArray(state.events),
    roster: freezeArray(state.roster),
    rngOrdinals: freezeArray(state.rngOrdinals),
  });
}

export const guestSimulationReplayProjection = replayDeterminismProjection;

/** Hash authoritative replay content using the same portable checksum as the sidecar. */
export function guestSimulationReplayHash(state: GuestSimulationReplayState): string {
  assertGuestSimulationReplayState(state);
  return portableContentHash(encodeBinarySidecar(state));
}

export const replayStateChecksum = guestSimulationReplayHash;

export interface GuestSimulationRenderBufferEntry {
  readonly guestId: GuestId;
  readonly partyId: PartyId;
  readonly status: GuestState['status'];
  readonly currentPortalId: string | null;
  readonly currentResourceId: string | null;
}

export interface GuestSimulationDerivedState {
  readonly guestById: ReadonlyMap<GuestId, GuestState>;
  readonly partyById: ReadonlyMap<PartyId, PartyState>;
  readonly eventById: ReadonlyMap<string, GuestSimulationReplayEvent>;
  readonly queueOrder: readonly string[];
  readonly roster: readonly GuestId[];
  readonly renderBuffer: readonly GuestSimulationRenderBufferEntry[];
}

function immutableMap<K, V>(entries: readonly (readonly [K, V])[]): ReadonlyMap<K, V> {
  return new Map(entries);
}

/** Rebuild every non-authoritative index/cache/render projection after load. */
export function rebuildGuestSimulationDerivedState(state: GuestSimulationReplayState): GuestSimulationDerivedState {
  assertGuestSimulationReplayState(state);
  const guestById = immutableMap(state.snapshot.guests.map((guest) => [guest.id, guest] as const));
  const partyById = immutableMap(state.snapshot.parties.map((party) => [party.id, party] as const));
  const eventById = immutableMap(state.events.map((event) => [event.id, event] as const));
  const renderBuffer = freezeArray(state.snapshot.guests.map((guest) => Object.freeze({
    guestId: guest.id,
    partyId: guest.partyId,
    status: guest.status,
    currentPortalId: guest.currentPortalId,
    currentResourceId: guest.currentResourceId,
  })));
  return Object.freeze({ guestById, partyById, eventById, queueOrder: replayQueueOrder(state),
    roster: freezeArray(state.roster), renderBuffer });
}

export const rebuildDerivedStateAfterLoad = rebuildGuestSimulationDerivedState;

export interface RestoredGuestSimulationState {
  readonly authoritative: GuestSimulationReplayState;
  readonly derived: GuestSimulationDerivedState;
}

export function restoreGuestSimulationReplayState(state: GuestSimulationReplayState): RestoredGuestSimulationState {
  return Object.freeze({ authoritative: state, derived: rebuildGuestSimulationDerivedState(state) });
}

export type ReplayPersistenceErrorCode = 'incoherent-tick' | 'reentrant-capture';

export class ReplayPersistenceError extends Error {
  readonly code: ReplayPersistenceErrorCode;

  constructor(code: ReplayPersistenceErrorCode, message: string) {
    super(message);
    this.name = 'ReplayPersistenceError';
    this.code = code;
  }
}

export interface CoherentTickSnapshotSource<T extends GuestSimulationReplayState = GuestSimulationReplayState> {
  getCurrentTick(): SimulatedSecond;
  captureAuthoritativeState(): T;
}

/**
 * Synchronous barrier around a state capture.  The simulation owner must not
 * advance between the two tick reads.  If it does, the capture is rejected;
 * a half-old/half-new checkpoint is never handed to a storage adapter.
 */
export class CoherentTickSnapshotBarrier<T extends GuestSimulationReplayState = GuestSimulationReplayState> {
  private capturing = false;
  private readonly source: CoherentTickSnapshotSource<T>;

  constructor(source: CoherentTickSnapshotSource<T>) {
    this.source = source;
  }

  get isCapturing(): boolean {
    return this.capturing;
  }

  capture(): T {
    if (this.capturing) throw new ReplayPersistenceError('reentrant-capture', 'A coherent snapshot capture is already in progress');
    this.capturing = true;
    try {
      const startTick = this.source.getCurrentTick();
      if (!isSimulatedSecond(startTick)) throw new ReplayPersistenceError('incoherent-tick', 'Simulation source returned an invalid current tick');
      const state = this.source.captureAuthoritativeState();
      const endTick = this.source.getCurrentTick();
      assertGuestSimulationReplayState(state);
      if (startTick !== endTick || state.snapshot.tick !== startTick || state.eventCalendar.currentTick !== startTick) {
        throw new ReplayPersistenceError('incoherent-tick', 'Simulation advanced during the coherent snapshot barrier');
      }
      return state;
    } finally {
      this.capturing = false;
    }
  }
}

export function captureCoherentTickSnapshot<T extends GuestSimulationReplayState>(source: CoherentTickSnapshotSource<T>): T {
  return new CoherentTickSnapshotBarrier(source).capture();
}

export type CheckpointSlot = 'current' | 'previous';

/** A synchronous adapter; file/IPC/browser implementations remain outside this module. */
export interface CheckpointStorageAdapter {
  read(slot: CheckpointSlot): Uint8Array | undefined;
  /** Commit both slots atomically from the adapter's point of view. */
  commit(current: Uint8Array, previous: Uint8Array | undefined): void;
}

/** In-memory adapter for deterministic tests and worker-local simulations. */
export class InMemoryCheckpointAdapter implements CheckpointStorageAdapter {
  private currentBytes: Uint8Array | undefined;
  private previousBytes: Uint8Array | undefined;

  read(slot: CheckpointSlot): Uint8Array | undefined {
    const bytes = slot === 'current' ? this.currentBytes : this.previousBytes;
    return bytes === undefined ? undefined : bytes.slice();
  }

  commit(current: Uint8Array, previous: Uint8Array | undefined): void {
    this.currentBytes = current.slice();
    this.previousBytes = previous?.slice();
  }

  /** Test-only raw replacement, useful for simulating torn/corrupt storage. */
  replace(slot: CheckpointSlot, bytes: Uint8Array | undefined): void {
    if (slot === 'current') this.currentBytes = bytes?.slice();
    else this.previousBytes = bytes?.slice();
  }

  remove(slot: CheckpointSlot): void {
    this.replace(slot, undefined);
  }

  clear(): void {
    this.currentBytes = undefined;
    this.previousBytes = undefined;
  }
}

export interface CheckpointSaveResult {
  readonly ok: true;
  readonly slot: 'current';
  readonly tick: SimulatedSecond;
  readonly bytes: Uint8Array;
  readonly contentHash: string;
}

export interface CheckpointDiagnostic {
  readonly slot: CheckpointSlot;
  readonly outcome: 'missing' | 'corrupt';
  readonly message: string;
}

export interface CheckpointLoadSuccess {
  readonly ok: true;
  readonly slot: CheckpointSlot;
  readonly state: GuestSimulationReplayState;
  readonly bytes: Uint8Array;
  readonly diagnostics: readonly CheckpointDiagnostic[];
}

export interface CheckpointLoadFailure {
  readonly ok: false;
  readonly outcome: 'missing' | 'corrupt';
  readonly diagnostics: readonly CheckpointDiagnostic[];
}

export type CheckpointLoadResult = CheckpointLoadSuccess | CheckpointLoadFailure;

export interface CheckpointLoadExpectations {
  readonly runId?: string;
  readonly configVersion?: number;
  readonly demandSeed?: string;
  readonly environmentRevision?: number;
  readonly topologyRevision?: number;
}

export interface CheckpointManagerOptions extends BinaryCodecOptions {
  readonly codec?: BinarySidecarCodec<GuestSimulationReplayState>;
}

function describeError(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

function assertExpectedState(state: GuestSimulationReplayState, expected: CheckpointLoadExpectations | undefined): void {
  if (!expected) return;
  const snapshot = state.snapshot;
  if (expected.runId !== undefined && snapshot.runId !== expected.runId) throw new Error(`Checkpoint run id mismatch: expected ${expected.runId}`);
  if (expected.configVersion !== undefined && snapshot.configVersion !== expected.configVersion) throw new Error(`Checkpoint config version mismatch: expected ${expected.configVersion}`);
  if (expected.demandSeed !== undefined && snapshot.demandPlan.seed !== expected.demandSeed) throw new Error(`Checkpoint demand seed mismatch: expected ${expected.demandSeed}`);
  if (expected.environmentRevision !== undefined && snapshot.environmentRevision !== expected.environmentRevision) throw new Error(`Checkpoint environment revision mismatch: expected ${expected.environmentRevision}`);
  if (expected.topologyRevision !== undefined && snapshot.topologyRevision !== expected.topologyRevision) throw new Error(`Checkpoint topology revision mismatch: expected ${expected.topologyRevision}`);
}

/**
 * Current-first recovery manager.  A failed current decode never poisons a
 * valid previous checkpoint; both outcomes are returned explicitly so the UI
 * or desktop adapter can report recovery rather than silently starting over.
 */
export class GuestSimulationCheckpointManager {
  readonly adapter: CheckpointStorageAdapter;
  readonly codec: BinarySidecarCodec<GuestSimulationReplayState>;

  constructor(adapter: CheckpointStorageAdapter, options: CheckpointManagerOptions = {}) {
    this.adapter = adapter;
    this.codec = options.codec ?? new BinarySidecarCodec<GuestSimulationReplayState>(options);
  }

  save(state: GuestSimulationReplayState): CheckpointSaveResult {
    assertGuestSimulationReplayState(state);
    const bytes = this.codec.encode(state);
    const priorCurrent = this.adapter.read('current');
    const priorPrevious = this.adapter.read('previous');
    this.adapter.commit(bytes, priorCurrent ?? priorPrevious);
    const contentHash = readBinarySidecarHeader(bytes).contentHash;
    return Object.freeze({ ok: true, slot: 'current', tick: state.snapshot.tick, bytes: bytes.slice(), contentHash });
  }

  saveCoherent(source: CoherentTickSnapshotSource): CheckpointSaveResult {
    return this.save(new CoherentTickSnapshotBarrier(source).capture());
  }

  load(expected?: CheckpointLoadExpectations): CheckpointLoadResult {
    const diagnostics: CheckpointDiagnostic[] = [];
    let sawBytes = false;
    for (const slot of ['current', 'previous'] as const) {
      const bytes = this.adapter.read(slot);
      if (bytes === undefined) {
        diagnostics.push(Object.freeze({ slot, outcome: 'missing', message: `Checkpoint ${slot} is missing` }));
        continue;
      }
      sawBytes = true;
      try {
        const state = this.codec.decode(bytes);
        assertGuestSimulationReplayState(state);
        assertExpectedState(state, expected);
        return Object.freeze({ ok: true, slot, state, bytes: bytes.slice(), diagnostics: freezeArray(diagnostics) });
      } catch (error) {
        diagnostics.push(Object.freeze({ slot, outcome: 'corrupt', message: describeError(error) }));
      }
    }
    return Object.freeze({ ok: false, outcome: sawBytes ? 'corrupt' : 'missing', diagnostics: freezeArray(diagnostics) });
  }

  restore(expected?: CheckpointLoadExpectations): RestoredGuestSimulationState | CheckpointLoadFailure {
    const result = this.load(expected);
    return result.ok ? restoreGuestSimulationReplayState(result.state) : result;
  }
}

export const CheckpointManager = GuestSimulationCheckpointManager;
export const InMemoryCheckpointStore = InMemoryCheckpointAdapter;
export const GuestSimulationReplayCheckpointManager = GuestSimulationCheckpointManager;

/** Convenience wrappers for adapters that do not need a manager instance. */
export function encodeGuestSimulationReplayState(state: GuestSimulationReplayState, options: BinaryCodecOptions = {}): Uint8Array {
  assertGuestSimulationReplayState(state);
  return encodeBinarySidecar(state, options);
}

export function decodeGuestSimulationReplayState(bytes: Uint8Array, options: BinaryCodecOptions = {}): GuestSimulationReplayState {
  const state = decodeBinarySidecar<GuestSimulationReplayState>(bytes, options);
  assertGuestSimulationReplayState(state);
  return state;
}

export const GuestSimulationReplayCodec = BinarySidecarCodec;
