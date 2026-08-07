import type { SavedPond } from '../types/snowmaking';
import { nextPondName } from '../pondAnalysis';
import type { PondEarthworkDesign } from '../pondEarthwork';

export interface DraftPond extends Omit<SavedPond, 'id' | 'createdAt'> {}

export type PondTool =
  | { phase: 'idle' }
  | { phase: 'armed'; error: string | null }
  | { phase: 'drawing'; points: [number, number][]; cursor: [number, number] | null;
      error: string | null }
  | { phase: 'review'; draft: DraftPond; error: string | null };

export type PondControllerAction =
  | { type: 'arm' }
  | { type: 'point-failed'; error: string }
  | { type: 'add-point'; point: [number, number] }
  | { type: 'move'; point: [number, number] }
  | { type: 'undo' }
  | { type: 'review'; draft: DraftPond }
  | { type: 'patch'; patch: Partial<DraftPond> }
  | { type: 'design-failed'; topElevationM: number; excavationDepthM: number; error: string }
  | { type: 'cancel' };

export const IDLE_POND_TOOL: PondTool = Object.freeze({ phase: 'idle' });

export function reducePondTool(state: PondTool, action: PondControllerAction): PondTool {
  switch (action.type) {
    case 'arm': return { phase: 'armed', error: null };
    case 'point-failed':
      if (state.phase === 'armed') return { ...state, error: action.error };
      return state.phase === 'drawing' ? { ...state, error: action.error } : state;
    case 'add-point':
      if (state.phase === 'armed') return {
        phase: 'drawing', points: [action.point], cursor: null, error: null,
      };
      return state.phase === 'drawing'
        ? { ...state, points: [...state.points, action.point], cursor: null, error: null }
        : state;
    case 'move': return state.phase === 'drawing'
      ? { ...state, cursor: action.point } : state;
    case 'undo':
      if (state.phase !== 'drawing') return state;
      return state.points.length <= 1
        ? { phase: 'armed', error: null }
        : { ...state, points: state.points.slice(0, -1), cursor: null, error: null };
    case 'review': return state.phase === 'drawing'
      ? { phase: 'review', draft: action.draft, error: null } : state;
    case 'patch': return state.phase === 'review'
      ? { ...state, draft: { ...state.draft, ...action.patch } } : state;
    case 'design-failed': return state.phase === 'review' ? {
      phase: 'review', error: action.error, draft: { ...state.draft,
        topElevationM: action.topElevationM, excavationDepthM: action.excavationDepthM },
    } : state;
    case 'cancel': return IDLE_POND_TOOL;
  }
}

export function pondFromDraft(
  draft: DraftPond,
  design: PondEarthworkDesign,
  existing: readonly SavedPond[],
  id: string,
  createdAt: string,
): SavedPond {
  return {
    ...draft,
    id,
    name: draft.name.trim() || nextPondName([...existing]),
    crestElevationM: design.crestElevationM,
    excavationDepthM: design.excavationDepthM,
    maxBermHeightM: design.maxBermHeightM,
    bermLengthM: design.bermLengthM,
    maxCutDepthM: design.maxCutDepthM,
    disturbedAreaM2: design.disturbedAreaM2,
    terrainGraded: true,
    earthwork: { cutM3: design.cutM3, fillM3: design.fillM3, balanceM3: design.balanceM3 },
    createdAt,
  };
}
