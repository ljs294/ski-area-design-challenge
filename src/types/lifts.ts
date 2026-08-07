import type { ConstructionStatus } from './construction';

export type LiftClass = 'fixed-grip';
export type ChairSize = 2 | 3 | 4;
export type LiftStatus = ConstructionStatus;

export interface SavedLiftBase {
  id: string;
  /** User-facing letter or number. Optional only for schema-11 compatibility. */
  identifier?: string;
  /** The lift's actual name, independent of its letter/number identifier. */
  name: string;
  liftClass: LiftClass;
  points: [[number, number], [number, number]];
  endpointElevM: [number | null, number | null];
  lengthM: number;
  verticalM: number | null;
  status: LiftStatus;
  closed?: boolean;
  createdAt: string;
}

export interface SavedFixedGripLift extends SavedLiftBase {
  liftClass: 'fixed-grip';
  chairSize: ChairSize;
}

export type SavedLift = SavedFixedGripLift;
