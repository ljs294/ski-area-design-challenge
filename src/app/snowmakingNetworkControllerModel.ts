import type { NumberedSnowmakingNodeKind, SnowmakingPipeDiameterIn } from '../types/snowmaking';

export type SnowmakingSnapIntent =
  | { kind: 'node'; nodeId: string; point: [number, number] }
  | { kind: 'pipe'; pipeId: string; point: [number, number] };

export interface SnowmakingDraftPoint {
  point: [number, number];
  snap: SnowmakingSnapIntent | null;
}

export type SnowmakingPipeTool =
  | { phase: 'idle' }
  | { phase: 'armed' }
  | { phase: 'drawing'; points: SnowmakingDraftPoint[]; cursor: [number, number] | null;
      cursorSnap: SnowmakingSnapIntent | null }
  | { phase: 'review'; points: SnowmakingDraftPoint[]; name: string; error: string | null };

export interface SnowmakingNodeCandidate {
  point: [number, number];
  snap: SnowmakingSnapIntent | null;
  elevM: number | null;
}

export type SnowmakingNodeTool =
  | { phase: 'idle' }
  | { phase: 'placing'; kind: Extract<NumberedSnowmakingNodeKind, 'pump' | 'hydrant'>;
      candidate: SnowmakingNodeCandidate | null; error: string | null };

export type SnowmakingPipeAction =
  | { type: 'arm' }
  | { type: 'add'; point: SnowmakingDraftPoint }
  | { type: 'move'; point: [number, number]; snap: SnowmakingSnapIntent | null }
  | { type: 'undo' }
  | { type: 'review'; name: string }
  | { type: 'rename'; name: string }
  | { type: 'review-error'; error: string | null }
  | { type: 'cancel' };

export type SnowmakingNodeAction =
  | { type: 'arm'; kind: 'pump' | 'hydrant' }
  | { type: 'candidate'; candidate: SnowmakingNodeCandidate | null; error: string | null }
  | { type: 'committed' }
  | { type: 'cancel' };

export const IDLE_SNOWMAKING_PIPE_TOOL: SnowmakingPipeTool = Object.freeze({ phase: 'idle' });
export const IDLE_SNOWMAKING_NODE_TOOL: SnowmakingNodeTool = Object.freeze({ phase: 'idle' });

export function reduceSnowmakingPipeTool(state: SnowmakingPipeTool,
  action: SnowmakingPipeAction): SnowmakingPipeTool {
  switch (action.type) {
    case 'arm': return { phase: 'armed' };
    case 'add':
      if (state.phase === 'armed') return { phase: 'drawing', points: [action.point],
        cursor: null, cursorSnap: null };
      if (state.phase === 'drawing') return { ...state, points: [...state.points, action.point],
        cursor: null, cursorSnap: null };
      return state;
    case 'move': return state.phase === 'drawing'
      ? { ...state, cursor: action.point, cursorSnap: action.snap } : state;
    case 'undo':
      if (state.phase !== 'drawing') return state;
      return state.points.length <= 1 ? { phase: 'armed' }
        : { ...state, points: state.points.slice(0, -1), cursor: null, cursorSnap: null };
    case 'review': return state.phase === 'drawing' && state.points.length >= 2
      ? { phase: 'review', points: state.points, name: action.name, error: null } : state;
    case 'rename': return state.phase === 'review' ? { ...state, name: action.name } : state;
    case 'review-error': return state.phase === 'review' ? { ...state, error: action.error } : state;
    case 'cancel': return IDLE_SNOWMAKING_PIPE_TOOL;
  }
}

export function reduceSnowmakingNodeTool(state: SnowmakingNodeTool,
  action: SnowmakingNodeAction): SnowmakingNodeTool {
  switch (action.type) {
    case 'arm': return { phase: 'placing', kind: action.kind, candidate: null, error: null };
    case 'candidate': return state.phase === 'placing'
      ? { ...state, candidate: action.candidate, error: action.error } : state;
    case 'committed': return state.phase === 'placing'
      ? { ...state, candidate: null, error: null } : state;
    case 'cancel': return IDLE_SNOWMAKING_NODE_TOOL;
  }
}

export interface SnowmakingToolOptionsState {
  diameterIn: SnowmakingPipeDiameterIn;
  snapping: boolean;
}
