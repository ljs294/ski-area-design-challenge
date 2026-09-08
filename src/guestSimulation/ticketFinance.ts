/** Phase 3 prepaid day-ticket revenue. Integer cents and stable IDs make recognition exactly-once. */
import type { Guest, SimulatedSecond } from './contracts.ts';
import { eventCalendarChecksum } from './eventCalendar.ts';

export const TICKET_FINANCE_VERSION = 1 as const;

export interface TicketTransaction {
  readonly id: string;
  readonly bookingId: string;
  readonly guestId: string;
  readonly segment: Guest['preferences']['economicSegment'];
  readonly product: 'day-ticket';
  readonly amountCents: number;
  readonly recognizedTick: SimulatedSecond;
}

export interface FinanceLedgerEntry {
  readonly id: string;
  readonly sequence: number;
  readonly tick: SimulatedSecond;
  readonly kind: 'ticket-revenue';
  readonly amountCents: number;
  readonly transactionId: string;
  readonly guestId: string;
}

export interface TicketFinanceSnapshot {
  readonly version: typeof TICKET_FINANCE_VERSION;
  readonly dayId: string;
  readonly ticketPriceCents: number;
  readonly transactions: readonly TicketTransaction[];
  readonly entries: readonly FinanceLedgerEntry[];
  readonly bookedCount: number;
  readonly recognizedCount: number;
  readonly ticketRevenueCents: number;
  readonly reconciled: boolean;
  readonly checksum: string;
}

function safeCents(value: number, label: string): void {
  if (!Number.isSafeInteger(value) || value <= 0) throw new RangeError(`${label} must be positive integer cents`);
}

function checksum(value: Omit<TicketFinanceSnapshot, 'checksum'>): string {
  return eventCalendarChecksum(value);
}

function snapshotFromTransactions(dayId: string, ticketPriceCents: number,
  transactionsInput: readonly TicketTransaction[]): TicketFinanceSnapshot {
  const transactions = [...transactionsInput].sort((a, b) => a.guestId.localeCompare(b.guestId));
  const entries = transactions.map((transaction, sequence) => Object.freeze({ id: `finance:${transaction.id}`,
    sequence, tick: transaction.recognizedTick, kind: 'ticket-revenue' as const,
    amountCents: transaction.amountCents, transactionId: transaction.id, guestId: transaction.guestId }));
  const ticketRevenueCents = entries.reduce((sum, entry) => {
    const next = sum + entry.amountCents;
    if (!Number.isSafeInteger(next)) throw new RangeError('ticket revenue exceeds safe integer cents');
    return next;
  }, 0);
  const ids = new Set(entries.map((entry) => entry.transactionId));
  const base = Object.freeze({ version: TICKET_FINANCE_VERSION, dayId, ticketPriceCents,
    transactions: Object.freeze(transactions), entries: Object.freeze(entries), bookedCount: transactions.length,
    recognizedCount: ids.size, ticketRevenueCents, reconciled: ids.size === transactions.length
      && ticketRevenueCents === transactions.length * ticketPriceCents });
  return Object.freeze({ ...base, checksum: checksum(base) });
}

export function createPrepaidTicketFinance(input: {
  readonly dayId: string;
  readonly recognizedTick: SimulatedSecond;
  readonly ticketPriceCents: number;
  readonly guests: readonly Guest[];
}): TicketFinanceSnapshot {
  if (!input.dayId) throw new RangeError('dayId is required');
  if (!Number.isSafeInteger(input.recognizedTick) || input.recognizedTick < 0) throw new RangeError('recognizedTick is invalid');
  safeCents(input.ticketPriceCents, 'ticketPriceCents');
  const ordered = [...input.guests].sort((a, b) => a.ordinal - b.ordinal || a.id.localeCompare(b.id));
  if (new Set(ordered.map((guest) => guest.id)).size !== ordered.length) throw new RangeError('guest ids must be unique');
  const transactions = ordered.map((guest) => Object.freeze({ id: `ticket:${input.dayId}:${guest.id}`,
    bookingId: `booking:${input.dayId}:${guest.id}`, guestId: guest.id, segment: guest.preferences.economicSegment,
    product: 'day-ticket' as const, amountCents: input.ticketPriceCents, recognizedTick: input.recognizedTick }));
  return snapshotFromTransactions(input.dayId, input.ticketPriceCents, transactions);
}

/** Idempotently merge ticket transactions from independently replayed batches. */
export function mergePrepaidTicketFinance(left: TicketFinanceSnapshot, right: TicketFinanceSnapshot): TicketFinanceSnapshot {
  if (left.dayId !== right.dayId || left.ticketPriceCents !== right.ticketPriceCents) {
    throw new RangeError('ticket finance snapshots describe different plans');
  }
  const byGuest = new Map<string, TicketTransaction>();
  for (const transaction of [...left.transactions, ...right.transactions]) {
    const prior = byGuest.get(transaction.guestId);
    if (prior && (prior.id !== transaction.id || prior.amountCents !== transaction.amountCents)) {
      throw new RangeError(`conflicting ticket transaction for ${transaction.guestId}`);
    }
    byGuest.set(transaction.guestId, transaction);
  }
  return snapshotFromTransactions(left.dayId, left.ticketPriceCents, [...byGuest.values()]);
}
