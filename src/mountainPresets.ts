// Curated North American mountain presets, plus a procedural height
// generator used both for these presets and as a placeholder while live
// terrain downloads.
import type { AreaSizeMeters } from './types';

export interface MountainPreset {
  id: string;
  name: string;
  state: string; // State or Province
  country: string;
  latitude: number;
  longitude: number;
  minAltitude: number; // meters
  maxAltitude: number; // meters
  description: string;
  /** Defaults to 4000 (4km) if omitted. */
  areaSizeMeters?: AreaSizeMeters;
}

export const NA_MOUNTAIN_PRESETS: MountainPreset[] = [
  {
    id: 'crystal-mountain',
    name: 'Crystal Mountain',
    state: 'WA',
    country: 'USA',
    latitude: 46.9354,
    longitude: -121.4748,
    minAltitude: 1341,
    maxAltitude: 2138,
    areaSizeMeters: 8000,
    description: "Washington's largest ski area, in the shadow of Mt. Rainier — big alpine bowls and sustained vertical.",
  },
  {
    id: 'whistler',
    name: 'Whistler Blackcomb',
    state: 'BC',
    country: 'Canada',
    latitude: 50.1163,
    longitude: -122.9574,
    minAltitude: 653,
    maxAltitude: 2284,
    description: 'Massive vertical drop, legendary powder, and over 200 marked runs across two mountains.',
  },
  {
    id: 'vail',
    name: 'Vail Mountain',
    state: 'CO',
    country: 'USA',
    latitude: 39.6061,
    longitude: -106.355,
    minAltitude: 2476,
    maxAltitude: 3527,
    description: 'Iconic back bowls, expansive groomed terrain, and high-altitude Colorado snow.',
  },
  {
    id: 'stowe',
    name: 'Stowe Mountain Resort',
    state: 'VT',
    country: 'USA',
    latitude: 44.5302,
    longitude: -72.7806,
    minAltitude: 390,
    maxAltitude: 1340,
    description: 'Classic New England skiing with narrow trails, icy conditions, and challenging terrain.',
  },
  {
    id: 'palisades',
    name: 'Palisades Tahoe',
    state: 'CA',
    country: 'USA',
    latitude: 39.1962,
    longitude: -120.2351,
    minAltitude: 1890,
    maxAltitude: 2760,
    description: 'Host of the 1960 Winter Olympics. Steep chutes, open bowls, and Sierra cement snow.',
  },
];

/**
 * Generates a procedural height grid (a ridge sloping from top-left/high
 * to bottom-right/low, with a valley and light noise) — used to give
 * curated presets a real-feeling shape without a network fetch.
 */
export function generateProceduralHeights(minAlt: number, maxAlt: number, resolution: number): number[] {
  const heights: number[] = [];
  for (let r = 0; r < resolution; r++) {
    const yFrac = r / (resolution - 1);
    for (let c = 0; c < resolution; c++) {
      const xFrac = c / (resolution - 1);

      const slope = (1.0 - yFrac) * 0.6 + (1.0 - xFrac) * 0.4;
      const valley = Math.sin(xFrac * Math.PI) * 0.15;
      const noise = Math.sin(xFrac * 6) * Math.cos(yFrac * 6) * 0.05 + Math.sin(xFrac * 15) * 0.02;

      const hFraction = Math.max(0, Math.min(1, slope - valley + noise));
      const z = minAlt + (maxAlt - minAlt) * hFraction;
      heights.push(Math.round(z));
    }
  }
  return heights;
}
