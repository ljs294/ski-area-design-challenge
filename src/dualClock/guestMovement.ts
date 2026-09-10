import { uniformOpen01 } from '../guestSimulation/random';
import type { NetworkEdge } from '../network';
import type { GuestMovementConfig, RepresentativeGuest, TransitRoute } from '../types/dualClock';
import { DEFAULT_GUEST_MOVEMENT } from './model';

/**
 * Deterministically sample a standard normal and reject values outside the
 * designer's truncation bound. The bounded fallback is only a guard against a
 * pathological keyed sequence; it keeps every returned value in the contract.
 */
export function truncatedStandardNormal(seed: string, identity: string,
  config: Pick<GuestMovementConfig, 'speedDomainTag' | 'speedNormalLimit'> = DEFAULT_GUEST_MOVEMENT): number {
  const limit = Math.max(0, config.speedNormalLimit);
  for (let salt = 0; salt < 32; salt++) {
    const u1 = uniformOpen01(seed, identity, config.speedDomainTag, salt * 2);
    const u2 = uniformOpen01(seed, identity, config.speedDomainTag, salt * 2 + 1);
    const sample = Math.sqrt(-2 * Math.log(u1)) * Math.cos(2 * Math.PI * u2);
    if (Math.abs(sample) <= limit) return sample;
  }
  const u = uniformOpen01(seed, identity, config.speedDomainTag, 65);
  return (u * 2 - 1) * limit;
}

export function guestAbilityFactor(ability: number): number {
  return 0.6 + 0.8 * Math.max(0, Math.min(1, ability));
}

export function guestSpeedMultiplier(ability: number, speedZ: number): number {
  return guestAbilityFactor(ability) * (1 + 0.15 * speedZ);
}

/**
 * Apply the existing edge travel-time baseline to one representative guest.
 * Lift service time stays authoritative; the engine calls this for trail
 * segments while connector and path walking keep their existing baseline.
 */
export function guestTravelDuration(edge: Pick<{ lengthM: number; travelTimeS: number }, 'lengthM' | 'travelTimeS'>,
  ability: number, speedZ: number): number {
  const baselineSpeed = edge.travelTimeS > 0 ? edge.lengthM / edge.travelTimeS : 0;
  const speed = baselineSpeed * guestSpeedMultiplier(ability, speedZ);
  if (!(speed > 0) || !Number.isFinite(speed)) return Math.max(1, edge.travelTimeS);
  return Math.max(0.000001, edge.lengthM / speed);
}

export function deriveGuestSpeedZ(seed: string, guestId: string, config?: GuestMovementConfig): number {
  return truncatedStandardNormal(seed, guestId, config ?? DEFAULT_GUEST_MOVEMENT);
}

export function ensureGuestSpeed(seed: string, guest: RepresentativeGuest, config?: GuestMovementConfig): number {
  if (guest.speedZ === undefined || !Number.isFinite(guest.speedZ)) {
    guest.speedZ = deriveGuestSpeedZ(seed, guest.id, config);
  }
  return guest.speedZ;
}

export function representativeGuestDuration(seed: string, guest: RepresentativeGuest,
  edge: NetworkEdge | TransitRoute, config?: GuestMovementConfig): number {
  if (edge.kind !== 'trail') return edge.travelTimeS > 0 ? edge.travelTimeS : 1;
  const speedZ = ensureGuestSpeed(seed, guest, config);
  return guestTravelDuration(edge, guest.ability, speedZ);
}
