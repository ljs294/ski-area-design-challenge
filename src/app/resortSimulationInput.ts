import type { NetworkEdge } from '../network';
import type { DualAmenity, DualPortal, ResortSimulationInput } from '../dualClock/model';
import type { SavedTrail } from '../types/trails';
import type { IntegratedBenchmarkScenario } from '../integratedBenchmarkScenario';

export const DAILY_DEMAND_BY_WEEKDAY = [1300, 900, 900, 900, 900, 900, 1300] as const;

export function canonicalResortSimulationInput(input: {
  revision: number;
  edges: readonly NetworkEdge[];
  trails: readonly SavedTrail[];
  portal: DualPortal | null;
  ticketPriceCents: number;
  amenities: readonly DualAmenity[];
}, scenario?: IntegratedBenchmarkScenario | null): ResortSimulationInput {
  return {
    revision: input.revision,
    edges: input.edges,
    trails: input.trails,
    portal: input.portal,
    dailyDemand: scenario?.dailyDemand ?? 900,
    dailyDemandByWeekday: scenario?.dailyDemandByWeekday ?? DAILY_DEMAND_BY_WEEKDAY,
    ticketPriceCents: input.ticketPriceCents,
    amenities: scenario?.amenities ?? input.amenities,
  };
}

export function resortSimulationInputKey(input: ResortSimulationInput): string {
  return JSON.stringify({
    edges: input.edges,
    trails: input.trails,
    dailyDemand: input.dailyDemand,
    dailyDemandByWeekday: input.dailyDemandByWeekday,
    portal: input.portal,
    ticketPriceCents: input.ticketPriceCents,
    amenities: input.amenities,
  });
}
