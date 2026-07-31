import { describe, expect, it } from 'vitest';
import type { GameSave } from '../types';
import { resumeCameraOf, withResumeCheckpoint } from './resumeCheckpoint';

const save: GameSave = {
  schemaVersion: 3,
  key: 'resort',
  name: 'Resort',
  center: [-116.2, 48.4],
  zoom: 13.25,
  bearing: -37,
  pitch: 42,
  is3D: true,
  site: null,
  lifts: [],
  trails: [],
  roads: [],
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-02T00:00:00.000Z',
};

describe('resume checkpoints', () => {
  it('preserves every valid non-default camera component exactly', () => {
    expect(resumeCameraOf(save, {
      center: [0, 0], zoom: 2, bearing: 0, pitch: 0,
    })).toEqual({
      center: [-116.2, 48.4], zoom: 13.25, bearing: -37, pitch: 42,
    });
  });

  it('sanitizes invalid legacy camera data', () => {
    const broken = {
      ...save, center: [Number.NaN, 1000], zoom: Number.POSITIVE_INFINITY,
      bearing: 999, pitch: -10,
    } as GameSave;
    expect(resumeCameraOf(broken, {
      center: [12, 34], zoom: 5, bearing: 6, pitch: 7,
    })).toEqual({
      center: [12, 85.051129], zoom: 5, bearing: 360, pitch: 0,
    });
  });

  it('updates only resume state and keeps explicit-save structures unchanged', () => {
    const persisted = {
      ...save,
      lifts: [{ id: 'persisted-lift' }] as GameSave['lifts'],
      trails: [{ id: 'persisted-trail' }] as GameSave['trails'],
    };
    const next = withResumeCheckpoint(
      persisted,
      { center: [1, 2], zoom: 10, bearing: 11, pitch: 12 },
      false,
      '2026-02-03T04:05:06.000Z'
    );
    expect(next).toMatchObject({
      center: [1, 2], zoom: 10, bearing: 11, pitch: 12, is3D: false,
      lastPlayedAt: '2026-02-03T04:05:06.000Z',
      updatedAt: persisted.updatedAt,
    });
    expect(next.lifts).toBe(persisted.lifts);
    expect(next.trails).toBe(persisted.trails);
  });
});
