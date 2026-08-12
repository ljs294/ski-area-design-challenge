import type { SnowmakingAnalysisInput, SnowmakingAnalysisResult } from '../snowmakingHydraulics';

export interface SnowmakingAnalysisRequest {
  id: number;
  input: SnowmakingAnalysisInput;
}

export type SnowmakingAnalysisResponse =
  | { id: number; ok: true; result: SnowmakingAnalysisResult }
  | { id: number; ok: false; error: string };
