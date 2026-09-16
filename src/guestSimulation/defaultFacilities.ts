import { createFacility, type FacilityContract } from './facilities';

export function defaultBaseFacilities(entrance: string, startTick: number, endTick: number): readonly FacilityContract[] {
  return [
    createFacility({ id: 'base-cafe', label: 'Base Café', kind: 'cafe', operating: true,
      quality: 0.62, comfort: 0.68, schedule: { openFromTick: startTick, openUntilTick: endTick },
      entrances: [{ id: 'base-cafe-entrance', nodeId: entrance, accessSeconds: 90 }], services: [
        { id: 'hot-meal', label: 'Hot meal', kind: 'meal', priceCents: 1_800, serviceSeconds: 120,
          capacity: 24, queueCapacity: 180, quality: 0.7, comfort: 0.72,
          restores: { hunger: 0.8, thirst: 0.25, warmth: 0.35, fatigue: 0.12 } },
        { id: 'drink', label: 'Drink', kind: 'drink', priceCents: 450, serviceSeconds: 30,
          capacity: 12, queueCapacity: 120, quality: 0.58, comfort: 0.6, restores: { thirst: 0.8 } },
      ] }),
    createFacility({ id: 'base-services', label: 'Base Services', kind: 'restroom', operating: true,
      quality: 0.55, comfort: 0.58, schedule: { openFromTick: startTick, openUntilTick: endTick },
      entrances: [{ id: 'base-services-entrance', nodeId: entrance, accessSeconds: 60 }], services: [
        { id: 'restroom', label: 'Restroom', kind: 'restroom', priceCents: 0, serviceSeconds: 45,
          capacity: 20, queueCapacity: 160, quality: 0.55, comfort: 0.58, restores: { restroom: 0.95 } },
        { id: 'warm-up', label: 'Warm up', kind: 'warmth', priceCents: 0, serviceSeconds: 180,
          capacity: 40, queueCapacity: 200, quality: 0.6, comfort: 0.7, restores: { warmth: 0.8, fatigue: 0.2 } },
      ] }),
  ];
}

