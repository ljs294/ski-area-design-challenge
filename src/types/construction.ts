/** Shared persisted build state for constructed resort entities. */
export type ConstructionStatus = 'planning' | 'complete';

/** Public compatibility name retained for lift consumers and saved data. */
export type LiftStatus = ConstructionStatus;

/** Public compatibility name retained for trail consumers and saved data. */
export type TrailStatus = ConstructionStatus;
