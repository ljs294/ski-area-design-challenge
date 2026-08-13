import type { ConstructionStatus } from './construction';

export type LiftCategoryId =
  | 'surface'
  | 'fixed-grip-chairlift'
  | 'detachable-chairlift'
  | 'detachable-gondola'
  | 'tram';

export type LiftTypeId =
  | 'rope-tow'
  | 'magic-carpet'
  | 't-bar'
  | 'fixed-grip-double'
  | 'fixed-grip-triple'
  | 'fixed-grip-quad'
  | 'detachable-quad'
  | 'detachable-six-pack'
  | 'detachable-eight-pack'
  | 'gondola-8'
  | 'gondola-10'
  | 'gondola-12'
  | 'tram-60'
  | 'tram-80';
export type LiftStatus = ConstructionStatus;

export interface SavedLift {
  id: string;
  /** User-facing letter or number. Optional only for schema-11 compatibility. */
  identifier?: string;
  /** The lift's actual name, independent of its letter/number identifier. */
  name: string;
  /** Authoritative schema-14 leaf type. Category and performance are derived. */
  liftTypeId: LiftTypeId;
  points: [[number, number], [number, number]];
  endpointElevM: [number | null, number | null];
  lengthM: number;
  verticalM: number | null;
  status: LiftStatus;
  closed?: boolean;
  createdAt: string;
}
