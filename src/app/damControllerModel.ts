import type { SavedDam } from '../types/snowmaking';
import { nextDamName } from '../damAnalysis';

export interface DraftDam extends Omit<SavedDam, 'id' | 'createdAt'> {}

export type DamTool =
  | { phase: 'idle' }
  | { phase: 'armed'; error: string | null }
  | { phase: 'anchored'; first: [number, number]; crestElevationM: number;
      cursor: [number, number] | null; error: string | null }
  | { phase: 'analyzing'; points: [[number, number], [number, number]]; crestElevationM: number }
  | { phase: 'review'; draft: DraftDam; error: string | null };

export type DamControllerAction =
  | { type: 'arm' }
  | { type: 'arm-failed'; error: string }
  | { type: 'anchor'; point: [number, number]; crestElevationM: number }
  | { type: 'move'; point: [number, number] | null; error: string | null }
  | { type: 'analyze'; points: [[number, number], [number, number]] }
  | { type: 'review'; draft: DraftDam }
  | { type: 'analysis-failed'; points: [[number, number], [number, number]];
      crestElevationM: number; error: string }
  | { type: 'patch'; patch: Partial<DraftDam> }
  | { type: 'build-failed'; error: string }
  | { type: 'cancel' };

export const IDLE_DAM_TOOL: DamTool = Object.freeze({ phase: 'idle' });

export function reduceDamTool(state: DamTool, action: DamControllerAction): DamTool {
  switch (action.type) {
    case 'arm': return { phase: 'armed', error: null };
    case 'arm-failed': return state.phase === 'armed'
      ? { phase: 'armed', error: action.error } : state;
    case 'anchor': return state.phase === 'armed' ? {
      phase: 'anchored', first: action.point, crestElevationM: action.crestElevationM,
      cursor: null, error: null,
    } : state;
    case 'move': return state.phase === 'anchored'
      ? { ...state, cursor: action.point, error: action.error } : state;
    case 'analyze': return state.phase === 'anchored' ? {
      phase: 'analyzing', points: action.points, crestElevationM: state.crestElevationM,
    } : state;
    case 'review': return state.phase === 'analyzing'
      ? { phase: 'review', draft: action.draft, error: null } : state;
    case 'analysis-failed': return state.phase === 'analyzing' ? {
      phase: 'anchored', first: action.points[0], crestElevationM: action.crestElevationM,
      cursor: null, error: action.error,
    } : state;
    case 'patch': return state.phase === 'review'
      ? { ...state, draft: { ...state.draft, ...action.patch } } : state;
    case 'build-failed': return state.phase === 'review'
      ? { ...state, error: action.error } : state;
    case 'cancel': return IDLE_DAM_TOOL;
  }
}

export function damFromDraft(
  draft: DraftDam,
  existing: readonly SavedDam[],
  id: string,
  createdAt: string,
): SavedDam {
  return {
    ...draft,
    id,
    name: draft.name.trim() || nextDamName([...existing]),
    terrainGraded: true,
    createdAt,
  };
}
