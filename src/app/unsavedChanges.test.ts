import { describe, expect, it, vi } from 'vitest';
import type { GameSave, TerrainRecord } from '../types';
import { withResumeCheckpoint } from './resumeCheckpoint';
import {
  TERRAIN_CLEAN,
  designHasEdits,
  designOf,
  flushTerrainEdits,
  terrainHasEdits,
  withTerrainEdit,
} from './unsavedChanges';

const save: GameSave = {
  schemaVersion: 10,
  key: 'resort',
  name: 'Resort',
  center: [-121.5, 46.9],
  zoom: 13.25,
  bearing: -37,
  pitch: 42,
  is3D: true,
  site: null,
  lifts: [],
  trails: [],
  roads: [],
  dams: [],
  ponds: [],
  createdAt: '2026-01-01T00:00:00.000Z',
  updatedAt: '2026-01-02T00:00:00.000Z',
};

// Only the fields flushTerrainEdits actually reads.
const vectorPackage = {
  key: 'terrain',
  coverDisplayGeometry: { type: 'FeatureCollection', features: [] },
  coverDisplayMetadata: { count: 0 },
} as unknown as TerrainRecord;
const rasterPackage = { key: 'terrain' } as unknown as TerrainRecord;

function writers() {
  return {
    saveTerrain: vi.fn(async () => ({ ok: true as const, key: 'terrain' })),
    saveTerrainCover: vi.fn(async () => ({ ok: true as const, key: 'terrain' })),
  };
}

describe('terrain dirty tracking', () => {
  it('accumulates edit kinds and reports pending work', () => {
    expect(terrainHasEdits(TERRAIN_CLEAN)).toBe(false);
    const cover = withTerrainEdit(TERRAIN_CLEAN, 'cover');
    expect(cover).toEqual({ elevation: false, cover: true });
    expect(terrainHasEdits(cover)).toBe(true);
    expect(withTerrainEdit(cover, 'elevation')).toEqual({ elevation: true, cover: true });
    // Re-flagging an already-dirty kind must not churn the reference.
    expect(withTerrainEdit(cover, 'cover')).toBe(cover);
  });
});

describe('flushTerrainEdits', () => {
  it('writes nothing when there are no pending edits', async () => {
    const io = writers();
    await expect(flushTerrainEdits(vectorPackage, TERRAIN_CLEAN, io))
      .resolves.toEqual({ ok: true, key: 'terrain' });
    expect(io.saveTerrain).not.toHaveBeenCalled();
    expect(io.saveTerrainCover).not.toHaveBeenCalled();
  });

  it('takes the partial cover write for a cover-only edit on a vector package', async () => {
    const io = writers();
    await flushTerrainEdits(vectorPackage, { elevation: false, cover: true }, io);
    expect(io.saveTerrainCover).toHaveBeenCalledWith(vectorPackage);
    expect(io.saveTerrain).not.toHaveBeenCalled();
  });

  it('takes the full write whenever elevation moved', async () => {
    const io = writers();
    await flushTerrainEdits(vectorPackage, { elevation: true, cover: true }, io);
    expect(io.saveTerrain).toHaveBeenCalledWith(vectorPackage);
    expect(io.saveTerrainCover).not.toHaveBeenCalled();
  });

  it('takes the full write for a raster-only package, whose cover lives in the record', async () => {
    const io = writers();
    await flushTerrainEdits(rasterPackage, { elevation: false, cover: true }, io);
    expect(io.saveTerrain).toHaveBeenCalledWith(rasterPackage);
    expect(io.saveTerrainCover).not.toHaveBeenCalled();
  });

  it('reports a storage failure rather than swallowing it', async () => {
    const io = writers();
    io.saveTerrain.mockResolvedValue({ ok: false, error: 'disk full' } as never);
    await expect(flushTerrainEdits(vectorPackage, { elevation: true, cover: false }, io))
      .resolves.toEqual({ ok: false, error: 'disk full' });
  });
});

describe('designHasEdits', () => {
  const saved = designOf(save);

  it('is clean while every collection reference is the saved one', () => {
    expect(designHasEdits(saved, designOf(save))).toBe(false);
  });

  it('is dirty once a collection is replaced', () => {
    const live = { ...saved, dams: [{ id: 'dam' }] as GameSave['dams'] };
    expect(designHasEdits(saved, live)).toBe(true);
  });

  it('is dirty after a live rename', () => {
    expect(designHasEdits(saved, { ...saved, name: 'Renamed' })).toBe(true);
  });

  it('stays dirty for an equal-but-rebuilt array, so a save is never skipped', () => {
    expect(designHasEdits(saved, { ...saved, lifts: [] })).toBe(true);
  });

  it('stays clean across a camera-only exit checkpoint', () => {
    const checkpoint = withResumeCheckpoint(
      save, { center: [1, 2], zoom: 10, bearing: 11, pitch: 12 }, false
    );
    expect(designHasEdits(saved, designOf(checkpoint))).toBe(false);
  });
});
