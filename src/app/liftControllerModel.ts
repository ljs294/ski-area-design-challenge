import type { LiftTypeId, SavedLift } from '../types/lifts';
import {
  DEFAULT_LIFT_TYPE_ID,
  liftStats,
  nextLiftIdentifier,
  nextLiftName,
  orientBottomToTop,
} from '../lifts';

export type LiveElevationStatus = 'idle' | 'pending' | 'ok' | 'error';
export type CommittedLiftPatch = Partial<Omit<SavedLift, 'liftTypeId'>>;

export type LiftTool =
  | { phase: 'idle' }
  | { phase: 'choosing'; liftTypeId: LiftTypeId }
  | { phase: 'armed'; liftTypeId: LiftTypeId }
  | {
      phase: 'anchored';
      liftTypeId: LiftTypeId;
      a: [number, number];
      cursor: [number, number] | null;
      elev: [number | null, number | null];
      anchorElevStatus: LiveElevationStatus;
      cursorElevStatus: LiveElevationStatus;
    }
  | { phase: 'review'; draft: DraftLift };

export interface DraftLift {
  points: [[number, number], [number, number]];
  elev: [number | null, number | null];
  elevStatus: 'pending' | 'ok' | 'error';
  liftTypeId: LiftTypeId;
  status: SavedLift['status'];
  identifier: string;
  name: string;
}

export type LiftControllerAction =
  | { type: 'open' }
  | { type: 'start' }
  | { type: 'set-type'; liftTypeId: LiftTypeId }
  | { type: 'anchor'; point: [number, number] }
  | { type: 'move'; point: [number, number] }
  | { type: 'anchor-sample-succeeded'; elevation: number }
  | { type: 'anchor-sample-failed' }
  | { type: 'cursor-sample-succeeded'; elevation: number }
  | { type: 'cursor-sample-failed' }
  | { type: 'review'; points: [[number, number], [number, number]];
      identifier: string; name: string }
  | { type: 'patch'; patch: Partial<DraftLift> }
  | { type: 'sample-started' }
  | { type: 'sample-succeeded'; elevations: [number, number] }
  | { type: 'sample-failed' }
  | { type: 'cancel' };

export const IDLE_LIFT_TOOL: LiftTool = Object.freeze({ phase: 'idle' });

/** Pure lift workflow; map listeners and asynchronous identity live in the hook. */
export function reduceLiftTool(state: LiftTool, action: LiftControllerAction): LiftTool {
  switch (action.type) {
    case 'open': return state.phase === 'idle'
      ? { phase: 'choosing', liftTypeId: DEFAULT_LIFT_TYPE_ID }
      : state;
    case 'start': return state.phase === 'choosing'
      ? { phase: 'armed', liftTypeId: state.liftTypeId }
      : state;
    case 'set-type': {
      if (state.phase === 'choosing' || state.phase === 'armed' || state.phase === 'anchored') {
        return { ...state, liftTypeId: action.liftTypeId };
      }
      return state.phase === 'review'
        ? { ...state, draft: { ...state.draft, liftTypeId: action.liftTypeId } }
        : state;
    }
    case 'anchor': return state.phase === 'armed'
      ? {
          phase: 'anchored',
          liftTypeId: state.liftTypeId,
          a: action.point,
          cursor: null,
          elev: [null, null],
          anchorElevStatus: 'pending',
          cursorElevStatus: 'idle',
        }
      : state;
    case 'move': return state.phase === 'anchored'
      ? {
          ...state,
          cursor: action.point,
          elev: [state.elev[0], null],
          cursorElevStatus: 'pending',
        }
      : state;
    case 'anchor-sample-succeeded': return state.phase === 'anchored'
      ? { ...state, elev: [action.elevation, state.elev[1]], anchorElevStatus: 'ok' }
      : state;
    case 'anchor-sample-failed': return state.phase === 'anchored'
      ? { ...state, elev: [null, state.elev[1]], anchorElevStatus: 'error' }
      : state;
    case 'cursor-sample-succeeded': return state.phase === 'anchored'
      ? { ...state, elev: [state.elev[0], action.elevation], cursorElevStatus: 'ok' }
      : state;
    case 'cursor-sample-failed': return state.phase === 'anchored'
      ? { ...state, elev: [state.elev[0], null], cursorElevStatus: 'error' }
      : state;
    case 'review': return state.phase === 'anchored' ? { phase: 'review', draft: {
      points: action.points,
      elev: [null, null],
      elevStatus: 'pending',
      liftTypeId: state.liftTypeId,
      status: 'planning',
      identifier: action.identifier,
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
    identifier: draft.identifier.trim() || nextLiftIdentifier(existing),
    name: draft.name.trim() || nextLiftName(existing),
    liftTypeId: draft.liftTypeId,
    points: oriented.points,
    endpointElevM: oriented.elevs,
    lengthM: stats.lengthM,
    verticalM: stats.verticalM,
    status: draft.status,
    createdAt,
  };
}
