import {
  addBuildingLayers,
  BUILDING_BUILT_LAYER_IDS,
  BUILDING_HIT_LAYERS,
  buildingDraftGeoJSON,
  buildingGeoJSON,
  clearBuildingLayers,
  setBuildingCaptureTransient,
  setBuildingData,
  setBuildingDraftData,
  setSelectedBuilding,
  type BuildingDraftMapData,
  type BuildingRenderRecord,
} from './buildingLayers';
import {
  MAP_HIT_RANK,
  MAP_Z_ORDER,
  type ManagedMapContribution,
  type MapPresentationMode,
  type MapVisibilityDescriptor,
} from './mapContribution';

export interface BuildingContributionOptions {
  readonly getBuildings: () => readonly BuildingRenderRecord[];
  readonly getSelectedId?: () => string | null;
  readonly getDraft?: () => BuildingDraftMapData | null;
  readonly structuresVisible?: () => boolean;
  readonly setSelected?: (id: string) => void;
  readonly synchronizeMap?: () => void;
}

function setBuiltLayerVisibility(
  map: maplibregl.Map,
  descriptorVisible: boolean,
  presentationMode: MapPresentationMode,
): void {
  const visible = descriptorVisible && presentationMode === null;
  for (const id of BUILDING_BUILT_LAYER_IDS) {
    if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', visible ? 'visible' : 'none');
  }
}

/** Managed-map adapter for native player-building extrusion and support data. */
export function createBuildingContribution(options: BuildingContributionOptions): ManagedMapContribution {
  let captureActive = false;
  let descriptorVisible = true;
  let presentationMode: MapPresentationMode = null;
  return {
    id: 'building',
    zOrder: MAP_Z_ORDER.building,
    hits: [{
      id: 'building',
      priority: MAP_HIT_RANK.building,
      layerIds: [...BUILDING_HIT_LAYERS],
      select: (id) => options.setSelected?.(id),
    }],
    install: ({ map }) => {
      descriptorVisible = options.structuresVisible?.() !== false;
      addBuildingLayers(map);
      setBuiltLayerVisibility(map, descriptorVisible, presentationMode);
    },
    synchronizeData: ({ map }) => {
      const buildings = options.getBuildings();
      const selected = options.getSelectedId?.() ?? null;
      const draft = options.getDraft?.() ?? null;
      setBuildingData(map, buildings, selected);
      setBuildingDraftData(map, draft);
      setSelectedBuilding(map, selected);
    },
    visibility: (): MapVisibilityDescriptor[] => options.structuresVisible?.() !== false ? [{
      id: 'buildings',
      label: 'Player buildings',
      layerIds: [...BUILDING_BUILT_LAYER_IDS],
      visible: true,
      section: 'Structures',
    }] : [],
    visibilityChanged: ({ map }, descriptorId, visible) => {
      if (descriptorId !== 'buildings') return;
      descriptorVisible = visible;
      setBuiltLayerVisibility(map, descriptorVisible, presentationMode);
    },
    presentationChanged: ({ map }, mode) => {
      presentationMode = mode;
      setBuiltLayerVisibility(map, descriptorVisible, presentationMode);
    },
    setCaptureTransient: ({ map }, hidden) => {
      const draft = options.getDraft?.() ?? null;
      if (hidden && !captureActive) captureActive = true;
      setBuildingCaptureTransient(map, hidden, draft);
      if (!hidden) captureActive = false;
    },
    cleanup: ({ map }) => clearBuildingLayers(map),
  };
}

export function buildingRenderSnapshot(
  buildings: readonly BuildingRenderRecord[],
  selectedId: string | null = null,
): { meshCount: number; footprint: GeoJSON.FeatureCollection; draft: GeoJSON.FeatureCollection } {
  return {
    meshCount: buildings.length,
    footprint: buildingGeoJSON(buildings, selectedId),
    draft: buildingDraftGeoJSON(null),
  };
}
