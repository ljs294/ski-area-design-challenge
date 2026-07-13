import maplibregl from 'maplibre-gl';
import type { SavedLift } from '../types';

// Capacity badges pinned to each lift's base terminal. Rendered as MapLibre HTML
// markers (not GL layers) so the chair emblem stays crisp SVG at a constant
// screen size and survives basemap style swaps. Each badge is a classic red
// ski-map plaque holding a white chairlift emblem — the seat count (single …
// quad) reads the carrier size at a glance — plus the hourly capacity caption.

const LIFT_RED = '#d42027';

export interface BadgeEntry {
  marker: maplibregl.Marker;
  sig: string; // rebuild the emblem only when these visual inputs change
}

/**
 * Front-view chairlift emblem: a hanger dropping from the haul rope to a chair
 * carrying `seats` riders (white heads on a seat + footrest bar). Width grows
 * with the seat count so a quad plainly reads wider than a double. Drawn white
 * so it sits on the red plaque.
 */
function emblemSVG(seats: number): string {
  const cell = 13;
  const padX = 10;
  const chairW = seats * cell;
  const W = chairW + padX * 2;
  const H = 44;
  const cx = W / 2;
  const yokeY = 12;
  const seatY = 33;
  const footY = 39;
  const headCy = seatY - 5;
  const headR = 3.4;

  const heads = Array.from({ length: seats }, (_, i) => {
    const hx = padX + cell * (i + 0.5);
    return `<circle cx="${hx.toFixed(1)}" cy="${headCy}" r="${headR}" fill="#fff" />`;
  }).join('');

  // Thin verticals connecting the seat bar down to the footrest.
  const legX1 = padX + 3;
  const legX2 = W - padX - 3;

  return `<svg width="${W}" height="${H}" viewBox="0 0 ${W} ${H}" xmlns="http://www.w3.org/2000/svg" aria-hidden="true">
    <g fill="none" stroke="#fff" stroke-width="2" stroke-linecap="round">
      <line x1="${cx}" y1="6" x2="${cx}" y2="${yokeY}" />
      <line x1="${legX1}" y1="${yokeY}" x2="${legX1}" y2="${seatY}" />
      <line x1="${legX2}" y1="${yokeY}" x2="${legX2}" y2="${seatY}" />
      <line x1="${legX1 + 2}" y1="${seatY}" x2="${legX1 + 2}" y2="${footY}" stroke-width="1.5" />
      <line x1="${legX2 - 2}" y1="${seatY}" x2="${legX2 - 2}" y2="${footY}" stroke-width="1.5" />
    </g>
    <circle cx="${cx}" cy="5" r="2.6" fill="#fff" />
    <rect x="${padX}" y="${yokeY - 1.5}" width="${chairW}" height="3" rx="1.5" fill="#fff" />
    ${heads}
    <rect x="${padX - 1}" y="${seatY - 1.5}" width="${chairW + 2}" height="3.4" rx="1.7" fill="#fff" />
    <rect x="${legX1}" y="${footY - 1.2}" width="${legX2 - legX1}" height="2.4" rx="1.2" fill="#fff" />
  </svg>`;
}

function badgeHTML(lift: SavedLift): string {
  return (
    `<div class="lift-badge-plaque">${emblemSVG(lift.chairSize)}</div>` +
    `<div class="lift-badge-cap">${lift.capacityPph.toLocaleString()}/hr</div>`
  );
}

function makeElement(lift: SavedLift): HTMLDivElement {
  const el = document.createElement('div');
  el.className = 'lift-badge';
  el.style.setProperty('--lift-red', LIFT_RED);
  el.innerHTML = badgeHTML(lift);
  return el;
}

/**
 * Reconcile the on-map capacity badges with the current lift list: create,
 * move, restyle, or remove markers so `store` mirrors `lifts`. `onSelect` fires
 * when a badge is clicked (re-set every call so it never captures stale state).
 */
export function syncLiftBadges(
  map: maplibregl.Map,
  lifts: SavedLift[],
  store: Map<string, BadgeEntry>,
  onSelect: (id: string) => void
): void {
  const seen = new Set<string>();
  for (const lift of lifts) {
    seen.add(lift.id);
    const base = lift.points[0];
    const sig = `${lift.chairSize}|${lift.capacityPph}|${lift.status}`;
    let entry = store.get(lift.id);
    if (!entry) {
      const marker = new maplibregl.Marker({ element: makeElement(lift), anchor: 'bottom', offset: [0, -9] })
        .setLngLat(base)
        .addTo(map);
      entry = { marker, sig };
      store.set(lift.id, entry);
    } else {
      entry.marker.setLngLat(base);
      if (entry.sig !== sig) {
        entry.marker.getElement().innerHTML = badgeHTML(lift);
        entry.sig = sig;
      }
    }
    const el = entry.marker.getElement();
    el.classList.toggle('lift-badge--planning', lift.status === 'planning');
    el.onclick = (e) => {
      e.stopPropagation();
      onSelect(lift.id);
    };
  }
  for (const [id, entry] of store) {
    if (!seen.has(id)) {
      entry.marker.remove();
      store.delete(id);
    }
  }
}

/** Tear down every badge marker (component unmount). */
export function clearLiftBadges(store: Map<string, BadgeEntry>): void {
  for (const entry of store.values()) entry.marker.remove();
  store.clear();
}
