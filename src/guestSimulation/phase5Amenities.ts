/**
 * Phase 5 amenities runtime.
 *
 * The runtime is deliberately independent from map/building models and from
 * the React/Electron application.  It owns only guest comfort state, service
 * selection, FIFO service queues, integer-cent purchases, and a checksummed
 * serializable snapshot.
 */
import type { Guest, GuestPreferences, SimulatedSecond } from './contracts.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';
import {
  createFacility,
  facilityOpenAt,
  minimumFacilityAccessSeconds,
  type FacilityContract,
  type FacilityInventory,
  type FacilityServiceOffer,
} from './facilities.ts';
import {
  DEFAULT_NEED_RATES,
  NEED_TYPES,
  assertNeedState,
  createNeedState,
  relieveNeedState,
  type NeedRates,
  type NeedState,
  type NeedType,
} from './needs.ts';

export const PHASE_5_AMENITIES_VERSION = 1 as const;
export const PHASE_5_AMENITIES_FORMULA_VERSION = 1 as const;
export const DEFAULT_OPEN_ENDED_REMAINING_SECONDS = 43_200;

export type AmenityRequestStatus = 'queued' | 'service' | 'completed' | 'declined' | 'cancelled';
export type AmenityDeclineReason =
  | 'unknown-guest'
  | 'unknown-facility'
  | 'unknown-service'
  | 'closed'
  | 'entrance-closed'
  | 'unaffordable'
  | 'inventory-unavailable'
  | 'queue-full'
  | 'insufficient-time'
  | 'need-not-significant';

export interface AmenityGuestInput {
  readonly guest: Guest;
  readonly needs?: Partial<Readonly<Record<NeedType, number>>>;
  readonly walletCents?: number;
  readonly inventory?: Readonly<Record<string, number>>;
  readonly satisfaction?: number;
}

export interface AmenityGuestSnapshot {
  readonly version: typeof PHASE_5_AMENITIES_VERSION;
  readonly guest: Guest;
  readonly needs: NeedState;
  readonly walletCents: number;
  readonly inventory: Readonly<Record<string, number>>;
  readonly satisfaction: number;
}

export interface AmenityQueueEstimate {
  readonly queueLength?: number;
  readonly activeCount?: number;
}

export interface AmenityChoiceInput {
  readonly guest: Guest;
  readonly needs: NeedState;
  readonly walletCents: number;
  readonly facilities: readonly FacilityContract[];
  readonly currentTick: SimulatedSecond;
  readonly departureTick?: SimulatedSecond | null;
  readonly queueEstimates?: Readonly<Record<string, AmenityQueueEstimate>>;
  readonly accessSecondsByFacility?: Readonly<Record<string, number>>;
}

export interface AmenityChoiceCandidate {
  readonly facilityId: string;
  readonly serviceId: string;
  readonly facility: FacilityContract;
  readonly service: FacilityServiceOffer;
  readonly accessSeconds: number;
  readonly waitSeconds: number;
  readonly serviceSeconds: number;
  readonly remainingSeconds: number;
  readonly needScore: number;
  readonly valueScore: number;
  readonly accessScore: number;
  readonly waitScore: number;
  readonly remainingTimeScore: number;
  readonly preferenceScore: number;
  readonly totalScore: number;
}

export interface AmenityChoice {
  readonly candidate: AmenityChoiceCandidate;
  readonly considered: readonly AmenityChoiceCandidate[];
}

export interface AmenityRequestRecord {
  readonly version: typeof PHASE_5_AMENITIES_VERSION;
  readonly requestId: string;
  readonly guestId: string;
  readonly facilityId: string;
  readonly serviceId: string;
  /** Monotonic arrival order; preserves FIFO for requests at the same tick. */
  readonly queueSequence: number;
  readonly requestedTick: SimulatedSecond;
  readonly startTick: SimulatedSecond | null;
  readonly completionTick: SimulatedSecond | null;
  readonly status: AmenityRequestStatus;
  readonly amountCents: number;
  readonly inventoryItemId: string | null;
  readonly quality: number;
  readonly comfort: number;
  readonly declineReason: AmenityDeclineReason | null;
}

export interface AmenitiesMetrics {
  readonly requests: number;
  readonly acceptedRequests: number;
  readonly completedRequests: number;
  readonly declinedRequests: number;
  readonly activeServices: number;
  readonly queuedServices: number;
  readonly revenueCents: number;
  readonly inventoryUnitsSold: number;
  readonly averageWaitSeconds: number | null;
}

export interface Phase5AmenitiesSnapshot {
  readonly version: typeof PHASE_5_AMENITIES_VERSION;
  readonly formulaVersion: typeof PHASE_5_AMENITIES_FORMULA_VERSION;
  readonly tick: SimulatedSecond;
  readonly facilities: readonly FacilityContract[];
  readonly guests: readonly AmenityGuestSnapshot[];
  readonly requests: readonly AmenityRequestRecord[];
  readonly metrics: AmenitiesMetrics;
  readonly checksum: string;
}

export interface Phase5AmenitiesOptions {
  readonly facilities: readonly FacilityContract[];
  readonly guests: readonly AmenityGuestInput[];
  readonly startTick?: SimulatedSecond;
  readonly needRates?: NeedRates;
}

export interface AmenityRequestInput {
  readonly requestId: string;
  readonly guestId: string;
  readonly facilityId: string;
  readonly serviceId: string;
  readonly accessSeconds?: number;
}

export type AmenityRequestResult =
  | { readonly ok: true; readonly request: AmenityRequestRecord }
  | { readonly ok: false; readonly request: AmenityRequestRecord; readonly reason: AmenityDeclineReason };

interface MutableGuest {
  readonly guest: Guest;
  needs: NeedState;
  walletCents: number;
  inventory: Record<string, number>;
  satisfaction: number;
  lastNeedTick: SimulatedSecond;
  needUnits: Record<NeedType, number>;
}

const NEED_UNIT_SCALE = 1_000_000_000_000;
function needUnits(state: NeedState): Record<NeedType, number> {
  return Object.fromEntries(NEED_TYPES.map((type) => [type, Math.round(state[type] * NEED_UNIT_SCALE)])) as Record<NeedType, number>;
}
function needsFromUnits(units: Readonly<Record<NeedType, number>>): NeedState {
  return createNeedState(Object.fromEntries(NEED_TYPES.map((type) => [type, units[type] / NEED_UNIT_SCALE])));
}

interface MutableFacility {
  contract: FacilityContract;
  inventoryByService: Map<string, number>;
}

interface MutableRequest extends AmenityRequestRecord {
  status: AmenityRequestStatus;
  startTick: SimulatedSecond | null;
  completionTick: SimulatedSecond | null;
}

function text(value: unknown, label: string): asserts value is string {
  if (typeof value !== 'string' || value.length === 0) throw new RangeError(`${label} must be a non-empty string`);
}

function integer(value: unknown, label: string, minimum = 0): asserts value is number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum) throw new RangeError(`${label} must be an integer >= ${minimum}`);
}

function unit(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1) throw new RangeError(`${label} must be in [0, 1]`);
}

function bounded(value: number): number { return Math.max(0, Math.min(1, value)); }

function cloneInventory(inventory: Readonly<Record<string, number>> | undefined): Record<string, number> {
  const result: Record<string, number> = {};
  for (const [itemId, count] of Object.entries(inventory ?? {})) {
    text(itemId, 'guest inventory itemId');
    integer(count, `guest inventory ${itemId}`);
    result[itemId] = count;
  }
  return result;
}

function preference(value: number, fallback: number): number {
  return Number.isFinite(value) ? bounded(value) : fallback;
}

function guestWealth(preferences: GuestPreferences, walletCents: number): number {
  const tripCash = Math.max(0, preferences.tripCashCents);
  return bounded((walletCents + tripCash) / Math.max(1, tripCash * 2));
}

export function facilityServiceKey(facilityId: string, serviceId: string): string {
  text(facilityId, 'facilityId');
  text(serviceId, 'serviceId');
  return `${facilityId.length}:${facilityId}|${serviceId.length}:${serviceId}`;
}

function compareRequests(left: AmenityRequestRecord, right: AmenityRequestRecord): number {
  if (left.requestedTick !== right.requestedTick) return left.requestedTick - right.requestedTick;
  if (left.queueSequence !== right.queueSequence) return left.queueSequence - right.queueSequence;
  if (left.guestId !== right.guestId) return left.guestId < right.guestId ? -1 : 1;
  return left.requestId < right.requestId ? -1 : left.requestId > right.requestId ? 1 : 0;
}

function serviceInventory(service: FacilityServiceOffer): FacilityInventory | undefined {
  return service.inventory;
}

function serviceOpen(service: FacilityServiceOffer): boolean {
  return service.operating !== false;
}

function totalTime(accessSeconds: number, waitSeconds: number, serviceSeconds: number): number {
  return accessSeconds + waitSeconds + serviceSeconds;
}

function estimateWait(service: FacilityServiceOffer, estimate: AmenityQueueEstimate | undefined): number {
  const queued = estimate?.queueLength ?? 0;
  const active = estimate?.activeCount ?? 0;
  integer(queued, 'queue length');
  integer(active, 'active service count');
  if (queued === 0 && active < service.capacity) return 0;
  return Math.ceil((queued + active) / service.capacity) * service.serviceSeconds;
}

function reliefScore(needs: NeedState, service: FacilityServiceOffer): number {
  let total = 0;
  let count = 0;
  for (const type of NEED_TYPES) {
    const relief = service.restores[type] ?? 0;
    total += Math.min(needs[type], relief);
    count += relief > 0 ? 1 : 0;
  }
  return count === 0 ? 0 : total / count;
}

/** Score one offer using need, value, access, wait, remaining time, and preferences. */
export function scoreAmenityService(input: {
  readonly guest: Guest;
  readonly needs: NeedState;
  readonly walletCents: number;
  readonly facility: FacilityContract;
  readonly service: FacilityServiceOffer;
  readonly currentTick: SimulatedSecond;
  readonly departureTick?: SimulatedSecond | null;
  readonly accessSeconds: number;
  readonly waitSeconds?: number;
}): AmenityChoiceCandidate | null {
  assertNeedState(input.needs);
  integer(input.walletCents, 'walletCents');
  integer(input.currentTick, 'currentTick');
  integer(input.accessSeconds, 'accessSeconds');
  if (input.departureTick !== undefined && input.departureTick !== null) integer(input.departureTick, 'departureTick');
  unit(input.facility.quality, 'facility quality');
  unit(input.facility.comfort, 'facility comfort');
  if (input.service.priceCents > input.walletCents) return null;
  if (input.guest.preferences.budgetCents > 0 && input.service.priceCents > input.guest.preferences.budgetCents) return null;
  const waitSeconds = input.waitSeconds ?? 0;
  integer(waitSeconds, 'waitSeconds');
  const remainingSeconds = input.departureTick === undefined || input.departureTick === null
    ? DEFAULT_OPEN_ENDED_REMAINING_SECONDS : input.departureTick - input.currentTick;
  if (remainingSeconds <= 0 || totalTime(input.accessSeconds, waitSeconds, input.service.serviceSeconds) > remainingSeconds) return null;
  const comfortDemand = preference(input.guest.preferences.comfortDemand, 0.5);
  const priceSensitivity = preference(input.guest.preferences.priceSensitivity, 0.5);
  const frugality = preference(input.guest.preferences.frugality, 0.5);
  const patience = preference(input.guest.preferences.patience, 0.5);
  const wealth = guestWealth(input.guest.preferences, input.walletCents);
  const quality = (input.facility.quality + input.service.quality) / 2;
  const comfort = (input.facility.comfort + input.service.comfort) / 2;
  const priceRatio = input.service.priceCents === 0 ? 0 : input.service.priceCents / Math.max(1, input.walletCents);
  const priceValue = bounded(1 - priceRatio * (0.65 + priceSensitivity * 0.75 + frugality * 0.35));
  const valueScore = bounded(quality * 0.55 + comfort * comfortDemand * 0.25 + priceValue * (0.2 + wealth * 0.1));
  const accessScore = 1 / (1 + input.accessSeconds / 300);
  const waitScale = 90 + patience * 510;
  const waitScore = 1 / (1 + waitSeconds / waitScale);
  const remainingTimeScore = bounded(1 - totalTime(input.accessSeconds, waitSeconds, input.service.serviceSeconds) / remainingSeconds);
  const preferenceScore = bounded(quality * (0.5 + comfortDemand * 0.5) + priceValue * (0.5 + priceSensitivity * 0.5)) / 2;
  const needScore = reliefScore(input.needs, input.service);
  const totalScore = bounded(needScore * 0.4 + valueScore * 0.2 + accessScore * 0.1
    + waitScore * 0.1 + remainingTimeScore * 0.1 + preferenceScore * 0.1);
  if (needScore <= 0 && input.service.kind !== 'retail' && input.service.kind !== 'lodging') return null;
  return Object.freeze({ facilityId: input.facility.id, serviceId: input.service.id, facility: input.facility,
    service: input.service, accessSeconds: input.accessSeconds, waitSeconds, serviceSeconds: input.service.serviceSeconds,
    remainingSeconds, needScore, valueScore, accessScore, waitScore, remainingTimeScore, preferenceScore, totalScore });
}

/** Deterministically select the best reachable and affordable offer. */
export function chooseAmenityService(input: AmenityChoiceInput): AmenityChoice | null {
  integer(input.currentTick, 'currentTick');
  const candidates: AmenityChoiceCandidate[] = [];
  for (const facility of [...input.facilities].sort((left, right) => left.id.localeCompare(right.id))) {
    if (!facilityOpenAt(facility, input.currentTick)) continue;
    const accessOverride = input.accessSecondsByFacility?.[facility.id];
    const accessSeconds = accessOverride ?? minimumFacilityAccessSeconds(facility);
    if (accessSeconds === null) continue;
    integer(accessSeconds, `accessSeconds ${facility.id}`);
    for (const service of [...facility.services].sort((left, right) => left.id.localeCompare(right.id))) {
      if (!serviceOpen(service)) continue;
      const waitSeconds = estimateWait(service, input.queueEstimates?.[facilityServiceKey(facility.id, service.id)]);
      const candidate = scoreAmenityService({ ...input, facility, service, accessSeconds, waitSeconds });
      if (candidate) candidates.push(candidate);
    }
  }
  candidates.sort((left, right) => right.totalScore - left.totalScore
    || left.facilityId.localeCompare(right.facilityId) || left.serviceId.localeCompare(right.serviceId));
  const candidate = candidates[0];
  return candidate ? Object.freeze({ candidate, considered: Object.freeze(candidates) }) : null;
}

function snapshotProjection(snapshot: Omit<Phase5AmenitiesSnapshot, 'checksum'>): unknown {
  return { version: snapshot.version, formulaVersion: snapshot.formulaVersion, tick: snapshot.tick,
    facilities: snapshot.facilities, guests: snapshot.guests, requests: snapshot.requests, metrics: snapshot.metrics };
}

export function phase5AmenitiesSnapshotChecksum(snapshot: Omit<Phase5AmenitiesSnapshot, 'checksum'> | Phase5AmenitiesSnapshot): string {
  const { checksum: _checksum, ...base } = snapshot as Phase5AmenitiesSnapshot;
  return eventCalendarChecksum(snapshotProjection(base));
}

function freezeGuestSnapshot(guest: MutableGuest): AmenityGuestSnapshot {
  return Object.freeze({ version: PHASE_5_AMENITIES_VERSION, guest: guest.guest, needs: guest.needs,
    walletCents: guest.walletCents, inventory: Object.freeze({ ...guest.inventory }), satisfaction: guest.satisfaction });
}

function metricsFor(requests: readonly AmenityRequestRecord[]): AmenitiesMetrics {
  const accepted = requests.filter((request) => request.status !== 'declined' && request.status !== 'cancelled');
  const completed = requests.filter((request) => request.status === 'completed');
  const active = requests.filter((request) => request.status === 'service');
  const queued = requests.filter((request) => request.status === 'queued');
  const waits = completed.filter((request) => request.startTick !== null)
    .map((request) => request.startTick! - request.requestedTick);
  const revenue = accepted.reduce((sum, request) => sum + request.amountCents, 0);
  const inventoryUnitsSold = accepted.filter((request) => request.inventoryItemId !== null).length;
  if (!Number.isSafeInteger(revenue)) throw new RangeError('amenity revenue exceeds safe integer cents');
  return Object.freeze({ requests: requests.length, acceptedRequests: accepted.length,
    completedRequests: completed.length, declinedRequests: requests.filter((request) => request.status === 'declined').length,
    activeServices: active.length, queuedServices: queued.length, revenueCents: revenue, inventoryUnitsSold,
    averageWaitSeconds: waits.length === 0 ? null : waits.reduce((sum, value) => sum + value, 0) / waits.length });
}

function toFacilitySnapshot(facility: MutableFacility): FacilityContract {
  const services = facility.contract.services.map((service) => {
    const inventory = serviceInventory(service);
    if (!inventory) return service;
    return Object.freeze({ ...service, inventory: Object.freeze({ ...inventory, availableUnits: facility.inventoryByService.get(service.id) ?? inventory.availableUnits }) });
  });
  return Object.freeze({ ...facility.contract, services: Object.freeze(services) });
}

function requestView(request: MutableRequest): AmenityRequestRecord { return Object.freeze({ ...request }); }

function assertGuestIdentity(guest: Guest): void {
  text(guest.id, 'guest id');
  text(guest.partyId, 'guest partyId');
  integer(guest.ordinal, 'guest ordinal');
  integer(guest.arrivalTick, 'guest arrivalTick');
  if (guest.plannedDepartureTick !== null) integer(guest.plannedDepartureTick, 'guest plannedDepartureTick');
}

function assertRequestRecord(request: AmenityRequestRecord): void {
  if (request.version !== PHASE_5_AMENITIES_VERSION) throw new RangeError('unsupported amenity request version');
  text(request.requestId, 'requestId');
  text(request.guestId, 'request guestId');
  text(request.facilityId, 'request facilityId');
  text(request.serviceId, 'request serviceId');
  integer(request.requestedTick, 'request requestedTick');
  integer(request.queueSequence, 'request queueSequence');
  if (request.startTick !== null) integer(request.startTick, 'request startTick');
  if (request.completionTick !== null) integer(request.completionTick, 'request completionTick');
  integer(request.amountCents, 'request amountCents');
  if (request.inventoryItemId !== null) text(request.inventoryItemId, 'inventoryItemId');
  unit(request.quality, 'request quality');
  unit(request.comfort, 'request comfort');
}

export function assertPhase5AmenitiesSnapshot(value: unknown): asserts value is Phase5AmenitiesSnapshot {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new RangeError('amenities snapshot must be an object');
  const snapshot = value as Phase5AmenitiesSnapshot;
  if (snapshot.version !== PHASE_5_AMENITIES_VERSION || snapshot.formulaVersion !== PHASE_5_AMENITIES_FORMULA_VERSION) throw new RangeError('unsupported amenities snapshot version');
  integer(snapshot.tick, 'snapshot tick');
  if (!Array.isArray(snapshot.facilities) || !Array.isArray(snapshot.guests) || !Array.isArray(snapshot.requests)) throw new RangeError('amenities snapshot collections must be arrays');
  const facilityIds = new Set<string>();
  for (const facility of snapshot.facilities) {
    createFacility(facility);
    if (facilityIds.has(facility.id)) throw new RangeError('duplicate facility in amenities snapshot');
    facilityIds.add(facility.id);
  }
  const guestIds = new Set<string>();
  for (const entry of snapshot.guests) {
    if (entry.version !== PHASE_5_AMENITIES_VERSION) throw new RangeError('unsupported amenities guest version');
    assertGuestIdentity(entry.guest);
    assertNeedState(entry.needs);
    integer(entry.walletCents, 'guest walletCents');
    unit(entry.satisfaction, 'guest satisfaction');
    cloneInventory(entry.inventory);
    if (guestIds.has(entry.guest.id)) throw new RangeError('duplicate guest in amenities snapshot');
    guestIds.add(entry.guest.id);
  }
  const requestIds = new Set<string>();
  for (const request of snapshot.requests) {
    assertRequestRecord(request);
    if (requestIds.has(request.requestId)) throw new RangeError('duplicate amenity request');
    requestIds.add(request.requestId);
    if (!guestIds.has(request.guestId) || !facilityIds.has(request.facilityId)) throw new RangeError('amenity request references missing entity');
    const facility = snapshot.facilities.find((entry) => entry.id === request.facilityId)!;
    if (!facility.services.some((service: FacilityServiceOffer) => service.id === request.serviceId)) throw new RangeError('amenity request references missing service');
    if (request.status === 'service' && (request.startTick === null || request.completionTick === null)) throw new RangeError('active amenity request lacks timing');
    if (request.status === 'completed' && request.completionTick === null) throw new RangeError('completed amenity request lacks completion timing');
  }
  const expected = metricsFor(snapshot.requests);
  if (JSON.stringify(expected) !== JSON.stringify(snapshot.metrics)) throw new RangeError('amenity metrics do not reconcile');
  if (snapshot.checksum !== phase5AmenitiesSnapshotChecksum(snapshot)) throw new RangeError('amenities snapshot checksum mismatch');
}

export function isPhase5AmenitiesSnapshot(value: unknown): value is Phase5AmenitiesSnapshot {
  try { assertPhase5AmenitiesSnapshot(value); return true; } catch { return false; }
}

/** Runtime for needs, purchases, and FIFO amenity service. */
export class Phase5AmenitiesRuntime {
  readonly needRates: NeedRates;
  private tickValue: SimulatedSecond;
  private readonly facilitiesById = new Map<string, MutableFacility>();
  private readonly guestsById = new Map<string, MutableGuest>();
  private readonly requestsById = new Map<string, MutableRequest>();
  private nextQueueSequence = 0;

  constructor(options: Phase5AmenitiesOptions) {
    integer(options.startTick ?? 0, 'startTick');
    this.tickValue = options.startTick ?? 0;
    this.needRates = options.needRates ?? DEFAULT_NEED_RATES;
    for (const source of options.facilities) {
      const contract = createFacility(source);
      if (this.facilitiesById.has(contract.id)) throw new RangeError(`duplicate facility ${contract.id}`);
      const inventoryByService = new Map<string, number>();
      for (const service of contract.services) if (service.inventory) inventoryByService.set(service.id, service.inventory.availableUnits);
      this.facilitiesById.set(contract.id, { contract, inventoryByService });
    }
    for (const source of options.guests) {
      assertGuestIdentity(source.guest);
      if (this.guestsById.has(source.guest.id)) throw new RangeError(`duplicate guest ${source.guest.id}`);
      const walletCents = source.walletCents ?? source.guest.preferences.tripCashCents;
      integer(walletCents, `walletCents ${source.guest.id}`);
      const satisfaction = source.satisfaction ?? 1;
      unit(satisfaction, `satisfaction ${source.guest.id}`);
      const needs = createNeedState(source.needs);
      this.guestsById.set(source.guest.id, { guest: source.guest, needs, needUnits: needUnits(needs), walletCents,
        inventory: cloneInventory(source.inventory), satisfaction, lastNeedTick: this.tickValue });
    }
  }

  get tick(): SimulatedSecond { return this.tickValue; }

  facility(facilityId: string): FacilityContract | undefined {
    return this.facilitiesById.get(facilityId) && toFacilitySnapshot(this.facilitiesById.get(facilityId)!);
  }

  guest(guestId: string): AmenityGuestSnapshot | undefined {
    const guest = this.guestsById.get(guestId);
    return guest ? freezeGuestSnapshot(guest) : undefined;
  }

  requestRecord(requestId: string): AmenityRequestRecord | undefined {
    const request = this.requestsById.get(requestId);
    return request ? requestView(request) : undefined;
  }

  /** Change operating state without changing the facility/building contract. */
  setFacilityOperating(facilityId: string, operating: boolean): void {
    const facility = this.facilitiesById.get(facilityId);
    if (!facility) throw new RangeError(`unknown facility ${facilityId}`);
    if (typeof operating !== 'boolean') throw new RangeError('operating must be boolean');
    facility.contract = Object.freeze({ ...facility.contract, operating });
  }

  chooseForGuest(guestId: string): AmenityChoice | null {
    const guest = this.guestsById.get(guestId);
    if (!guest) return null;
    this.syncGuestNeed(guest, this.tickValue);
    const facilities = [...this.facilitiesById.values()].map(toFacilitySnapshot);
    const queueEstimates: Record<string, AmenityQueueEstimate> = {};
    for (const facility of facilities) for (const service of facility.services) {
      const requests = [...this.requestsById.values()].filter((request) => request.facilityId === facility.id && request.serviceId === service.id);
      queueEstimates[facilityServiceKey(facility.id, service.id)] = {
        queueLength: requests.filter((request) => request.status === 'queued').length,
        activeCount: requests.filter((request) => request.status === 'service').length,
      };
    }
    return chooseAmenityService({ guest: guest.guest, needs: guest.needs, walletCents: guest.walletCents,
      facilities, currentTick: this.tickValue, departureTick: guest.guest.plannedDepartureTick, queueEstimates });
  }

  chooseService(guestId: string): AmenityChoice | null { return this.chooseForGuest(guestId); }

  /** Charge at order time and reserve one inventory unit, then place in FIFO queue. */
  requestService(input: AmenityRequestInput): AmenityRequestResult {
    text(input.requestId, 'requestId');
    if (this.requestsById.has(input.requestId)) throw new RangeError(`duplicate amenity request ${input.requestId}`);
    const guest = this.guestsById.get(input.guestId);
    const facility = this.facilitiesById.get(input.facilityId);
    const service = facility?.contract.services.find((entry) => entry.id === input.serviceId);
    let reason: AmenityDeclineReason | null = null;
    if (!guest) reason = 'unknown-guest';
    else if (!facility) reason = 'unknown-facility';
    else if (!service) reason = 'unknown-service';
    else {
      this.syncGuestNeed(guest, this.tickValue);
      const accessSeconds = input.accessSeconds ?? minimumFacilityAccessSeconds(facility.contract);
      if (!facilityOpenAt(facility.contract, this.tickValue) || !serviceOpen(service!)) reason = 'closed';
      else if (accessSeconds === null) reason = 'entrance-closed';
      else {
        const key = facilityServiceKey(facility.contract.id, service.id);
        const same = [...this.requestsById.values()].filter((entry) => facilityServiceKey(entry.facilityId, entry.serviceId) === key);
        const queued = same.filter((entry) => entry.status === 'queued').length;
        const active = same.filter((entry) => entry.status === 'service').length;
        const estimate = estimateWait(service, { queueLength: queued, activeCount: active });
        const candidate = scoreAmenityService({ guest: guest.guest, needs: guest.needs, walletCents: guest.walletCents,
          facility: facility.contract, service, currentTick: this.tickValue, departureTick: guest.guest.plannedDepartureTick,
          accessSeconds, waitSeconds: estimate });
        if (!candidate) {
          if (service.priceCents > guest.walletCents || (guest.guest.preferences.budgetCents > 0 && service.priceCents > guest.guest.preferences.budgetCents)) reason = 'unaffordable';
          else if (service.inventory?.enabled && (facility.inventoryByService.get(service.id) ?? 0) <= 0) reason = 'inventory-unavailable';
          else reason = 'insufficient-time';
        } else if (queued >= service.queueCapacity) reason = 'queue-full';
        else if (service.inventory?.enabled && (facility.inventoryByService.get(service.id) ?? 0) <= 0) reason = 'inventory-unavailable';
        else if (service.priceCents > guest.walletCents || (guest.guest.preferences.budgetCents > 0 && service.priceCents > guest.guest.preferences.budgetCents)) reason = 'unaffordable';
        else {
          guest.walletCents -= service.priceCents;
          if (service.inventory?.enabled) facility.inventoryByService.set(service.id, (facility.inventoryByService.get(service.id) ?? 0) - 1);
          const record: MutableRequest = { version: PHASE_5_AMENITIES_VERSION, requestId: input.requestId,
            guestId: guest.guest.id, facilityId: facility.contract.id, serviceId: service.id, requestedTick: this.tickValue,
            queueSequence: this.nextQueueSequence++,
            startTick: null, completionTick: null, status: 'queued', amountCents: service.priceCents,
            inventoryItemId: service.inventory?.enabled ? service.inventory.itemId : null,
            quality: (facility.contract.quality + service.quality) / 2, comfort: (facility.contract.comfort + service.comfort) / 2,
            declineReason: null };
          this.requestsById.set(input.requestId, record);
          this.startEligible();
          return { ok: true, request: requestView(record) };
        }
      }
    }
    const declined: MutableRequest = { version: PHASE_5_AMENITIES_VERSION, requestId: input.requestId,
      guestId: input.guestId, facilityId: input.facilityId, serviceId: input.serviceId, requestedTick: this.tickValue,
      queueSequence: this.nextQueueSequence++,
      startTick: null, completionTick: null, status: 'declined', amountCents: 0, inventoryItemId: null,
      quality: 0, comfort: 0, declineReason: reason ?? 'unknown-service' };
    this.requestsById.set(input.requestId, declined);
    return { ok: false, request: requestView(declined), reason: declined.declineReason! };
  }

  request(input: AmenityRequestInput): AmenityRequestResult { return this.requestService(input); }

  chooseAndRequest(guestId: string, requestId: string): AmenityRequestResult | null {
    const choice = this.chooseForGuest(guestId);
    if (!choice) return null;
    return this.requestService({ requestId, guestId, facilityId: choice.candidate.facilityId, serviceId: choice.candidate.serviceId,
      accessSeconds: choice.candidate.accessSeconds });
  }

  /** Advance in explicit seconds; completion order is invariant to chunk size. */
  advanceTo(targetTick: SimulatedSecond): Phase5AmenitiesSnapshot {
    this.advanceClockTo(targetTick);
    return this.snapshot();
  }

  /** Advance without materializing a snapshot; used by the engine event loop. */
  advanceClockTo(targetTick: SimulatedSecond): void {
    integer(targetTick, 'targetTick');
    if (targetTick < this.tickValue) throw new RangeError('targetTick cannot move backwards');
    this.startEligible();
    for (;;) {
      const next = [...this.requestsById.values()].filter((request) => request.status === 'service' && request.completionTick !== null)
        .sort((left, right) => (left.completionTick! - right.completionTick!) || left.requestId.localeCompare(right.requestId))[0];
      if (!next || next.completionTick! > targetTick) break;
      this.tickValue = next.completionTick!;
      for (const request of [...this.requestsById.values()].filter((entry) => entry.status === 'service' && entry.completionTick === this.tickValue)
        .sort((left, right) => left.requestId.localeCompare(right.requestId))) this.completeRequest(request);
      this.startEligible();
    }
    this.tickValue = targetTick;
    this.startEligible();
  }

  advanceBy(seconds: SimulatedSecond): Phase5AmenitiesSnapshot { integer(seconds, 'seconds'); return this.advanceTo(this.tickValue + seconds); }

  /** Internal restore hook; callers should use createPhase5AmenitiesFromSnapshot. */
  restoreRequests(requests: readonly AmenityRequestRecord[]): void {
    if (this.requestsById.size !== 0) throw new RangeError('amenity requests already exist');
    for (const source of requests) {
      assertRequestRecord(source);
      this.requestsById.set(source.requestId, { ...source });
      this.nextQueueSequence = Math.max(this.nextQueueSequence, source.queueSequence + 1);
    }
  }

  snapshot(): Phase5AmenitiesSnapshot {
    this.syncGuestNeeds(this.tickValue);
    const base: Omit<Phase5AmenitiesSnapshot, 'checksum'> = { version: PHASE_5_AMENITIES_VERSION,
      formulaVersion: PHASE_5_AMENITIES_FORMULA_VERSION, tick: this.tickValue,
      facilities: Object.freeze([...this.facilitiesById.values()].sort((left, right) => left.contract.id.localeCompare(right.contract.id)).map(toFacilitySnapshot)),
      guests: Object.freeze([...this.guestsById.values()].sort((left, right) => left.guest.id.localeCompare(right.guest.id)).map(freezeGuestSnapshot)),
      requests: Object.freeze([...this.requestsById.values()].sort(compareRequests).map(requestView)),
      metrics: metricsFor([...this.requestsById.values()]) };
    return Object.freeze({ ...base, checksum: phase5AmenitiesSnapshotChecksum(base) });
  }

  private syncGuestNeeds(targetTick: SimulatedSecond): void {
    if (targetTick < this.tickValue) throw new RangeError('need target cannot move backwards');
    for (const guest of this.guestsById.values()) this.syncGuestNeed(guest, targetTick);
  }

  private syncGuestNeed(guest: MutableGuest, targetTick: SimulatedSecond): void {
    if (targetTick <= guest.lastNeedTick) return;
    const elapsed = targetTick - guest.lastNeedTick;
    for (const type of NEED_TYPES) guest.needUnits[type] = Math.min(NEED_UNIT_SCALE,
      guest.needUnits[type] + elapsed * Math.round(this.needRates[type] * NEED_UNIT_SCALE));
    guest.needs = needsFromUnits(guest.needUnits);
    guest.lastNeedTick = targetTick;
  }

  private startEligible(): void {
    const queued = [...this.requestsById.values()].filter((request) => request.status === 'queued').sort(compareRequests);
    for (const request of queued) {
      if (request.status !== 'queued') continue;
      const facility = this.facilitiesById.get(request.facilityId);
      const service = facility?.contract.services.find((entry) => entry.id === request.serviceId);
      if (!facility || !service || !serviceOpen(service) || !facilityOpenAt(facility.contract, this.tickValue)) continue;
      const activeCount = [...this.requestsById.values()].filter((entry) => entry.status === 'service'
        && entry.facilityId === request.facilityId && entry.serviceId === request.serviceId).length;
      if (activeCount >= service.capacity) continue;
      request.status = 'service';
      request.startTick = this.tickValue;
      request.completionTick = this.tickValue + service.serviceSeconds;
      if (service.serviceSeconds === 0) this.completeRequest(request);
    }
  }

  private completeRequest(request: MutableRequest): void {
    if (request.status !== 'service') return;
    const guest = this.guestsById.get(request.guestId);
    const facility = this.facilitiesById.get(request.facilityId);
    const service = facility?.contract.services.find((entry) => entry.id === request.serviceId);
    if (!guest || !service || request.completionTick === null) return;
    this.syncGuestNeed(guest, request.completionTick);
    guest.needs = relieveNeedState(guest.needs, service.restores);
    guest.needUnits = needUnits(guest.needs);
    guest.satisfaction = bounded(guest.satisfaction * 0.7 + ((facility!.contract.quality + service.quality + facility!.contract.comfort + service.comfort) / 4) * 0.3);
    if (service.inventory?.enabled) guest.inventory[service.inventory.itemId] = (guest.inventory[service.inventory.itemId] ?? 0) + 1;
    request.status = 'completed';
  }
}

export function createPhase5Amenities(options: Phase5AmenitiesOptions): Phase5AmenitiesRuntime {
  return new Phase5AmenitiesRuntime(options);
}

export const createAmenitiesRuntime = createPhase5Amenities;
export const createGuestAmenitiesRuntime = createPhase5Amenities;
export const AmenitiesRuntime = Phase5AmenitiesRuntime;
export const Phase5Amenities = Phase5AmenitiesRuntime;

/** Restore a runtime from its JSON-safe authoritative snapshot. */
export function createPhase5AmenitiesFromSnapshot(snapshot: Phase5AmenitiesSnapshot): Phase5AmenitiesRuntime {
  assertPhase5AmenitiesSnapshot(snapshot);
  const runtime = new Phase5AmenitiesRuntime({ startTick: snapshot.tick, facilities: snapshot.facilities,
    guests: snapshot.guests.map((entry) => ({ guest: entry.guest, needs: entry.needs, walletCents: entry.walletCents,
      inventory: entry.inventory, satisfaction: entry.satisfaction })) });
  runtime.restoreRequests(snapshot.requests);
  return runtime;
}

export const restorePhase5Amenities = createPhase5AmenitiesFromSnapshot;
export const validatePhase5AmenitiesSnapshot = assertPhase5AmenitiesSnapshot;
