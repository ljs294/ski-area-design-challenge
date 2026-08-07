import type { EarthworkEstimate, RoadType, SavedRoad } from '../types';
import { nextRoadName, roadLengthM, TWO_LANE_ROAD_WIDTH_M } from '../roads';

export interface DraftRoad {
  name: string;
  roadType: RoadType;
  points: [number, number][];
  gradingStatus: 'pending' | 'ok' | 'error';
  gradingError: string | null;
  gradingPolygons: [number, number][][][];
  earthwork: EarthworkEstimate | null;
  maxFaceSlopePct: number;
  maxGroundCrossSlopePct: number;
  maxDisturbedWidthM: number;
  ungradedLengthM: number;
  gradingInfeasibleLines: [number, number][][];
}

export type RoadTool =
  | { phase: 'idle' }
  | { phase: 'armed'; roadType: RoadType }
  | { phase: 'drawing'; roadType: RoadType; points: [number, number][];
      cursor: [number, number] | null }
  | { phase: 'review'; draft: DraftRoad };

export type RoadControllerAction =
  | { type: 'arm'; roadType: RoadType }
  | { type: 'add-point'; point: [number, number] }
  | { type: 'move'; point: [number, number] }
  | { type: 'undo' }
  | { type: 'review'; name: string }
  | { type: 'patch'; patch: Partial<DraftRoad> }
  | { type: 'grade-failed'; error: string }
  | { type: 'cancel' };

export const IDLE_ROAD_TOOL: RoadTool = Object.freeze({ phase: 'idle' });

export function reduceRoadTool(state: RoadTool, action: RoadControllerAction): RoadTool {
  switch (action.type) {
    case 'arm': return { phase: 'armed', roadType: action.roadType };
    case 'add-point':
      if (state.phase === 'armed') return {
        phase: 'drawing', roadType: state.roadType, points: [action.point], cursor: null,
      };
      return state.phase === 'drawing'
        ? { ...state, points: [...state.points, action.point], cursor: null }
        : state;
    case 'move': return state.phase === 'drawing' ? { ...state, cursor: action.point } : state;
    case 'undo':
      if (state.phase !== 'drawing') return state;
      return state.points.length <= 1
        ? { phase: 'armed', roadType: state.roadType }
        : { ...state, points: state.points.slice(0, -1), cursor: null };
    case 'review': return state.phase === 'drawing' && state.points.length >= 2
      ? { phase: 'review', draft: {
        name: action.name,
        roadType: state.roadType,
        points: state.points,
        gradingStatus: 'pending',
        gradingError: null,
        gradingPolygons: [],
        earthwork: null,
        maxFaceSlopePct: 0,
        maxGroundCrossSlopePct: 0,
        maxDisturbedWidthM: 0,
        ungradedLengthM: 0,
        gradingInfeasibleLines: [],
      } }
      : state;
    case 'patch': return state.phase === 'review'
      ? { phase: 'review', draft: { ...state.draft, ...action.patch } }
      : state;
    case 'grade-failed': return state.phase === 'review' ? { phase: 'review', draft: {
      ...state.draft, gradingStatus: 'error', gradingError: action.error,
    } } : state;
    case 'cancel': return IDLE_ROAD_TOOL;
  }
}

export function roadFromDraft(
  draft: DraftRoad,
  existing: readonly SavedRoad[],
  id: string,
  createdAt: string,
): SavedRoad {
  return {
    id,
    name: draft.name.trim() || nextRoadName([...existing]),
    roadType: 'two-lane',
    widthM: TWO_LANE_ROAD_WIDTH_M,
    points: draft.points,
    lengthM: roadLengthM(draft.points),
    terrainGraded: true,
    earthwork: draft.earthwork ?? undefined,
    createdAt,
  };
}
