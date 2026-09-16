import { defaultBaseFacilities } from '../guestSimulation/defaultFacilities';
import type { DualAmenity } from './model';

/** Preserve the existing virtual base café until authored facility buildings replace it. */
export function defaultDualAmenities(nodeId: string | null): DualAmenity[] {
  if (!nodeId) return [];
  return defaultBaseFacilities(nodeId, 8 * 3600, 16 * 3600).flatMap(facility => facility.services
    .filter(service => service.kind === 'meal' || service.kind === 'drink').map(service => ({
      id: `${facility.id}:${service.id}`, label: service.label, nodeId, priceCents: service.priceCents,
      capacityPerHour: service.capacity * 3600 / service.serviceSeconds, inventory: service.inventory?.availableUnits ?? 1_000_000,
      opens: 8, closes: 16, accessSeconds: facility.entrances[0].accessSeconds, serviceSeconds: service.serviceSeconds,
      need: service.kind === 'meal' ? 'hunger' as const : 'thirst' as const, relief: { ...service.restores },
    })));
}
