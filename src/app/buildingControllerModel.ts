import { buildingSiteGeometryKey } from './buildingSiteProtocol';
import type { BuildingSiteAnalysisResult } from '../buildingSiteAnalysis';
import {
  defaultBuildingDraft,
  createSavedBuilding,
} from '../buildings';
import { normalizeBearingDeg } from '../buildingUnits';
import type { BuildingDraftParameters, BuildingFoundation, SavedBuilding } from '../types/buildings';
import type { EarthworkEstimate } from '../types/earthwork';

export type BuildingPoint = [number, number];
export type BuildingToolPhase = 'idle' | 'armed' | 'centered' | 'review';
export type BuildingSiteStatus = 'pending' | 'ok' | 'error';

/** The state shared by the review panel and the map draft renderer. */
export interface BuildingReviewDraft extends BuildingDraftParameters {
  siteStatus: BuildingSiteStatus;
  siteError: string | null;
  siteAnalysis: BuildingSiteAnalysisResult | null;
  siteIdentity: BuildingSiteIdentity | null;
  /** True when this draft positively overlaps another player building. */
  hasCollision: boolean;
  /** Error from the last confirmation attempt. Review is deliberately retained. */
  confirmationError: string | null;
}

export interface BuildingSiteIdentity {
  geometryKey: string;
  terrainRevision: string | number;
  elevationChecksum: string;
}

export interface ArmedBuildingTool {
  phase: 'armed';
  name: string;
  buildingTypeId: BuildingDraftParameters['buildingTypeId'];
  dimensions: BuildingDraftParameters['dimensions'];
  foundationMode: BuildingDraftParameters['foundationMode'];
  /** The current pointer, if a pointer event has reached the map. */
  cursor: BuildingPoint | null;
  /** Bearing is retained while hovering before the center click. */
  bearingDeg: number;
}

export interface CenteredBuildingTool {
  phase: 'centered';
  name: string;
  buildingTypeId: BuildingDraftParameters['buildingTypeId'];
  dimensions: BuildingDraftParameters['dimensions'];
  foundationMode: BuildingDraftParameters['foundationMode'];
  center: BuildingPoint;
  cursor: BuildingPoint | null;
  bearingDeg: number;
}

export type BuildingTool =
  | { phase: 'idle' }
  | ArmedBuildingTool
  | CenteredBuildingTool
  | { phase: 'review'; draft: BuildingReviewDraft };

export const IDLE_BUILDING_TOOL: BuildingTool = Object.freeze({ phase: 'idle' });

export type BuildingControllerAction =
  | { type: 'arm'; draft?: Partial<BuildingDraftParameters> }
  | { type: 'open'; draft?: Partial<BuildingDraftParameters> }
  | { type: 'move'; point: BuildingPoint }
  | { type: 'center'; point: BuildingPoint }
  | { type: 'anchor'; point: BuildingPoint }
  | { type: 'lock'; point?: BuildingPoint; bearingDeg?: number }
  | { type: 'review'; point?: BuildingPoint; bearingDeg?: number }
  | { type: 'patch'; patch: Partial<BuildingReviewDraft>; invalidateSite?: boolean }
  | { type: 'site-started'; identity?: BuildingSiteIdentity | null }
  | { type: 'site-succeeded'; result: BuildingSiteAnalysisResult; identity?: BuildingSiteIdentity; hasCollision?: boolean }
  | { type: 'site-failed'; error: string }
  | { type: 'confirmation-failed'; error: string }
  | { type: 'cancel' };

const SITE_GEOMETRY_FIELDS = new Set<keyof BuildingReviewDraft>([
  'center', 'bearingDeg', 'dimensions', 'foundationMode',
]);

function pointCopy(point: readonly [number, number]): BuildingPoint {
  return [point[0], point[1]];
}

function dimensionsCopy(dimensions: BuildingDraftParameters['dimensions']) {
  return {
    lengthM: dimensions.lengthM,
    widthM: dimensions.widthM,
    eaveHeightM: dimensions.eaveHeightM,
  };
}

function initialValues(draft?: Partial<BuildingDraftParameters>): BuildingDraftParameters {
  const base = defaultBuildingDraft();
  return {
    ...base,
    ...draft,
    center: pointCopy(draft?.center ?? base.center),
    dimensions: dimensionsCopy({ ...base.dimensions, ...(draft?.dimensions ?? {}) }),
    bearingDeg: normalizeBearingDeg(draft?.bearingDeg ?? base.bearingDeg),
  };
}

function siteIdentityFor(draft: Pick<BuildingDraftParameters, 'center' | 'bearingDeg' | 'dimensions' | 'foundationMode'>,
  terrainRevision: string | number = 0, elevationChecksum = ''): BuildingSiteIdentity {
  return {
    geometryKey: buildingSiteGeometryKey(draft.center, draft.bearingDeg, draft.dimensions, draft.foundationMode),
    terrainRevision,
    elevationChecksum,
  };
}

function reviewDraftFromCentered(state: CenteredBuildingTool, bearingDeg = state.bearingDeg): BuildingReviewDraft {
  const values: BuildingDraftParameters = {
    buildingTypeId: state.buildingTypeId,
    name: state.name,
    center: pointCopy(state.center),
    bearingDeg: normalizeBearingDeg(bearingDeg),
    dimensions: dimensionsCopy(state.dimensions),
    foundationMode: state.foundationMode,
  };
  return {
    ...values,
    siteStatus: 'pending',
    siteError: null,
    siteAnalysis: null,
    siteIdentity: null,
    hasCollision: false,
    confirmationError: null,
  };
}

function bearingFromPoint(center: BuildingPoint, point: BuildingPoint): number {
  const latitude1 = center[1] * Math.PI / 180;
  const latitude2 = point[1] * Math.PI / 180;
  const deltaLongitude = (point[0] - center[0]) * Math.PI / 180;
  const y = Math.sin(deltaLongitude) * Math.cos(latitude2);
  const x = Math.cos(latitude1) * Math.sin(latitude2) -
    Math.sin(latitude1) * Math.cos(latitude2) * Math.cos(deltaLongitude);
  return normalizeBearingDeg(Math.atan2(y, x) * 180 / Math.PI);
}

/** Clockwise-from-north heading used by the placement preview. */
export function buildingBearingBetween(center: BuildingPoint, point: BuildingPoint): number {
  return bearingFromPoint(center, point);
}
export const bearingBetweenBuildingPoints = buildingBearingBetween;

function patchNeedsSiteRefresh(patch: Partial<BuildingReviewDraft>): boolean {
  return [...SITE_GEOMETRY_FIELDS].some((field) => field in patch);
}

/** Pure placement/review workflow. Worker identity and map listeners live in the hook. */
export function reduceBuildingTool(state: BuildingTool, action: BuildingControllerAction): BuildingTool {
  switch (action.type) {
    case 'arm':
    case 'open': {
      if (state.phase !== 'idle') return state;
      const values = initialValues(action.draft);
      return {
        phase: 'armed', name: values.name, buildingTypeId: values.buildingTypeId,
        dimensions: values.dimensions, foundationMode: values.foundationMode,
        cursor: null, bearingDeg: values.bearingDeg,
      };
    }
    case 'move': {
      if (state.phase === 'armed') return { ...state, cursor: pointCopy(action.point) };
      if (state.phase === 'centered') {
        const distanceSquared = (action.point[0] - state.center[0]) ** 2 +
          (action.point[1] - state.center[1]) ** 2;
        return {
          ...state, cursor: pointCopy(action.point),
          // Do not allow a sub-millimetre geographic jitter to make the heading
          // jump while the pointer is still on top of the center marker.
          bearingDeg: distanceSquared > 1e-18
            ? bearingFromPoint(state.center, action.point) : state.bearingDeg,
        };
      }
      return state;
    }
    case 'center':
    case 'anchor':
      return state.phase === 'armed' ? {
        phase: 'centered', name: state.name, buildingTypeId: state.buildingTypeId,
        dimensions: dimensionsCopy(state.dimensions), foundationMode: state.foundationMode,
        center: pointCopy(action.point), cursor: null, bearingDeg: state.bearingDeg,
      } : state;
    case 'lock':
    case 'review': {
      if (state.phase !== 'centered') return state;
      const point = action.point ?? state.cursor;
      if (point) {
        const dLng = (point[0] - state.center[0]) * 111_320 * Math.cos(state.center[1] * Math.PI / 180);
        const dLat = (point[1] - state.center[1]) * 111_320;
        if (Math.hypot(dLng, dLat) < 1) return state;
      } else if (action.type === 'lock') {
        return state;
      }
      const bearing = action.bearingDeg ?? (point ? bearingFromPoint(state.center, point) : state.bearingDeg);
      return { phase: 'review', draft: reviewDraftFromCentered(state, bearing) };
    }
    case 'patch': {
      if (state.phase !== 'review') return state;
      const patch = action.patch;
      const next: BuildingReviewDraft = {
        ...state.draft,
        ...patch,
        ...(patch.center ? { center: pointCopy(patch.center) } : {}),
        ...(patch.dimensions ? { dimensions: dimensionsCopy({ ...state.draft.dimensions, ...patch.dimensions }) } : {}),
        ...(patch.bearingDeg !== undefined ? { bearingDeg: normalizeBearingDeg(patch.bearingDeg) } : {}),
      };
      const invalidates = action.invalidateSite ?? patchNeedsSiteRefresh(patch);
      return {
        phase: 'review', draft: invalidates ? {
          ...next, siteStatus: 'pending', siteError: null, siteAnalysis: null,
          siteIdentity: null, hasCollision: false, confirmationError: null,
        } : { ...next, confirmationError: null },
      };
    }
    case 'site-started':
      return state.phase === 'review' ? { phase: 'review', draft: {
        ...state.draft, siteStatus: 'pending', siteError: null,
        siteAnalysis: null, siteIdentity: action.identity ?? null,
        hasCollision: false, confirmationError: null,
      } } : state;
    case 'site-succeeded':
      return state.phase === 'review' ? { phase: 'review', draft: {
        ...state.draft, siteStatus: 'ok', siteError: null,
        siteAnalysis: action.result, siteIdentity: action.identity ?? null,
        hasCollision: action.hasCollision ?? false, confirmationError: null,
      } } : state;
    case 'site-failed':
      return state.phase === 'review' ? { phase: 'review', draft: {
        ...state.draft, siteStatus: 'error', siteError: action.error,
        siteAnalysis: null, siteIdentity: null, hasCollision: false,
      } } : state;
    case 'confirmation-failed':
      return state.phase === 'review' ? { phase: 'review', draft: {
        ...state.draft, confirmationError: action.error,
      } } : state;
    case 'cancel': return IDLE_BUILDING_TOOL;
  }
}

export function buildingDraftSiteIdentity(
  draft: Pick<BuildingDraftParameters, 'center' | 'bearingDeg' | 'dimensions' | 'foundationMode'>,
  terrainRevision: string | number = 0,
  elevationChecksum = '',
): BuildingSiteIdentity {
  return siteIdentityFor(draft, terrainRevision, elevationChecksum);
}

/** Convert an active tool to the data accepted by the building map family. */
export function buildingDraftMapData(tool: BuildingTool): {
  center: BuildingPoint;
  lengthM: number;
  widthM: number;
  bearingDeg: number;
  foundationMode: string;
  gradePolygons?: readonly (readonly BuildingPoint[])[];
} | null {
  if (tool.phase === 'armed') {
    if (!tool.cursor) return null;
    return { center: pointCopy(tool.cursor), lengthM: tool.dimensions.lengthM,
      widthM: tool.dimensions.widthM, bearingDeg: tool.bearingDeg,
      foundationMode: tool.foundationMode };
  }
  if (tool.phase === 'centered') {
    return { center: pointCopy(tool.center), lengthM: tool.dimensions.lengthM,
      widthM: tool.dimensions.widthM, bearingDeg: tool.bearingDeg,
      foundationMode: tool.foundationMode };
  }
  if (tool.phase === 'review') {
    const draft = tool.draft;
    return { center: pointCopy(draft.center), lengthM: draft.dimensions.lengthM,
      widthM: draft.dimensions.widthM, bearingDeg: draft.bearingDeg,
      foundationMode: draft.foundationMode,
      gradePolygons: draft.siteAnalysis?.disturbancePolygons
        .map((polygon) => polygon[0])
        .filter((ring): ring is [number, number][] => !!ring) };
  }
  return null;
}
export const buildingMapDraftOf = buildingDraftMapData;

export function nextBuildingName(existing: readonly Pick<SavedBuilding, 'name'>[]): string {
  const used = new Set(existing.map((building) => building.name.trim().toLowerCase()));
  let index = 1;
  while (used.has(`pump house ${index}`.toLowerCase())) index++;
  return `Pump House ${index}`;
}

export interface BuildingCommitDraft extends BuildingReviewDraft {
  siteAnalysis: BuildingSiteAnalysisResult;
}

function foundationFromAnalysis(analysis: BuildingSiteAnalysisResult): BuildingFoundation {
  const foundation = analysis.foundation;
  return foundation.kind === 'flattened' ? {
    kind: 'flattened', finishedFloorElevationM: foundation.finishedFloorElevationM,
    terrainGraded: true, earthwork: { ...foundation.earthwork },
  } : {
    kind: 'slope', finishedFloorElevationM: foundation.finishedFloorElevationM,
    terrainGraded: false,
    perimeterGroundElevationsM: [...foundation.perimeterGroundElevationsM],
  };
}

/** Build the persisted record after site analysis has succeeded. */
export function buildingFromDraft(
  draft: BuildingCommitDraft | BuildingReviewDraft,
  existing: readonly SavedBuilding[],
  id: string,
  createdAt: string,
): SavedBuilding;
export function buildingFromDraft(
  draft: BuildingCommitDraft | BuildingReviewDraft,
  existing: readonly SavedBuilding[],
  id: string,
  nodeId: string,
  createdAt: string,
): SavedBuilding;
export function buildingFromDraft(
  draft: BuildingCommitDraft | BuildingReviewDraft,
  existing: readonly SavedBuilding[],
  id: string,
  nodeIdOrCreatedAt: string,
  createdAtMaybe?: string,
): SavedBuilding {
  if (!draft.siteAnalysis) throw new Error('A successful site analysis is required before confirmation.');
  const analysis = draft.siteAnalysis;
  const foundation = foundationFromAnalysis(analysis);
  const createdAt = createdAtMaybe ?? nodeIdOrCreatedAt;
  const nodeId = createdAtMaybe ? nodeIdOrCreatedAt : `${id}:pump`;
  return createSavedBuilding({
    id,
    name: draft.name.trim() || nextBuildingName(existing),
    center: pointCopy(draft.center), bearingDeg: draft.bearingDeg,
    dimensions: dimensionsCopy(draft.dimensions), foundation, nodeId, createdAt,
  });
}
export const savedBuildingFromDraft = buildingFromDraft;
export const pumpHouseFromDraft = buildingFromDraft;

/** Confirmation eligibility stays pure so review UIs and controllers agree. */
export function canConfirmBuilding(draft: BuildingReviewDraft): boolean {
  return draft.siteStatus === 'ok' && draft.siteAnalysis !== null && !draft.hasCollision;
}

export function buildingReviewEarthwork(draft: BuildingReviewDraft): EarthworkEstimate | null {
  return draft.siteAnalysis ? { ...draft.siteAnalysis.earthwork } : null;
}
