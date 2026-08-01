import { beforeEach, describe, expect, it } from 'vitest';
import type { GameSave } from './types';
import {
  captureGamePreview,
  deleteGame,
  loadGame,
  loadGamePreview,
  mostRecentGame,
  saveGame,
} from './gameSaveClient';

class MemoryStorage implements Storage {
  private values = new Map<string, string>();
  get length() { return this.values.size; }
  clear() { this.values.clear(); }
  getItem(key: string) { return this.values.get(key) ?? null; }
  key(index: number) { return [...this.values.keys()][index] ?? null; }
  removeItem(key: string) { this.values.delete(key); }
  setItem(key: string, value: string) { this.values.set(key, value); }
}

const game = (key: string, updatedAt: string, lastPlayedAt?: string): GameSave => ({
  schemaVersion: 3,
  key,
  name: key,
  center: [0, 0],
  zoom: 10,
  bearing: 0,
  pitch: 0,
  is3D: false,
  site: null,
  lifts: [],
  trails: [],
  roads: [],
  createdAt: updatedAt,
  updatedAt,
  lastPlayedAt,
});

describe('game preview fallback storage', () => {
  beforeEach(() => {
    Object.defineProperty(globalThis, 'localStorage', {
      configurable: true,
      value: new MemoryStorage(),
    });
  });

  it('stores previews outside the save and removes both together', async () => {
    await saveGame(game('alpine', '2026-01-01T00:00:00.000Z'));
    expect(await captureGamePreview('alpine', 'data:image/jpeg;base64,abc')).toEqual({ ok: true });
    expect(await loadGamePreview('alpine')).toBe('data:image/jpeg;base64,abc');

    await deleteGame('alpine');
    expect(await loadGamePreview('alpine')).toBeNull();
  });

  it('uses the newest explicit save or exit checkpoint for Continue', async () => {
    await saveGame(game('played', '2026-01-01T00:00:00.000Z', '2026-03-01T00:00:00.000Z'));
    await saveGame(game('edited', '2026-04-01T00:00:00.000Z', '2026-02-01T00:00:00.000Z'));
    expect((await mostRecentGame())?.key).toBe('edited');
  });

  it('round-trips schema-v7 lake property overrides', async () => {
    const save = { ...game('lakes', '2026-05-01T00:00:00.000Z'), schemaVersion: 7 as const,
      lakeDepthOverrides: { 'way/42': 3.75 }, lakeNameOverrides: { 'way/42': 'Mirror Pond' } };
    await saveGame(save);
    expect((await loadGame('lakes'))?.lakeDepthOverrides).toEqual({ 'way/42': 3.75 });
    expect((await loadGame('lakes'))?.lakeNameOverrides).toEqual({ 'way/42': 'Mirror Pond' });
  });
});
