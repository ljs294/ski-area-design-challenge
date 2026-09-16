export function gateGuestVibe<T extends { summary: { economy?: object } }>(view: T, blocked: boolean): T {
  if (!blocked || !view.summary.economy) return view;
  return { ...view, summary: { ...view.summary, economy: { ...view.summary.economy, onNextDayTicketPriceChange: undefined } } } as T;
}
