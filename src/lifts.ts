import { haversineMeters } from './geo';
import type { LiftCategoryId, LiftStatus, LiftTypeId, SavedLift } from './types/lifts';

const M_TO_FT = 3.28084;
const FPM_TO_MPS = 0.00508;

export const TRAM_DWELL_S = 4 * 60;
export const DEFAULT_LIFT_TYPE_ID: LiftTypeId = 'fixed-grip-double';

export interface LiftCategory {
  id: LiftCategoryId;
  label: string;
}

export const LIFT_CATEGORIES: readonly LiftCategory[] = [
  { id: 'surface', label: 'Surface Lift' },
  { id: 'fixed-grip-chairlift', label: 'Fixed-Grip Chairlift' },
  { id: 'detachable-chairlift', label: 'Detachable Chairlift' },
  { id: 'detachable-gondola', label: 'Detachable Gondola' },
  { id: 'tram', label: 'Tram' },
];

export type LiftCapacityRule =
  | { kind: 'fixed'; capacityPph: number }
  | { kind: 'tram-cycle'; cabinSize: 60 | 80; dwellS: number };

export interface LiftTypeSpec {
  id: LiftTypeId;
  categoryId: LiftCategoryId;
  /** Full label used outside the chooser. */
  label: string;
  /** Leaf label shown beneath its category in the chooser. */
  optionLabel: string;
  operatingSpeedFpm: number;
  capacity: LiftCapacityRule;
}

export const LIFT_TYPE_SPECS: readonly LiftTypeSpec[] = [
  { id: 'rope-tow', categoryId: 'surface', label: 'Rope Tow', optionLabel: 'Rope Tow',
    operatingSpeedFpm: 500, capacity: { kind: 'fixed', capacityPph: 700 } },
  { id: 'magic-carpet', categoryId: 'surface', label: 'Magic Carpet', optionLabel: 'Magic Carpet',
    operatingSpeedFpm: 100, capacity: { kind: 'fixed', capacityPph: 1000 } },
  { id: 't-bar', categoryId: 'surface', label: 'T-Bar', optionLabel: 'T-Bar',
    operatingSpeedFpm: 400, capacity: { kind: 'fixed', capacityPph: 1200 } },
  { id: 'fixed-grip-double', categoryId: 'fixed-grip-chairlift',
    label: 'Fixed-Grip Double Chairlift', optionLabel: 'Double', operatingSpeedFpm: 400,
    capacity: { kind: 'fixed', capacityPph: 1200 } },
  { id: 'fixed-grip-triple', categoryId: 'fixed-grip-chairlift',
    label: 'Fixed-Grip Triple Chairlift', optionLabel: 'Triple', operatingSpeedFpm: 400,
    capacity: { kind: 'fixed', capacityPph: 1800 } },
  { id: 'fixed-grip-quad', categoryId: 'fixed-grip-chairlift',
    label: 'Fixed-Grip Quad Chairlift', optionLabel: 'Quad', operatingSpeedFpm: 400,
    capacity: { kind: 'fixed', capacityPph: 2400 } },
  { id: 'detachable-quad', categoryId: 'detachable-chairlift',
    label: 'Detachable Quad Chairlift', optionLabel: 'Quad', operatingSpeedFpm: 1000,
    capacity: { kind: 'fixed', capacityPph: 2400 } },
  { id: 'detachable-six-pack', categoryId: 'detachable-chairlift',
    label: 'Detachable Six-Pack Chairlift', optionLabel: 'Six-Pack', operatingSpeedFpm: 1000,
    capacity: { kind: 'fixed', capacityPph: 3000 } },
  { id: 'detachable-eight-pack', categoryId: 'detachable-chairlift',
    label: 'Detachable Eight-Pack Chairlift', optionLabel: 'Eight-Pack', operatingSpeedFpm: 1000,
    capacity: { kind: 'fixed', capacityPph: 3200 } },
  { id: 'gondola-8', categoryId: 'detachable-gondola',
    label: 'Detachable 8-Person Gondola', optionLabel: '8-person', operatingSpeedFpm: 1000,
    capacity: { kind: 'fixed', capacityPph: 2400 } },
  { id: 'gondola-10', categoryId: 'detachable-gondola',
    label: 'Detachable 10-Person Gondola', optionLabel: '10-person', operatingSpeedFpm: 1000,
    capacity: { kind: 'fixed', capacityPph: 2800 } },
  { id: 'gondola-12', categoryId: 'detachable-gondola',
    label: 'Detachable 12-Person Gondola', optionLabel: '12-person', operatingSpeedFpm: 1000,
    capacity: { kind: 'fixed', capacityPph: 3000 } },
  { id: 'tram-60', categoryId: 'tram', label: '60-Person Aerial Tram', optionLabel: '60-person',
    operatingSpeedFpm: 2000, capacity: { kind: 'tram-cycle', cabinSize: 60, dwellS: TRAM_DWELL_S } },
  { id: 'tram-80', categoryId: 'tram', label: '80-Person Aerial Tram', optionLabel: '80-person',
    operatingSpeedFpm: 2000, capacity: { kind: 'tram-cycle', cabinSize: 80, dwellS: TRAM_DWELL_S } },
];

export const LIFT_TYPE_CATALOG = Object.fromEntries(
  LIFT_TYPE_SPECS.map((spec) => [spec.id, spec]),
) as Record<LiftTypeId, LiftTypeSpec>;

const LIFT_TYPE_IDS = new Set<string>(LIFT_TYPE_SPECS.map((spec) => spec.id));

export function isLiftTypeId(value: unknown): value is LiftTypeId {
  return typeof value === 'string' && LIFT_TYPE_IDS.has(value);
}

export interface LiftPerformance {
  operatingSpeedFpm: number;
  operatingSpeedMps: number;
  rideTimeS: number;
  capacityPph: number;
}

/** Shared builder/network performance model. Length is the two-terminal slope length. */
export function liftPerformance(liftTypeId: LiftTypeId, lengthM: number): LiftPerformance {
  const spec = LIFT_TYPE_CATALOG[liftTypeId];
  const operatingSpeedMps = spec.operatingSpeedFpm * FPM_TO_MPS;
  const rideTimeS = Math.max(0, lengthM) / operatingSpeedMps;
  const capacityPph = spec.capacity.kind === 'fixed'
    ? spec.capacity.capacityPph
    : (spec.capacity.cabinSize * 3600) / (rideTimeS + spec.capacity.dwellS);
  return { operatingSpeedFpm: spec.operatingSpeedFpm, operatingSpeedMps, rideTimeS, capacityPph };
}

export function liftTypeLabel(liftTypeId: LiftTypeId): string {
  return LIFT_TYPE_CATALOG[liftTypeId].label;
}

export interface LiftStats {
  horizontalM: number;
  /** Slope length: hypot(horizontal, vertical); horizontal-only when elevations unknown. */
  lengthM: number;
  verticalM: number | null;
  /** Index into points of the top terminal, or null while elevations unresolved. */
  topIndex: 0 | 1 | null;
}

export function liftStats(
  points: [[number, number], [number, number]],
  elevs: [number | null, number | null],
): LiftStats {
  const horizontalM = haversineMeters(points[0], points[1]);
  const [a, b] = elevs;
  if (a == null || b == null) {
    return { horizontalM, lengthM: horizontalM, verticalM: null, topIndex: null };
  }
  const verticalM = Math.abs(b - a);
  return {
    horizontalM,
    lengthM: Math.hypot(horizontalM, verticalM),
    verticalM,
    topIndex: b >= a ? 1 : 0,
  };
}

/** Reorder so index 0 is the bottom terminal. No-op while elevations are unknown. */
export function orientBottomToTop(
  points: [[number, number], [number, number]],
  elevs: [number | null, number | null],
): { points: [[number, number], [number, number]]; elevs: [number | null, number | null] } {
  const [a, b] = elevs;
  if (a != null && b != null && a > b) {
    return { points: [points[1], points[0]], elevs: [b, a] };
  }
  return { points, elevs };
}

function isLngLat(p: unknown): p is [number, number] {
  return (
    Array.isArray(p) &&
    p.length === 2 &&
    typeof p[0] === 'number' &&
    typeof p[1] === 'number' &&
    Number.isFinite(p[0]) &&
    Number.isFinite(p[1])
  );
}

/**
 * Hydration shield for `GameSave.lifts`. Schema 1-13 fixed-grip lifts migrate
 * from chairSize to the schema-14 leaf discriminator; current lifts validate
 * that discriminator. Cached length/vertical are always recomputed.
 */
export function sanitizeLifts(raw: unknown[]): SavedLift[] {
  const out: SavedLift[] = [];
  for (const item of raw) {
    if (typeof item !== 'object' || item === null) continue;
    const lift = item as Record<string, unknown>;
    if (typeof lift.id !== 'string' || typeof lift.name !== 'string') continue;
    if (!Array.isArray(lift.points) || lift.points.length !== 2) continue;
    if (!isLngLat(lift.points[0]) || !isLngLat(lift.points[1])) continue;
    const points: [[number, number], [number, number]] = [lift.points[0], lift.points[1]];
    const rawElevs = Array.isArray(lift.endpointElevM) ? lift.endpointElevM : [null, null];
    const elevs: [number | null, number | null] = [
      typeof rawElevs[0] === 'number' && Number.isFinite(rawElevs[0]) ? rawElevs[0] : null,
      typeof rawElevs[1] === 'number' && Number.isFinite(rawElevs[1]) ? rawElevs[1] : null,
    ];
    let liftTypeId: LiftTypeId;
    if ('liftTypeId' in lift) {
      if (!isLiftTypeId(lift.liftTypeId)) continue;
      liftTypeId = lift.liftTypeId;
    } else {
      if (lift.liftClass !== 'fixed-grip') continue;
      liftTypeId = lift.chairSize === 3 ? 'fixed-grip-triple'
        : lift.chairSize === 4 ? 'fixed-grip-quad'
          : DEFAULT_LIFT_TYPE_ID;
    }
    const status: LiftStatus = lift.status === 'planning' || lift.status === 'complete'
      ? lift.status
      : 'complete';
    const stats = liftStats(points, elevs);
    out.push({
      id: lift.id,
      identifier: typeof lift.identifier === 'string' && lift.identifier.trim()
        ? lift.identifier.trim()
        : undefined,
      name: lift.name,
      liftTypeId,
      points,
      endpointElevM: elevs,
      lengthM: stats.lengthM,
      verticalM: stats.verticalM,
      status,
      closed: lift.closed === true,
      createdAt: typeof lift.createdAt === 'string' ? lift.createdAt : new Date().toISOString(),
    });
  }
  return out;
}

/** Map/UI label, preserving the legacy name-only form when no identifier exists. */
export function formatLiftLabel(
  lift: Pick<SavedLift, 'identifier' | 'name'>,
): string {
  const identifier = lift.identifier?.trim() ?? '';
  const name = lift.name.trim();
  if (identifier && name) return `${identifier} - ${name}`;
  return identifier || name;
}

function reservedLiftNumbers(existing: readonly SavedLift[]): Set<string> {
  const taken = new Set<string>();
  for (const lift of existing) {
    const identifier = lift.identifier?.trim();
    if (identifier && /^\d+$/.test(identifier)) taken.add(String(Number(identifier)));
    const legacyNumber = /^Lift\s+(\d+)$/i.exec(lift.name.trim())?.[1];
    if (legacyNumber) taken.add(String(Number(legacyNumber)));
  }
  return taken;
}

/** First number unused by either an identifier or a legacy/default "Lift N" name. */
export function nextLiftIdentifier(existing: readonly SavedLift[]): string {
  const taken = reservedLiftNumbers(existing);
  for (let n = 1; ; n++) {
    const identifier = String(n);
    if (!taken.has(identifier)) return identifier;
  }
}

/** First "Lift N" not already taken. */
export function nextLiftName(existing: readonly SavedLift[]): string {
  const takenNames = new Set(existing.map((lift) => lift.name));
  const takenNumbers = reservedLiftNumbers(existing);
  for (let n = 1; ; n++) {
    const name = `Lift ${n}`;
    if (!takenNames.has(name) && !takenNumbers.has(String(n))) return name;
  }
}

export function fmtDistance(m: number, units: 'imperial' | 'metric'): string {
  return units === 'imperial'
    ? `${Math.round(m * M_TO_FT).toLocaleString()} ft`
    : `${Math.round(m).toLocaleString()} m`;
}
