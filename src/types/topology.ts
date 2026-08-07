import type { AnchorRef } from './anchors';
import type { ConstructionStatus } from './construction';

export interface SavedNode {
  id: string;
  name: string;
  point: [number, number];
  elevM: number | null;
  anchor?: AnchorRef;
  createdAt: string;
}

export interface SavedJunction {
  id: string;
  point: [number, number];
  elevM: number | null;
  liftTerminal?: { liftId: string; end: 'top' | 'base' };
  legacyAnchor?: AnchorRef;
  createdAt: string;
}

export interface SavedPath {
  id: string;
  name: string;
  points: [number, number][];
  pointElevM: number[];
  widthM: number;
  from: AnchorRef;
  to: AnchorRef;
  fromJunctionId?: string;
  toJunctionId?: string;
  lengthM: number;
  status: ConstructionStatus;
  closed?: boolean;
  createdAt: string;
}

export interface SavedTrailSegment {
  id: string;
  centerline: [number, number][];
  centerlineElevM: number[];
  fromJunctionId: string;
  toJunctionId: string;
}
