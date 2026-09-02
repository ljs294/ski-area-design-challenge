import { describe, expect, it } from 'vitest';
import { BuildingDocument, buildingProjection } from './buildingDocument';
import { createSavedBuilding } from './buildings';
import type { SavedBuilding } from './types/buildings';

function building(id: string): SavedBuilding {
  return createSavedBuilding({
    id, name: id, center: [0, 0], nodeId: `${id}-node`,
    foundation: { kind: 'slope', finishedFloorElevationM: 10, terrainGraded: false,
      perimeterGroundElevationsM: [1, 2, 3, 4, 5, 6, 7, 8] },
    createdAt: '2026-01-01T00:00:00.000Z',
  });
}

describe('BuildingDocument', () => {
  it('publishes atomic revisioned additions and protects its snapshot', () => {
    const changes: number[] = [];
    const document = new BuildingDocument({ buildings: [] }, () => changes.push(document.revision));
    const transaction = document.begin();
    expect(transaction.addBuilding(building('one'))).toBe(true);
    const prepared = transaction.prepareCommit();
    expect(prepared.ok).toBe(true);
    if (!prepared.ok) return;
    expect(document.revision).toBe(0);
    expect(document.applyPrepared(prepared.prepared)).toBe(true);
    expect(document.revision).toBe(1);
    expect(changes).toEqual([]);
    document.publishPrepared(prepared.prepared);
    expect(changes).toEqual([1]);
    expect(document.snapshot().buildings).toHaveLength(1);
    expect(buildingProjection(document.snapshot())).toEqual({ buildings: [building('one')] });
  });

  it('rejects stale transactions and supports rename/remove through one revision', () => {
    const document = new BuildingDocument([building('one'), building('two')]);
    const first = document.begin();
    const second = document.begin();
    expect(first.renameBuilding('one', 'Renamed')).toBe(true);
    expect(first.commit()).toMatchObject({ ok: true, revision: 1 });
    expect(second.removeBuilding('two')).toBe(true);
    expect(second.commit()).toEqual({ ok: false, reason: 'stale' });
    const remove = document.begin();
    expect(remove.removeBuilding('two')).toBe(true);
    expect(remove.commit()).toMatchObject({ ok: true, revision: 2 });
    expect(document.snapshot().buildings.map((entry) => entry.name)).toEqual(['Renamed']);
  });
});
