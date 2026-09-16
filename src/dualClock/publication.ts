import type { GuestInspectionSnapshot } from '../types/dualClock';

/**
 * Return the bounded inspection rows sent with a publication while retaining
 * the selected row when it would otherwise fall outside the prefix.
 *
 * This is a presentation boundary only.  The caller still publishes the
 * selected snapshot separately and the input rows are never mutated.
 */
export function boundedGuestInspection(
  guests: readonly GuestInspectionSnapshot[],
  selected: GuestInspectionSnapshot | null,
  limit = 12,
): GuestInspectionSnapshot[] {
  const bounded = guests.slice(0, limit);
  if (selected && !bounded.some(guest => guest.id === selected.id)) {
    if (bounded.length < limit) bounded.push(selected);
    else if (limit > 0) bounded[bounded.length - 1] = selected;
  }
  return bounded;
}
