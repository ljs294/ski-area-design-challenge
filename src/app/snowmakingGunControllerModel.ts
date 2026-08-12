import type { SnowgunVariantId } from '../types/snowmaking';

export interface SnowgunDraftPoint {
  draftId: string;
  variantId: SnowgunVariantId;
  point: [number, number];
  elevM: number | null;
}

export interface SnowgunMoveCandidate {
  point: [number, number];
  elevM: number | null;
}

export type SnowgunTool =
  | { phase: 'idle' }
  | { phase: 'placing'; variantId: SnowgunVariantId; items: SnowgunDraftPoint[];
      cursor: SnowgunMoveCandidate | null; error: string | null }
  | { phase: 'review'; variantId: SnowgunVariantId; items: SnowgunDraftPoint[];
      revision: number; error: string | null }
  | { phase: 'moving'; gunId: string; candidate: SnowgunMoveCandidate | null;
      revision: number; error: string | null };

export type SnowgunAction =
  | { type: 'arm'; variantId: SnowgunVariantId }
  | { type: 'variant'; variantId: SnowgunVariantId }
  | { type: 'cursor'; candidate: SnowgunMoveCandidate | null }
  | { type: 'add'; item: SnowgunDraftPoint }
  | { type: 'remove'; draftId: string }
  | { type: 'undo' }
  | { type: 'review'; revision: number }
  | { type: 'back' }
  | { type: 'review-error'; error: string | null; revision?: number }
  | { type: 'move'; gunId: string; revision: number }
  | { type: 'move-candidate'; candidate: SnowgunMoveCandidate | null }
  | { type: 'cancel' };

export const IDLE_SNOWGUN_TOOL: SnowgunTool = Object.freeze({ phase: 'idle' });

export function reduceSnowgunTool(state: SnowgunTool, action: SnowgunAction): SnowgunTool {
  switch (action.type) {
    case 'arm': return { phase: 'placing', variantId: action.variantId, items: [],
      cursor: null, error: null };
    case 'variant': return state.phase === 'placing' || state.phase === 'review'
      ? { ...state, variantId: action.variantId, error: null } : state;
    case 'cursor': return state.phase === 'placing' ? { ...state, cursor: action.candidate } : state;
    case 'add': return state.phase === 'placing'
      ? { ...state, items: [...state.items, action.item], cursor: null, error: null } : state;
    case 'remove': return state.phase === 'placing' || state.phase === 'review'
      ? { ...state, items: state.items.filter((item) => item.draftId !== action.draftId), error: null }
      : state;
    case 'undo': return state.phase === 'placing' && state.items.length > 0
      ? { ...state, items: state.items.slice(0, -1), error: null } : state;
    case 'review': return state.phase === 'placing' && state.items.length > 0
      ? { phase: 'review', variantId: state.variantId, items: state.items,
        revision: action.revision, error: null } : state;
    case 'back': return state.phase === 'review'
      ? { phase: 'placing', variantId: state.variantId, items: state.items,
        cursor: null, error: null } : state;
    case 'review-error': return state.phase === 'review' || state.phase === 'moving'
      ? { ...state, error: action.error,
        revision: action.revision ?? state.revision } : state;
    case 'move': return { phase: 'moving', gunId: action.gunId, candidate: null,
      revision: action.revision, error: null };
    case 'move-candidate': return state.phase === 'moving'
      ? { ...state, candidate: action.candidate, error: null } : state;
    case 'cancel': return IDLE_SNOWGUN_TOOL;
  }
}
