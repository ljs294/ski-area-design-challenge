import type { NumberedSnowmakingNodeKind, SnowmakingPipeDiameterIn } from '../types/snowmaking';
import type { SnowmakingPipeStation } from '../snowmakingNetwork';

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

export type SnowmakingHydrantRunTool =
  | { phase: 'idle' }
  | { phase: 'select-pipe'; error: string | null }
  | { phase: 'select-start'; pipeId: string; error: string | null }
  | { phase: 'select-end'; pipeId: string; start: SnowmakingPipeStation; error: string | null }
  | { phase: 'review'; pipeId: string; start: SnowmakingPipeStation; end: SnowmakingPipeStation;
      mode: 'count' | 'spacing'; count: number; spacingM: number; revision: number;
      error: string | null };

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

export type SnowmakingHydrantRunAction =
  | { type: 'arm' }
  | { type: 'pipe'; pipeId: string }
  | { type: 'start'; station: SnowmakingPipeStation }
  | { type: 'end'; station: SnowmakingPipeStation; revision: number }
  | { type: 'back' }
  | { type: 'mode'; mode: 'count' | 'spacing' }
  | { type: 'count'; count: number }
  | { type: 'spacing'; spacingM: number }
  | { type: 'error'; error: string | null; revision?: number }
  | { type: 'cancel' };

export const IDLE_SNOWMAKING_PIPE_TOOL: SnowmakingPipeTool = Object.freeze({ phase: 'idle' });
export const IDLE_SNOWMAKING_NODE_TOOL: SnowmakingNodeTool = Object.freeze({ phase: 'idle' });
export const IDLE_SNOWMAKING_HYDRANT_RUN_TOOL: SnowmakingHydrantRunTool = Object.freeze({ phase: 'idle' });

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

export function reduceSnowmakingHydrantRunTool(
  state: SnowmakingHydrantRunTool,
  action: SnowmakingHydrantRunAction,
): SnowmakingHydrantRunTool {
  switch (action.type) {
    case 'arm': return { phase: 'select-pipe', error: null };
    case 'pipe': return state.phase === 'select-pipe'
      ? { phase: 'select-start', pipeId: action.pipeId, error: null } : state;
    case 'start': return state.phase === 'select-start'
      ? { phase: 'select-end', pipeId: state.pipeId, start: action.station, error: null } : state;
    case 'end': return state.phase === 'select-end'
      ? { phase: 'review', pipeId: state.pipeId, start: state.start, end: action.station,
        mode: 'count', count: 2, spacingM: 30, revision: action.revision, error: null } : state;
    case 'back':
      if (state.phase === 'review') return { phase: 'select-end', pipeId: state.pipeId,
        start: state.start, error: null };
      if (state.phase === 'select-end') return { phase: 'select-start', pipeId: state.pipeId, error: null };
      if (state.phase === 'select-start') return { phase: 'select-pipe', error: null };
      return state;
    case 'mode': return state.phase === 'review' ? { ...state, mode: action.mode, error: null } : state;
    case 'count': return state.phase === 'review' ? { ...state, count: action.count, error: null } : state;
    case 'spacing': return state.phase === 'review'
      ? { ...state, spacingM: action.spacingM, error: null } : state;
    case 'error': return state.phase === 'idle' ? state : { ...state, error: action.error,
      ...(state.phase === 'review' && action.revision != null ? { revision: action.revision } : {}) };
    case 'cancel': return IDLE_SNOWMAKING_HYDRANT_RUN_TOOL;
  }
}

export interface SnowmakingToolOptionsState {
  diameterIn: SnowmakingPipeDiameterIn;
  snapping: boolean;
}

/** Geometry that remains visible while a pipe is being drawn or reviewed. */
export function snowmakingPipePreview(tool: SnowmakingPipeTool): {
  points: [number, number][];
  cursor: [number, number] | null;
} | null {
  if (tool.phase === 'drawing') return {
    points: tool.points.map((draft) => draft.point),
    cursor: tool.cursor,
  };
  if (tool.phase === 'review') return {
    points: tool.points.map((draft) => draft.point),
    cursor: null,
  };
  return null;
}
