import type { AnchorRef } from '../types/anchors';
import type { EarthworkEstimate } from '../types/earthwork';
import type { SavedJunction } from '../types/topology';
import type { SavedTrail, SavedTrailPart, TrailDifficulty, TrailStatus } from '../types/trails';
import { nextTrailName, pinTrailEndpoints, trailPartsStats,
  difficultyForSlopes } from '../trails';
import { withTopologyPart } from '../topology';
import type { PaintMode } from './trailPaintEngine';
import type { TrailHeadAnchor, TrailTailAnchor } from './trailHeadAnchor';

export interface DraftTrail {
  parts: SavedTrailPart[];
  /** Terrain-sampled parts retained so an unchecked preview is lossless. */
  ungradedParts: SavedTrailPart[];
  areaM2: number;
  /** Exact painted area restored when grading is unchecked. */
  ungradedAreaM2: number;
  brushWidthM: number;
  name: string;
  status: TrailStatus;
  difficulty: TrailDifficulty;
  elevStatus: 'pending' | 'ok' | 'error';
  elevError?: string | null;
  gradingEnabled: boolean;
  gradingStatus: 'idle' | 'pending' | 'ok' | 'error';
  gradingError: string | null;
  earthwork: EarthworkEstimate | null;
  maxGroundCrossSlopePct: number;
  maxFaceSlopePct: number;
  maxDisturbedWidthM: number;
  ungradedLengthM: number;
  infeasibleLines: [number, number][][];
  anchor: AnchorRef | null;
  tailAnchor?: AnchorRef | null;
}

export interface TrailPaintState {
  mode: PaintMode;
  polygons: [number, number][][][];
  areaM2: number;
  activeAreaM2: number | null;
  canUndo: boolean;
  pending: boolean;
  error: string | null;
  anchor: TrailHeadAnchor;
  hasUserStroke: boolean;
}

export type TrailTool =
  | { phase: 'idle' }
  | { phase: 'place-head'; candidate: TrailHeadAnchor | null; error: string | null }
  | ({ phase: 'paint' } & TrailPaintState)
  | ({ phase: 'place-tail'; candidate: TrailTailAnchor | null } & TrailPaintState)
  | { phase: 'analyzing'; polygons: [number, number][][][]; areaM2: number;
      anchor: TrailHeadAnchor; tailAnchor: TrailTailAnchor }
  | { phase: 'review'; draft: DraftTrail };

type PaintPatch = Partial<Omit<TrailPaintState, 'anchor'>>;

export type TrailToolAction =
  | { type: 'arm' }
  | { type: 'head-candidate'; candidate: TrailHeadAnchor | null; error?: string | null }
  | { type: 'begin-paint'; anchor: TrailHeadAnchor }
  | { type: 'paint-patch'; patch: PaintPatch }
  | { type: 'place-tail' }
  | { type: 'tail-candidate'; candidate: TrailTailAnchor | null; error: string | null }
  | { type: 'analyze'; tailAnchor: TrailTailAnchor }
  | { type: 'analysis-failed'; error: string; canUndo: boolean; hasUserStroke: boolean }
  | { type: 'review'; draft: DraftTrail }
  | { type: 'review-patch'; patch: Partial<DraftTrail> }
  | { type: 'back-to-paint' }
  | { type: 'cancel' };

export const IDLE_TRAIL_TOOL: TrailTool = Object.freeze({ phase: 'idle' });

export function reduceTrailTool(state: TrailTool, action: TrailToolAction): TrailTool {
  switch (action.type) {
    case 'arm': return { phase: 'place-head', candidate: null, error: null };
    case 'head-candidate': return state.phase === 'place-head'
      ? { ...state, candidate: action.candidate,
          error: action.error === undefined ? (action.candidate ? null : state.error) : action.error }
      : state;
    case 'begin-paint': return { phase: 'paint', mode: 'paint', polygons: [], areaM2: 0,
      activeAreaM2: null, canUndo: false, pending: true, error: null,
      anchor: action.anchor, hasUserStroke: false };
    case 'paint-patch': return state.phase === 'paint' ? { ...state, ...action.patch } : state;
    case 'place-tail': return state.phase === 'paint'
      ? { ...state, phase: 'place-tail', candidate: null, error: null } : state;
    case 'tail-candidate': return state.phase === 'place-tail'
      ? { ...state, candidate: action.candidate, error: action.error } : state;
    case 'analyze': return state.phase === 'place-tail'
      ? { phase: 'analyzing', polygons: state.polygons, areaM2: state.areaM2,
          anchor: state.anchor, tailAnchor: action.tailAnchor } : state;
    case 'analysis-failed': {
      if (state.phase !== 'analyzing') return state;
      return { phase: 'place-tail', mode: 'paint', polygons: state.polygons,
        areaM2: state.areaM2, activeAreaM2: null, canUndo: action.canUndo,
        pending: false, error: action.error, anchor: state.anchor,
        hasUserStroke: action.hasUserStroke, candidate: null };
    }
    case 'review': return { phase: 'review', draft: action.draft };
    case 'review-patch': return state.phase === 'review'
      ? { phase: 'review', draft: { ...state.draft, ...action.patch } } : state;
    case 'back-to-paint': return state.phase === 'place-tail'
      ? { phase: 'paint', mode: state.mode, polygons: state.polygons, areaM2: state.areaM2,
          activeAreaM2: null, canUndo: state.canUndo, pending: false, error: null,
          anchor: state.anchor, hasUserStroke: state.hasUserStroke } : state;
    case 'cancel': return IDLE_TRAIL_TOOL;
  }
}

export function createTrailDraft(
  parts: SavedTrailPart[],
  areaM2: number,
  brushWidthM: number,
  existing: readonly SavedTrail[],
  anchor: TrailHeadAnchor,
  tailAnchor: TrailTailAnchor,
): DraftTrail {
  return { parts, ungradedParts: parts, areaM2, ungradedAreaM2: areaM2,
    brushWidthM, name: nextTrailName([...existing]), status: 'planning', difficulty: 'blue',
    elevStatus: 'pending', elevError: null, gradingEnabled: false, gradingStatus: 'idle',
    gradingError: null, earthwork: null, maxGroundCrossSlopePct: 0, maxFaceSlopePct: 0,
    maxDisturbedWidthM: 0, ungradedLengthM: 0, infeasibleLines: [], anchor, tailAnchor };
}

export function buildSavedTrail(
  draft: DraftTrail,
  existing: readonly SavedTrail[],
  id: string,
  createdAt: string,
  headJunction: SavedJunction,
  tailJunction: SavedJunction,
): { trail: SavedTrail; commitGrading: boolean } | null {
  const commitGrading = draft.status === 'complete' && draft.gradingEnabled;
  if (commitGrading && draft.gradingStatus !== 'ok') return null;
  const head = draft.anchor, tail = draft.tailAnchor;
  if (!head || !tail || (head.kind !== 'lift' && head.kind !== 'trail') ||
      (tail.kind !== 'lift' && tail.kind !== 'trail')) return null;
  if ((head.kind === 'lift' && head.end !== 'top') ||
      (tail.kind === 'lift' && tail.end !== 'base') || headJunction.id === tailJunction.id) return null;
  const pinned = pinTrailEndpoints(commitGrading ? draft.parts : draft.ungradedParts,
    head.point, tail.point);
  if (!pinned) return null;
  const parts = pinned.map((part, index) => withTopologyPart(part, headJunction.id,
    tailJunction.id, `${id}:${index}:segment:0`));
  const stats = trailPartsStats(parts);
  return { commitGrading, trail: {
    id, name: draft.name.trim() || nextTrailName([...existing]), parts,
    brushWidthM: draft.brushWidthM, areaM2: draft.areaM2,
    lengthM: stats.lengthM, verticalM: stats.verticalM,
    avgSlopeDeg: stats.avgSlopeDeg, maxSlopeDeg: stats.maxSlopeDeg,
    difficulty: difficultyForSlopes(stats.avgSlopeDeg, stats.maxSlopeDeg),
    terrainGraded: commitGrading,
    earthwork: commitGrading && draft.earthwork ? draft.earthwork : undefined,
    status: draft.status, anchor: head, createdAt,
  } };
}
