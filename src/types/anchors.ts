/** Where a run or path attaches to the rest of the mountain. */
export type AnchorRef =
  | { kind: 'lift'; liftId: string; end: 'top' | 'base'; point: [number, number] }
  | { kind: 'trail'; trailId: string; point: [number, number] }
  | { kind: 'path'; pathId: string; point: [number, number] }
  | { kind: 'node'; nodeId: string; point: [number, number] };
