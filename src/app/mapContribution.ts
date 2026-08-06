/**
 * Where every map family sits, and which family a click belongs to.
 *
 * These are one invariant expressed twice — the family drawn on top is the
 * family a click lands on — but they used to be maintained in two hand-written
 * places: the layer-add sequence in `reinitAfterStyle`, and a `priorityLayers`
 * array re-accumulated inside each of the seven click handlers. Re-accumulating
 * it seven times is how the run handler ended up yielding to snowmaking nodes
 * but not to lifts, so a click where a lift crossed a run selected the run.
 * Both orders are declared once here and every guard is derived from the
 * declaration.
 */

/** Bottom-to-top paint order. Later families draw over earlier ones. */
export const MAP_LAYER_ORDER = [
  'analysis',
  'site-boundary',
  'road',
  'dam',
  'pond',
  'ski-node-path',
  'trail',
  'lift',
  'snowmaking',
] as const;

export type MapFamilyId = (typeof MAP_LAYER_ORDER)[number];

/**
 * Top-to-bottom click order, the mirror of the paint order. Streams and lakes
 * are drawn inside the analysis family but pick separately, after every built
 * structure, so hit order names them in their own right.
 */
export const MAP_HIT_PRIORITY = [
  'snowmaking',
  'lift',
  'trail',
  'dam',
  'pond',
  'stream',
  'lake',
] as const;

export type MapHitFamilyId = (typeof MAP_HIT_PRIORITY)[number];

export interface MapContribution {
  readonly id: MapFamilyId;
  /** Install this family's sources and layers, then push its current data. */
  install(): void;
  /**
   * Hide (`true`) or restore (`false`) this family's in-progress overlays for
   * the resume-preview capture. Families that draw nothing transient omit it.
   */
  setCaptureTransient?(hidden: boolean): void;
}

export interface MapHitContribution {
  readonly id: MapHitFamilyId;
  /** The layers a click on this family is delegated to. */
  readonly layerIds: readonly string[];
  select(featureId: string): void;
}

function requireEach<Id, T extends { id: Id }>(
  order: readonly Id[],
  contributions: readonly T[],
  what: string,
): T[] {
  const byId = new Map<Id, T>();
  for (const contribution of contributions) {
    if (byId.has(contribution.id)) {
      throw new Error(`Duplicate ${what} contribution ${String(contribution.id)}.`);
    }
    byId.set(contribution.id, contribution);
  }
  return order.map((id) => {
    const contribution = byId.get(id);
    if (!contribution) throw new Error(`Missing ${what} contribution ${String(id)}.`);
    return contribution;
  });
}

/** Sort contributions into paint order, refusing an incomplete or repeated set. */
export function orderContributions(contributions: readonly MapContribution[]): MapContribution[] {
  return requireEach(MAP_LAYER_ORDER, contributions, 'map layer');
}

/** Sort hit contributions into click order, refusing an incomplete or repeated set. */
export function orderHitContributions(
  contributions: readonly MapHitContribution[],
): MapHitContribution[] {
  return requireEach(MAP_HIT_PRIORITY, contributions, 'map hit');
}

/**
 * Every layer belonging to a family that picks ahead of `id`, in priority
 * order. A handler that finds a rendered feature in these layers must yield:
 * the click belongs to whatever is drawn above it.
 */
export function hitGuardLayers(
  id: MapHitFamilyId,
  contributions: readonly MapHitContribution[],
): string[] {
  const rank = MAP_HIT_PRIORITY.indexOf(id);
  if (rank < 0) throw new Error(`Unknown map hit family ${String(id)}.`);
  return MAP_HIT_PRIORITY.slice(0, rank).flatMap((family) => {
    const above = contributions.find((contribution) => contribution.id === family);
    return above ? [...above.layerIds] : [];
  });
}
