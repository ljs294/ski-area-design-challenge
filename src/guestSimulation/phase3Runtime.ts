/** Small mutable orchestration seam between the event engine and pure Phase 3 ledgers. */
import type { GuestState, SimulatedSecond } from './contracts.ts';
import type { DailyGuestRoster } from './engineSupport.ts';
import type { DemandForecastV1, DemandRealizationV1 } from './demand.ts';
import { createPrepaidTicketFinance } from './ticketFinance.ts';
import {
  closePhase3Economy,
  createPhase3Economy,
  recordVisitOutcomes,
  type Phase3EconomySnapshot,
  type ReputationProfile,
  type VisitOutcomeInput,
} from './phase3Economy.ts';

export interface Phase3RuntimeInput {
  readonly dayId: string;
  readonly ticketPriceCents: number;
  readonly demandForecast?: DemandForecastV1;
  readonly demandRealization?: DemandRealizationV1;
  readonly openingReputation?: ReputationProfile;
}

export interface Phase3SimulationSnapshot {
  readonly demandForecast: DemandForecastV1 | null;
  readonly demandRealization: DemandRealizationV1 | null;
  readonly economy: Phase3EconomySnapshot;
  readonly bookedGuests: number;
  readonly arrivedGuests: number;
  readonly activeGuests: number;
  readonly departedGuests: number;
  readonly turnedAwayGuests: 0;
  readonly reconciled: boolean;
}

export class Phase3Runtime {
  private economyValue: Phase3EconomySnapshot;
  private readonly pendingOutcomes = new Map<string, VisitOutcomeInput>();
  private readonly roster: DailyGuestRoster;
  private readonly input: Phase3RuntimeInput;

  constructor(roster: DailyGuestRoster, input: Phase3RuntimeInput) {
    this.roster = roster;
    this.input = input;
    const ticketFinance = createPrepaidTicketFinance({ dayId: input.dayId,
      recognizedTick: roster.demandPlan.startTick, ticketPriceCents: input.ticketPriceCents,
      guests: roster.guests });
    this.economyValue = createPhase3Economy({ dayId: input.dayId,
      openingReputation: input.openingReputation, ticketFinance });
  }

  recordDeparture(guest: GuestState, tick: SimulatedSecond): void {
    this.pendingOutcomes.set(guest.id, { dayId: this.input.dayId,
      guestId: guest.id, segment: guest.preferences.economicSegment, tick: guest.plannedDepartureTick ?? tick,
      satisfaction: guest.satisfaction,
      dimensionScores: { terrain: guest.satisfaction, comfort: guest.satisfaction,
        value: guest.satisfaction, service: guest.satisfaction, safety: guest.status === 'patrol-response' ? 0.05 : 1 } });
  }

  snapshot(tick: SimulatedSecond, guests: readonly GuestState[]): Phase3SimulationSnapshot {
    if (tick >= this.roster.demandPlan.endTick && guests.every((guest) => guest.status === 'departed')
      && !this.economyValue.closed) {
      this.economyValue = recordVisitOutcomes(this.economyValue, [...this.pendingOutcomes.values()]);
      this.economyValue = closePhase3Economy(this.economyValue,
        { closeId: `close:${this.input.dayId}`, closedTick: this.roster.demandPlan.endTick });
    }
    const scheduled = guests.filter((guest) => guest.status === 'scheduled' || guest.status === 'arriving').length;
    const departedGuests = guests.filter((guest) => guest.status === 'departed').length;
    const activeGuests = guests.length - scheduled - departedGuests;
    const arrivedGuests = activeGuests + departedGuests;
    const bookedGuests = guests.length;
    return Object.freeze({ demandForecast: this.input.demandForecast ?? null,
      demandRealization: this.input.demandRealization ?? null, economy: this.economyValue,
      bookedGuests, arrivedGuests, activeGuests, departedGuests, turnedAwayGuests: 0 as const,
      reconciled: bookedGuests === scheduled + activeGuests + departedGuests
        && this.economyValue.metrics.ticketCount === bookedGuests });
  }
}
