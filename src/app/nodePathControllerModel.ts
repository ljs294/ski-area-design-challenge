import type { AnchorRef } from '../types/anchors';
import type { SavedPath } from '../types/topology';
import { DEFAULT_PATH_WIDTH_M, nextPathName, pathLengthM } from '../skiNodes';

export type NodeTool =
  | { phase: 'idle' }
  | { phase: 'add'; candidate: Extract<AnchorRef, { kind: 'trail' }> | null; error: string | null }
  | { phase: 'remove'; junctionId: string | null; error: string | null };

export type PathTool =
  | { phase: 'idle' }
  | { phase: 'armed' }
  | { phase: 'drawing'; points: [number, number][]; cursor: [number, number] | null;
      from: AnchorRef | null }
  | { phase: 'review'; points: [number, number][]; from: AnchorRef; to: AnchorRef; name: string };

export type NodeToolAction =
  | { type: 'arm'; phase: 'add' | 'remove' }
  | { type: 'add-candidate'; candidate: Extract<AnchorRef, { kind: 'trail' }> | null;
      error: string | null }
  | { type: 'remove-candidate'; junctionId: string | null; error: string | null }
  | { type: 'committed' }
  | { type: 'cancel' };

export type PathToolAction =
  | { type: 'arm' }
  | { type: 'start'; anchor: AnchorRef }
  | { type: 'add-point'; point: [number, number] }
  | { type: 'move'; point: [number, number] }
  | { type: 'undo' }
  | { type: 'review'; to: AnchorRef; name: string }
  | { type: 'rename'; name: string }
  | { type: 'cancel' };

export const IDLE_NODE_TOOL: NodeTool = Object.freeze({ phase: 'idle' });
export const IDLE_PATH_TOOL: PathTool = Object.freeze({ phase: 'idle' });

export function reduceNodeTool(state: NodeTool, action: NodeToolAction): NodeTool {
  switch (action.type) {
    case 'arm': return action.phase === 'add'
      ? { phase: 'add', candidate: null, error: null }
      : { phase: 'remove', junctionId: null, error: null };
    case 'add-candidate': return state.phase === 'add'
      ? { ...state, candidate: action.candidate, error: action.error } : state;
    case 'remove-candidate': return state.phase === 'remove'
      ? { ...state, junctionId: action.junctionId, error: action.error } : state;
    case 'committed':
      if (state.phase === 'add') return { ...state, candidate: null, error: null };
      if (state.phase === 'remove') return { ...state, junctionId: null, error: null };
      return state;
    case 'cancel': return IDLE_NODE_TOOL;
  }
}

export function reducePathTool(state: PathTool, action: PathToolAction): PathTool {
  switch (action.type) {
    case 'arm': return { phase: 'armed' };
    case 'start': return state.phase === 'armed' ? {
      phase: 'drawing', points: [action.anchor.point], cursor: null, from: action.anchor,
    } : state;
    case 'add-point': return state.phase === 'drawing'
      ? { ...state, points: [...state.points, action.point], cursor: null } : state;
    case 'move': return state.phase === 'drawing' ? { ...state, cursor: action.point } : state;
    case 'undo':
      if (state.phase !== 'drawing') return state;
      return state.points.length <= 1
        ? { phase: 'armed' }
        : { ...state, points: state.points.slice(0, -1), cursor: null };
    case 'review': return state.phase === 'drawing' && state.from ? {
      phase: 'review', points: [...state.points.slice(0, -1), action.to.point],
      from: state.from, to: action.to, name: action.name,
    } : state;
    case 'rename': return state.phase === 'review' ? { ...state, name: action.name } : state;
    case 'cancel': return IDLE_PATH_TOOL;
  }
}

export function pathFromReview(
  tool: Extract<PathTool, { phase: 'review' }>,
  existing: readonly SavedPath[],
  id: string,
  createdAt: string,
  fromJunctionId?: string,
  toJunctionId?: string,
): SavedPath {
  return {
    id, name: tool.name.trim() || nextPathName([...existing]), points: tool.points,
    pointElevM: [],
    from: tool.from, to: tool.to, widthM: DEFAULT_PATH_WIDTH_M,
    fromJunctionId, toJunctionId,
    lengthM: pathLengthM(tool.points), status: 'complete', createdAt,
  };
}
