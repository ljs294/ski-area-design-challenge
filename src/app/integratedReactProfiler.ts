import type { ProfilerOnRenderCallback } from 'react';

export interface IntegratedReactProfileEntry {
  readonly id: string;
  readonly phase: 'mount' | 'update' | 'nested-update';
  readonly actualDuration: number;
  readonly baseDuration: number;
  readonly startTime: number;
  readonly commitTime: number;
}

const PROFILE_KEY = '__MOUNTAIN_PLANNER_INTEGRATED_REACT_PROFILE__';
const MAX_ENTRIES = 20_000;

interface ProfileRing {
  entries: (IntegratedReactProfileEntry | undefined)[];
  maxEntries: number;
  size: number;
  writeIndex: number;
  dropped: number;
}

export const recordIntegratedReactProfile: ProfilerOnRenderCallback =
  (id, phase, actualDuration, baseDuration, startTime, commitTime) => {
    const target = globalThis as typeof globalThis & Record<string, unknown>;
    const ring = (target[PROFILE_KEY] as ProfileRing | undefined)
      ?? { entries: new Array(MAX_ENTRIES), maxEntries: MAX_ENTRIES, size: 0, writeIndex: 0, dropped: 0 };
    target[PROFILE_KEY] = ring;
    ring.entries[ring.writeIndex] = { id, phase, actualDuration, baseDuration, startTime, commitTime };
    ring.writeIndex = (ring.writeIndex + 1) % MAX_ENTRIES;
    if (ring.size < MAX_ENTRIES) ring.size++;
    else ring.dropped++;
  };
