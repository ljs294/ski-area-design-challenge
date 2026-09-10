import type { DualCheckpoint, DualInitialization, DualPublication, DualSpeed, AdvanceRequest, ResortSimulationInput, OperationalSignal } from '../dualClock/model';
import type { SnowAddResult } from '../types/dualClock';
import type { SnowGrid } from '../types/snow';
import type { PreparedRoute } from '../dualClock/geometry';
import type { DualSnowPatch } from './dualSnowPublication';
import type { DualMovementFrame } from './dualMovementPublication';

export type DualCommand =
  | { type: 'recycle-movement'; buffer: ArrayBuffer }
  | { type: 'initialize'; input: DualInitialization }
  | { type: 'resort'; input: ResortSimulationInput }
  | { type: 'weather'; hours: DualInitialization['weather'] }
  | { type: 'timezone'; timezone: string }
  | { type: 'terrain'; terrain: NonNullable<DualInitialization['terrain']> }
  | { type: 'snow-add'; meters: number }
  | { type: 'advance'; targetMs: number }
  | { type: 'skip'; request: AdvanceRequest }
  | { type: 'speed'; speed: DualSpeed }
  | { type: 'select'; id: string | null; autoTrack?: boolean }
  | { type: 'acknowledge'; id: string }
  | { type: 'signal'; signal: OperationalSignal }
  | { type: 'pause' | 'play' | 'cancel' | 'resume' | 'follow' | 'checkpoint' };
export type DualWorkerRequest = DualCommand & { generation: number; requestId: number; committedRevision: number };
export interface DualWorkerResponse {
  generation: number; id: number; type: 'publication' | 'checkpoint' | 'error';
  committedRevision: number; operationGeneration: number;
  publication?: DualPublication; checkpoint?: DualCheckpoint; snow?: SnowGrid; snowAdd?: SnowAddResult; error?: string; busy?: boolean;
  snowPatch?: DualSnowPatch;
  movement?: DualMovementFrame;
  geometry?: Record<string, PreparedRoute>;
}
