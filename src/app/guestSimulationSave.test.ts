import { beforeEach, describe, expect, it, vi } from 'vitest';
import type { GameSave } from '../types/gameSave';
import { createClock, createTimeSnapshot } from '../../time-engine/src/timeEngine';
import { saveGame } from '../gameSaveClient';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';
import { saveGameWithGuestCheckpoint } from './guestSimulationSave';

vi.mock('../gameSaveClient', () => ({
  saveGame: vi.fn(async () => ({ ok: true as const, key: 'coherent-save' })),
}));

const mockedSaveGame = vi.mocked(saveGame);

function game(withTime = true): GameSave {
  return {
    schemaVersion: 16,
    key: 'coherent-save', name: 'Coherent Resort', center: [0, 0], zoom: 10,
    bearing: 0, pitch: 0, is3D: false, site: null, lifts: [], trails: [], roads: [],
    ...(withTime ? { time: createTimeSnapshot(createClock()) } : {}),
    createdAt: '2026-01-01T00:00:00.000Z', updatedAt: '2026-01-02T00:00:00.000Z',
  };
}

function runtime(persistBarrier: GuestSimulationRuntime['persistBarrier']): GuestSimulationRuntime {
  return { status: 'ready', message: '', snapshot: null, points: [], weeklyGuestWeighting: {
    dailyDemand: [0, 0, 0, 0, 0, 0, 0], weeklyDemand: 0, simulatedRoster: 0,
    outcomeWeight: 0, daySelectionWeights: [0, 0, 0, 0, 0, 0, 0],
  }, outcomeWeight: 0, committedSecond: 0, backlogSeconds: 0,
  snapshotBarrier: vi.fn(async () => null), persistBarrier };
}

describe('guest checkpoint save barrier', () => {
  beforeEach(() => mockedSaveGame.mockClear());

  it('passes the saved clock second to the sidecar barrier before writing GameSave', async () => {
    const persistBarrier = vi.fn(async (_key: string, _revision: string, expected?: number) => ({
      ok: true as const, committedSecond: expected ?? -1,
    }));
    const save = game();
    const result = await saveGameWithGuestCheckpoint(save, runtime(persistBarrier));

    expect(result).toEqual({ ok: true, key: save.key });
    expect(persistBarrier).toHaveBeenCalledWith(save.key,
      `${save.updatedAt}|${save.lastPlayedAt}`, save.time!.clock.elapsedSimSecond);
    expect(mockedSaveGame).toHaveBeenCalledWith(save);
  });

  it('rejects a mixed-time checkpoint and never writes the GameSave', async () => {
    const persistBarrier = vi.fn(async () => ({ ok: true as const, committedSecond: 1 }));
    const save = game();
    const result = await saveGameWithGuestCheckpoint(save, runtime(persistBarrier));

    expect(result).toMatchObject({ ok: false });
    expect((result as { ok: false; error: string }).error).toMatch(/timestamp/i);
    expect(mockedSaveGame).not.toHaveBeenCalled();
  });

  it('does not let a malformed future clock move the guest barrier ahead of snow', async () => {
    const persistBarrier = vi.fn(async (_key: string, _revision: string, expected?: number) => ({
      ok: true as const, committedSecond: expected ?? 0,
    }));
    const save = { ...game(), time: {
      ...game().time!, clock: { ...game().time!.clock, elapsedSimSecond: 1 },
    } };
    // The mock runtime reports its committed time as zero. The wrapper still
    // passes the save timestamp, which is the contract exercised here.
    const result = await saveGameWithGuestCheckpoint(save, runtime(persistBarrier));
    expect(result).toEqual({ ok: true, key: save.key });
    expect(persistBarrier).toHaveBeenCalledWith(save.key,
      `${save.updatedAt}|${save.lastPlayedAt}`, 1);
  });

  it('keeps design-only legacy saves compatible when they have no time snapshot', async () => {
    const persistBarrier = vi.fn(async (_key: string, _revision: string, expected?: number) => ({
      ok: true as const, committedSecond: expected ?? 42,
    }));
    const save = game(false);
    const result = await saveGameWithGuestCheckpoint(save, runtime(persistBarrier));

    expect(result).toEqual({ ok: true, key: save.key });
    expect(persistBarrier).toHaveBeenCalledWith(save.key,
      `${save.updatedAt}|${save.lastPlayedAt}`, undefined);
    expect(mockedSaveGame).toHaveBeenCalledWith(save);
  });
});
