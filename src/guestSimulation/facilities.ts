/**
 * Dependency-neutral facility contracts for the amenities phase.
 *
 * A facility is intentionally not a building.  It has only the information
 * the guest simulation needs: reachable network entrances, an operating
 * schedule, service offers, and service quality/comfort.  Map/building
 * adapters can project their own objects into this contract.
 */
import type { SimulatedSecond } from './contracts.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';
import type { NeedType } from './needs.ts';

export const FACILITY_CONTRACT_VERSION = 1 as const;
export const FACILITY_FORMULA_VERSION = 1 as const;

export type FacilityKind =
  | 'restaurant'
  | 'cafe'
  | 'shop'
  | 'restroom'
  | 'warming-hut'
  | 'rental'
  | 'lodging'
  | 'other';

export type FacilityServiceKind =
  | 'meal'
  | 'drink'
  | 'warmth'
  | 'restroom'
  | 'retail'
  | 'rental'
  | 'lodging'
  | 'other';

export interface FacilityNetworkEntrance {
  readonly id: string;
  /** Stable network-node identity; no map/building type is required here. */
  readonly nodeId: string;
  /** Travel time from a guest's current network position, in seconds. */
  readonly accessSeconds: number;
  readonly operating?: boolean;
}

export interface FacilitySchedule {
  readonly openFromTick: SimulatedSecond;
  readonly openUntilTick: SimulatedSecond;
}

export interface FacilityInventory {
  readonly enabled: boolean;
  readonly itemId: string;
  readonly capacityUnits: number;
  readonly availableUnits: number;
}

export type NeedRelief = Partial<Readonly<Record<NeedType, number>>>;

export interface FacilityServiceOffer {
  readonly id: string;
  readonly label: string;
  readonly kind: FacilityServiceKind;
  /** Integer cents. Zero is allowed for public restrooms and similar services. */
  readonly priceCents: number;
  readonly serviceSeconds: number;
  /** Concurrent service slots for this offer. */
  readonly capacity: number;
  /** Maximum waiting entries; active entries do not count against this limit. */
  readonly queueCapacity: number;
  readonly quality: number;
  readonly comfort: number;
  readonly operating?: boolean;
  readonly restores: NeedRelief;
  readonly inventory?: FacilityInventory;
}

/** Input form keeps queue/restoration defaults convenient for simple adapters. */
export type FacilityServiceOfferInput = Omit<FacilityServiceOffer, 'queueCapacity' | 'restores'> & {
  readonly queueCapacity?: number;
  readonly restores?: NeedRelief;
};

export interface FacilityContract {
  readonly version: typeof FACILITY_CONTRACT_VERSION;
  readonly formulaVersion: typeof FACILITY_FORMULA_VERSION;
  readonly id: string;
  readonly label: string;
  readonly kind: FacilityKind;
  readonly entrances: readonly FacilityNetworkEntrance[];
  readonly schedule: FacilitySchedule;
  readonly operating: boolean;
  readonly quality: number;
  readonly comfort: number;
  readonly services: readonly FacilityServiceOffer[];
}

/** Compatibility spelling for adapters that call offers "services". */
export type Facility = FacilityContract;
export type FacilityService = FacilityServiceOffer;
export type FacilityEntrance = FacilityNetworkEntrance;
export type FacilityOffer = FacilityServiceOffer;

export type FacilityContractInput = Omit<FacilityContract, 'version' | 'formulaVersion' | 'services'> & {
  readonly version?: typeof FACILITY_CONTRACT_VERSION;
  readonly formulaVersion?: typeof FACILITY_FORMULA_VERSION;
  readonly services: readonly FacilityServiceOfferInput[];
};

function text(value: unknown, label: string): asserts value is string {
  if (typeof value !== 'string' || value.length === 0) throw new RangeError(`${label} must be a non-empty string`);
}

function integer(value: unknown, label: string, minimum = 0): asserts value is number {
  if (!Number.isSafeInteger(value) || (value as number) < minimum) {
    throw new RangeError(`${label} must be an integer >= ${minimum}`);
  }
}

function unit(value: unknown, label: string): asserts value is number {
  if (typeof value !== 'number' || !Number.isFinite(value) || value < 0 || value > 1) {
    throw new RangeError(`${label} must be in [0, 1]`);
  }
}

const FACILITY_KINDS: readonly FacilityKind[] = ['restaurant', 'cafe', 'shop', 'restroom', 'warming-hut', 'rental', 'lodging', 'other'];
const SERVICE_KINDS: readonly FacilityServiceKind[] = ['meal', 'drink', 'warmth', 'restroom', 'retail', 'rental', 'lodging', 'other'];

function enumValue<T extends string>(value: unknown, values: readonly T[], label: string): asserts value is T {
  if (typeof value !== 'string' || !values.includes(value as T)) throw new RangeError(`${label} is invalid`);
}

function cloneRelief(restores: NeedRelief): NeedRelief {
  const result: Record<string, number> = {};
  for (const [key, value] of Object.entries(restores)) {
    enumValue(key, ['hunger', 'thirst', 'warmth', 'restroom', 'fatigue'] as const, 'need relief');
    unit(value, `need relief ${key}`);
    result[key] = value;
  }
  return Object.freeze(result) as NeedRelief;
}

function cloneInventory(inventory: FacilityInventory | undefined): FacilityInventory | undefined {
  if (!inventory) return undefined;
  text(inventory.itemId, 'inventory itemId');
  if (typeof inventory.enabled !== 'boolean') throw new RangeError('inventory enabled must be boolean');
  integer(inventory.capacityUnits, 'inventory capacityUnits');
  integer(inventory.availableUnits, 'inventory availableUnits');
  if (inventory.availableUnits > inventory.capacityUnits) throw new RangeError('inventory availableUnits exceeds capacityUnits');
  return Object.freeze({ ...inventory });
}

function cloneService(service: FacilityServiceOfferInput): FacilityServiceOffer {
  text(service.id, 'service id');
  text(service.label, 'service label');
  enumValue(service.kind, SERVICE_KINDS, 'service kind');
  integer(service.priceCents, 'service priceCents');
  integer(service.serviceSeconds, 'service serviceSeconds');
  integer(service.capacity, 'service capacity', 1);
  const queueCapacity = service.queueCapacity ?? 1_000;
  integer(queueCapacity, 'service queueCapacity');
  unit(service.quality, 'service quality');
  unit(service.comfort, 'service comfort');
  if (service.operating !== undefined && typeof service.operating !== 'boolean') throw new RangeError('service operating must be boolean');
  const base = { id: service.id, label: service.label, kind: service.kind,
    priceCents: service.priceCents, serviceSeconds: service.serviceSeconds, capacity: service.capacity,
    queueCapacity, quality: service.quality, comfort: service.comfort,
    ...(service.operating === undefined ? {} : { operating: service.operating }),
    restores: cloneRelief(service.restores ?? {}) };
  const inventory = cloneInventory(service.inventory);
  return Object.freeze(inventory ? { ...base, inventory } : base);
}

function cloneEntrance(entrance: FacilityNetworkEntrance): FacilityNetworkEntrance {
  text(entrance.id, 'facility entrance id');
  text(entrance.nodeId, 'facility entrance nodeId');
  integer(entrance.accessSeconds, 'facility entrance accessSeconds');
  if (entrance.operating !== undefined && typeof entrance.operating !== 'boolean') throw new RangeError('facility entrance operating must be boolean');
  return Object.freeze({ ...entrance });
}

function validateSchedule(schedule: FacilitySchedule): FacilitySchedule {
  integer(schedule.openFromTick, 'facility openFromTick');
  integer(schedule.openUntilTick, 'facility openUntilTick');
  if (schedule.openUntilTick <= schedule.openFromTick) throw new RangeError('facility schedule must be a non-empty interval');
  return Object.freeze({ ...schedule });
}

/** Construct and freeze a validated, standalone facility contract. */
export function createFacility(input: FacilityContractInput): FacilityContract {
  if (input.version !== undefined && input.version !== FACILITY_CONTRACT_VERSION) throw new RangeError('unsupported facility contract version');
  if (input.formulaVersion !== undefined && input.formulaVersion !== FACILITY_FORMULA_VERSION) throw new RangeError('unsupported facility formula version');
  text(input.id, 'facility id');
  text(input.label, 'facility label');
  enumValue(input.kind, FACILITY_KINDS, 'facility kind');
  const entrances = [...input.entrances].map(cloneEntrance);
  if (entrances.length === 0) throw new RangeError('facility requires at least one network entrance');
  if (new Set(entrances.map((entry) => entry.id)).size !== entrances.length) throw new RangeError('facility entrance ids must be unique');
  const services = [...input.services].map(cloneService);
  if (services.length === 0) throw new RangeError('facility requires at least one service offer');
  if (new Set(services.map((service) => service.id)).size !== services.length) throw new RangeError('facility service ids must be unique');
  if (typeof input.operating !== 'boolean') throw new RangeError('facility operating must be boolean');
  unit(input.quality, 'facility quality');
  unit(input.comfort, 'facility comfort');
  return Object.freeze({ version: FACILITY_CONTRACT_VERSION, formulaVersion: FACILITY_FORMULA_VERSION,
    id: input.id, label: input.label, kind: input.kind, entrances: Object.freeze(entrances),
    schedule: validateSchedule(input.schedule), operating: input.operating, quality: input.quality,
    comfort: input.comfort, services: Object.freeze(services) });
}

export function facilityContractChecksum(facility: FacilityContract): string {
  return eventCalendarChecksum(facility);
}

export function serviceOffer(facility: FacilityContract, serviceId: string): FacilityServiceOffer | undefined {
  return facility.services.find((service) => service.id === serviceId);
}

export function facilityOpenAt(facility: FacilityContract, tick: SimulatedSecond): boolean {
  integer(tick, 'facility tick');
  return facility.operating && tick >= facility.schedule.openFromTick && tick < facility.schedule.openUntilTick;
}

export function entranceOpenAt(entrance: FacilityNetworkEntrance): boolean {
  return entrance.operating !== false;
}

/** Minimum reachable access time, or null when all facility entrances are closed. */
export function minimumFacilityAccessSeconds(facility: FacilityContract): number | null {
  const open = facility.entrances.filter(entranceOpenAt);
  if (open.length === 0) return null;
  return Math.min(...open.map((entrance) => entrance.accessSeconds));
}

export function assertFacilityContract(value: unknown): asserts value is FacilityContract {
  if (!value || typeof value !== 'object' || Array.isArray(value)) throw new RangeError('facility must be an object');
  const facility = value as FacilityContract;
  if (facility.version !== FACILITY_CONTRACT_VERSION || facility.formulaVersion !== FACILITY_FORMULA_VERSION) throw new RangeError('unsupported facility contract version');
  const recreated = createFacility(facility);
  if (facilityContractChecksum(recreated) !== facilityContractChecksum(facility)) throw new RangeError('facility contract checksum mismatch');
}

export function isFacilityContract(value: unknown): value is FacilityContract {
  try { assertFacilityContract(value); return true; } catch { return false; }
}

export const validateFacilityContract = assertFacilityContract;
export const createFacilityContract = createFacility;
