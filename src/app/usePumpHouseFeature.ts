import { useRef, useState, type MutableRefObject, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import { BuildingDocument, buildingProjection } from '../buildingDocument';
import type { SavedBuilding } from '../types/buildings';
import { createOwnedSnowmakingPump, removeBuildingOwnedPump,
  renameOwnedSnowmakingPump } from '../snowmakingOwnedPumps';
import { commitBuildingDocuments } from './buildingCommitCoordinator';
import { applyTerrainGradeToRecord } from './terrainGradeCommit';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import type { SnowmakingNetworkDocument } from './snowmakingNetworkDocument';
import type { TerrainDocument } from './terrainDocument';
import { useBuildingController } from './useBuildingController';

export interface PumpHouseFeatureOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  initialBuildings: SavedBuilding[];
  committedRef?: MutableRefObject<SavedBuilding[]>;
  terrain: TerrainDocument;
  snowmaking: SnowmakingNetworkDocument;
  canArm(): boolean;
  activate(): boolean;
  release(): void;
  openDock(): void;
  clearSelection(): void;
  selectBuilding(id: string): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  clearCover(polygons: [number, number][][][]): Promise<void>;
  createId(): string;
  now(): string;
  structuresVisible(): boolean;
  synchronizeMap(): void;
}

/** Owns the building document and its reciprocal snowmaking transaction port. */
export function usePumpHouseFeature(options: PumpHouseFeatureOptions) {
  const [buildings, setBuildings] = useState<SavedBuilding[]>(options.initialBuildings);
  const [selectedBuildingId, setSelectedBuildingId] = useState<string | null>(null);
  const localCommittedRef = useRef(buildings);
  const committedRef = options.committedRef ?? localCommittedRef;
  const documentRef = useRef<BuildingDocument | null>(null);
  if (!documentRef.current) {
    documentRef.current = new BuildingDocument(buildings, ({ snapshot }) => {
      const projection = buildingProjection(snapshot);
      committedRef.current = projection.buildings;
      setBuildings(projection.buildings);
    });
  }
  const document = documentRef.current;

  const controller = useBuildingController({
    mapRef: options.mapRef, buildings, selectedBuildingId,
    selectBuilding: options.selectBuilding,
    clearSelected: (id) => setSelectedBuildingId((selected) => selected === id ? null : selected),
    canArm: options.canArm, activate: options.activate, release: options.release,
    openDock: options.openDock, clearSelection: options.clearSelection,
    acquireInteractions: options.acquireInteractions, terrain: options.terrain,
    buildingRevision: () => document.revision,
    snowmakingRevision: () => options.snowmaking.revision,
    commitBuilding: ({ building, analysis, terrainRevision,
      buildingRevision, snowmakingRevision }) => {
      if (document.revision !== buildingRevision)
        throw new Error('The building plan changed after analysis. Redraw the pump house.');
      if (options.snowmaking.revision !== snowmakingRevision)
        throw new Error('The snowmaking network changed after analysis. Redraw the pump house.');
      const buildingTx = document.begin();
      if (!buildingTx.addBuilding(building)) throw new Error('That building already exists.');
      const networkTx = options.snowmaking.begin();
      const pump = createOwnedSnowmakingPump(networkTx.snapshot(), {
        id: building.connection.nodeId, ownerBuildingId: building.id, name: building.name,
        point: [...building.center], elevM: building.foundation.finishedFloorElevationM,
        createdAt: building.createdAt,
      });
      if (!pump) throw new Error('The pump house could not reserve its snowmaking pump.');
      networkTx.replace(pump.state);
      const terrainSnapshot = options.terrain.snapshot();
      if (terrainSnapshot.revision !== terrainRevision)
        throw new Error('The terrain changed after analysis. Redraw the pump house.');
      const terrainCommit = analysis.terrainGraded && terrainSnapshot.record
        ? { expectedRevision: terrainRevision,
          record: applyTerrainGradeToRecord(terrainSnapshot.record, analysis.terrainPatch),
          kind: 'elevation' as const }
        : undefined;
      const result = commitBuildingDocuments({ terrain: options.terrain, building: buildingTx,
        snowmaking: networkTx, terrainCommit });
      if (!result.ok) throw new Error('The resort changed while building. Review the site and try again.');
    },
    commands: {
      rename: (id, name) => {
        const buildingTx = document.begin();
        if (!buildingTx.renameBuilding(id, name)) return false;
        const networkTx = options.snowmaking.begin();
        const next = renameOwnedSnowmakingPump(networkTx.snapshot(), id, name);
        if (!next) { buildingTx.abort(); networkTx.abort(); return false; }
        networkTx.replace(next);
        return commitBuildingDocuments({ terrain: options.terrain, building: buildingTx,
          snowmaking: networkTx }).ok;
      },
      remove: (id) => {
        const buildingTx = document.begin();
        if (!buildingTx.removeBuilding(id)) return false;
        const networkTx = options.snowmaking.begin();
        const removal = removeBuildingOwnedPump(networkTx.snapshot(), id, options.createId);
        if (!removal) { buildingTx.abort(); networkTx.abort(); return false; }
        networkTx.replace(removal.state);
        return commitBuildingDocuments({ terrain: options.terrain, building: buildingTx,
          snowmaking: networkTx }).ok;
      },
    },
    clearCover: options.clearCover, createId: options.createId, createNodeId: options.createId,
    now: options.now, structuresVisible: options.structuresVisible,
    synchronizeMap: options.synchronizeMap,
  });

  return { buildings, selectedBuildingId, setSelectedBuildingId, committedRef, controller };
}
