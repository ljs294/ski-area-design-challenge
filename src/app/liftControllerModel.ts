import type { SavedLift } from '../types/lifts';
import { FIXED_GRIP_SPEC, liftStats, nextLiftName, orientBottomToTop } from '../lifts';

export type LiftTool =
  | { phase: 'idle' }
  | { phase: 'armed' }
  | { phase: 'anchored'; a: [number, number]; cursor: [number, number] | null }
  | { phase: 'review'; draft: DraftLift };

export interface DraftLift {
  points: [[number, number], [number, number]];
  elev: [number | null, number | null];
  elevStatus: 'pending' | 'ok' | 'error';
  chairSize: SavedLift['chairSize'];
  status: SavedLift['status'];
  name: string;
}

export type LiftControllerAction =
  | { type: 'arm' }
  | { type: 'anchor'; point: [number, number] }
  | { type: 'move'; point: [number, number] }
  | { type: 'review'; points: [[number, number], [number, number]]; name: string }
  | { type: 'patch'; patch: Partial<DraftLift> }
  | { type: 'sample-started' }
  | { type: 'sample-succeeded'; elevations: [number, number] }
  | { type: 'sample-failed' }
  | { type: 'cancel' };

export const IDLE_LIFT_TOOL: LiftTool = Object.freeze({ phase: 'idle' });

/** Pure lift workflow; map listeners and asynchronous identity live in the hook. */
export function reduceLiftTool(state: LiftTool, action: LiftControllerAction): LiftTool {
  switch (action.type) {
    case 'arm': return { phase: 'armed' };
    case 'anchor': return state.phase === 'armed'
      ? { phase: 'anchored', a: action.point, cursor: null }
      : state;
    case 'move': return state.phase === 'anchored' ? { ...state, cursor: action.point } : state;
    case 'review': return state.phase === 'anchored' ? { phase: 'review', draft: {
      points: action.points,
      elev: [null, null],
      elevStatus: 'pending',
      chairSize: FIXED_GRIP_SPEC.defaultChairSize,
      status: 'planning',
      name: action.name,
    } } : state;
    case 'patch': return state.phase === 'review'
      ? { phase: 'review', draft: { ...state.draft, ...action.patch } }
      : state;
    case 'sample-started': return state.phase === 'review'
      ? { phase: 'review', draft: { ...state.draft, elevStatus: 'pending' } }
      : state;
    case 'sample-succeeded': return state.phase === 'review' ? { phase: 'review', draft: {
      ...state.draft,
      elev: action.elevations,
      elevStatus: 'ok',
    } } : state;
    case 'sample-failed': return state.phase === 'review'
      ? { phase: 'review', draft: { ...state.draft, elevStatus: 'error' } }
      : state;
    case 'cancel': return IDLE_LIFT_TOOL;
  }
}

/** Construct the committed entity from a reviewed draft without UI ownership. */
export function liftFromDraft(
  draft: DraftLift,
  existing: readonly SavedLift[],
  id: string,
  createdAt: string,
): SavedLift {
  const oriented = orientBottomToTop(draft.points, draft.elev);
  const stats = liftStats(oriented.points, oriented.elevs);
  return {
    id,
    name: draft.name.trim() || nextLiftName([...existing]),
    liftClass: 'fixed-grip',
    points: oriented.points,
    endpointElevM: oriented.elevs,
    lengthM: stats.lengthM,
    verticalM: stats.verticalM,
    chairSize: draft.chairSize,
    status: draft.status,
    createdAt,
  };
}
