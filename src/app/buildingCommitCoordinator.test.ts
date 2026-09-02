import { describe, expect, it } from 'vitest';
import { BuildingDocument } from '../buildingDocument';
import type { SavedBuilding } from '../types/buildings';
import type { TerrainRecord } from '../types/terrain';
import { SnowmakingNetworkDocument } from './snowmakingNetworkDocument';
import { TerrainDocument, type TerrainDocumentPorts } from './terrainDocument';
import { commitBuildingDocuments } from './buildingCommitCoordinator';

const ports: TerrainDocumentPorts = {
  cacheDisplayAssets: () => {}, activateProtocols: () => {}, publishState: () => {},
  refreshSources: () => {}, publishPersisted: () => {}, publishConstruction: () => {},
};

const terrainRecord = { key: 'terrain' } as TerrainRecord;
const building = { id: 'building-1' } as SavedBuilding;

function documents() {
  const terrain = new TerrainDocument(ports);
  terrain.replace(terrainRecord);
  return {
    terrain,
    buildings: new BuildingDocument([]),
    snowmaking: new SnowmakingNetworkDocument({ nodes: [], pipes: [], guns: [],
      nextNumbers: { pump: 1, junction: 1, hydrant: 1 } }),
  };
}

describe('commitBuildingDocuments', () => {
  it('applies every authoritative snapshot before publishing observers', () => {
    const docs = documents();
    const buildingTx = docs.buildings.begin();
    buildingTx.addBuilding(building);
    const networkTx = docs.snowmaking.begin();
    networkTx.replace({ ...networkTx.snapshot(), nodes: [{
      id: 'pump-1', name: 'Pump House 1', kind: 'pump', point: [0, 0], elevM: 1,
      ownerBuildingId: 'building-1', pumpRating: { horsepowerHp: 1000, efficiency: 0.85 },
      createdAt: '2026-01-01T00:00:00.000Z',
    }] });

    expect(commitBuildingDocuments({ terrain: docs.terrain, building: buildingTx,
      snowmaking: networkTx })).toMatchObject({ ok: true, buildingRevision: 1,
      snowmakingRevision: 1 });
    expect(docs.buildings.snapshot().buildings).toHaveLength(1);
    expect(docs.snowmaking.snapshot().nodes).toHaveLength(1);
  });

  it('rejects a stale participant without moving either peer document', () => {
    const docs = documents();
    const stale = docs.buildings.begin();
    stale.addBuilding(building);
    const intervening = docs.buildings.begin();
    intervening.addBuilding({ ...building, id: 'other' });
    expect(intervening.commit().ok).toBe(true);
    const networkTx = docs.snowmaking.begin();

    expect(commitBuildingDocuments({ terrain: docs.terrain, building: stale,
      snowmaking: networkTx })).toEqual({ ok: false, reason: 'building-stale' });
    expect(docs.buildings.snapshot().buildings.map((entry) => entry.id)).toEqual(['other']);
    expect(docs.snowmaking.revision).toBe(0);
  });
});
