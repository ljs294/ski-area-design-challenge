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

  it('round-trips schema-v8 lake and stream property overrides', async () => {
    const save = { ...game('lakes', '2026-05-01T00:00:00.000Z'), schemaVersion: 8 as const,
      lakeDepthOverrides: { 'way/42': 3.75 }, lakeNameOverrides: { 'way/42': 'Mirror Pond' },
      snowmakingLakeIds: ['way/42'],
      streamWidthOverrides: { 'way/7': 4.5 } };
    await saveGame(save);
    expect((await loadGame('lakes'))?.lakeDepthOverrides).toEqual({ 'way/42': 3.75 });
    expect((await loadGame('lakes'))?.snowmakingLakeIds).toEqual(['way/42']);
    expect((await loadGame('lakes'))?.lakeNameOverrides).toEqual({ 'way/42': 'Mirror Pond' });
    expect((await loadGame('lakes'))?.streamWidthOverrides).toEqual({ 'way/7': 4.5 });
  });

  it('round-trips schema-v8 snowmaking dams', async () => {
    const save: GameSave = { ...game('dams', '2026-06-01T00:00:00.000Z'), schemaVersion: 8,
      streamWidthOverrides: { 'way/1': 4.5 },
      dams: [{ id: 'dam-1', name: 'Dam 1', points: [[0, 0], [0.001, 0]],
        crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
        inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
        areaM2: 1000, averageDepthM: 2, capacityM3: 2000, averageDamHeightM: 2.5,
        maxDamHeightM: 4,
        createdAt: '2026-06-01T00:00:00.000Z' }] };
    await saveGame(save);
    expect((await loadGame('dams'))?.dams).toEqual(save.dams);
    expect((await loadGame('dams'))?.streamWidthOverrides).toEqual({ 'way/1': 4.5 });
  });

  it('round-trips schema-v9 standalone ponds', async () => {
    const save: GameSave = { ...game('ponds', '2026-08-01T00:00:00.000Z'), schemaVersion: 9,
      ponds: [{ id: 'pond-1', name: 'Pond 1',
        boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]], topElevationM: 1001,
        areaM2: 1000, averageDepthM: 2, maxDepthM: 3, capacityM3: 2000,
        createdAt: '2026-08-01T00:00:00.000Z' }] };
    await saveGame(save);
    expect((await loadGame('ponds'))?.ponds).toEqual(save.ponds);
  });

  it('round-trips schema-v10 snowmaking pond designations', async () => {
    const save: GameSave = { ...game('pond-designation', '2026-08-01T00:00:00.000Z'), schemaVersion: 10,
      ponds: [{ id: 'pond-2', name: 'Decorative Pond',
        boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]], topElevationM: 1001,
        areaM2: 500, averageDepthM: 1, maxDepthM: 2, capacityM3: 500,
        isSnowmaking: false, createdAt: '2026-08-01T00:00:00.000Z' }] };
    await saveGame(save);
    expect((await loadGame('pond-designation'))?.ponds?.[0].isSnowmaking).toBe(false);
  });

  it('round-trips schema-v11 snowmaking network nodes', async () => {
    const save: GameSave = { ...game('snowmaking-nodes', '2026-08-01T00:00:00.000Z'), schemaVersion: 11,
      dams: [{ id: 'dam-1', name: 'Dam 1', points: [[0, 0], [0.001, 0]],
        crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
        inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
        areaM2: 1000, averageDepthM: 2, capacityM3: 2000, averageDamHeightM: 2.5,
        maxDamHeightM: 4, createdAt: '2026-06-01T00:00:00.000Z' }],
      snowmakingNodes: [{ id: 'node-1', name: 'Dam 1 Intake', kind: 'intake',
        point: [0.0005, 0.0005], elevM: 1000, source: { kind: 'dam', damId: 'dam-1' },
        createdAt: '2026-08-01T00:00:00.000Z' },
        { id: 'node-2', name: 'Hydrant A', kind: 'hydrant', point: [0.001, 0.001],
          elevM: null, createdAt: '2026-08-01T00:00:00.000Z' }] };
    await saveGame(save);
    expect((await loadGame('snowmaking-nodes'))?.snowmakingNodes).toEqual(save.snowmakingNodes);
  });
});
