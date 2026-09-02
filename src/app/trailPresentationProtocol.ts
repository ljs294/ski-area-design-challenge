import type { TrailPresentationInput, TrailPresentationResult } from '../types/trailPresentation';

export interface TrailPresentationRequest {
  id: number;
  type: 'compile';
  input: TrailPresentationInput;
}

export type TrailPresentationResponse =
  | { id: number; ok: true; result: TrailPresentationResult }
  | { id: number; ok: false; error: string };
