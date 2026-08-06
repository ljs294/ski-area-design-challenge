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

/** Explicit ranks let each family state its paint and hit ownership while the
 * canonical arrays remain the human-readable invariant. */
export const MAP_Z_ORDER: Readonly<Record<MapFamilyId, number>> = Object.freeze(
  Object.fromEntries(MAP_LAYER_ORDER.map((id, index) => [id, index])) as Record<MapFamilyId, number>
);

export const MAP_HIT_RANK: Readonly<Record<MapHitFamilyId, number>> = Object.freeze(
  Object.fromEntries(MAP_HIT_PRIORITY.map((id, index) => [id, index])) as
    Record<MapHitFamilyId, number>
);

export interface MapContributionContext {
  readonly map: maplibregl.Map;
  readonly mapGeneration: number;
  readonly styleGeneration: number;
}

export interface MapVisibilityDescriptor {
  readonly id: string;
  readonly label: string;
  readonly layerIds: readonly string[];
  readonly visible: boolean;
  readonly exclusiveGroup?: string;
  readonly section?: 'Imagery' | 'Master plan' | 'Analysis' | 'Structures';
}

export interface ManagedMapHitContribution extends MapHitContribution {
  readonly priority: number;
  /** Defaults to layerIds. Kept independent for families with narrow hits but
   * a larger pointer-hover affordance. */
  readonly hoverLayerIds?: readonly string[];
}

/** Complete lifecycle owned by one map family. Controller extraction moves
 * these objects without moving registry policy or cross-family ordering. */
export interface ManagedMapContribution {
  readonly id: MapFamilyId;
  readonly zOrder: number;
  readonly hits?: readonly ManagedMapHitContribution[];
  install(context: MapContributionContext): void;
  synchronizeData(context: MapContributionContext): void;
  visibility?(context: MapContributionContext): readonly MapVisibilityDescriptor[];
  visibilityChanged?(
    context: MapContributionContext,
    descriptorId: string,
    visible: boolean,
  ): void;
  setCaptureTransient?(context: MapContributionContext, hidden: boolean): void;
  cleanup(context: MapContributionContext): void;
}

type HitEvent = maplibregl.MapLayerMouseEvent;
type HitListener = (event: HitEvent) => void;

interface HitBinding {
  readonly type: 'click' | 'mouseenter' | 'mouseleave';
  readonly layerIds: readonly string[];
  readonly listener: HitListener;
}

function managedOrder(contributions: readonly ManagedMapContribution[]): ManagedMapContribution[] {
  const ordered = requireEach(MAP_LAYER_ORDER, contributions, 'managed map layer')
    .sort((left, right) => left.zOrder - right.zOrder);
  for (let index = 0; index < ordered.length; index += 1) {
    const contribution = ordered[index];
    const expectedId = MAP_LAYER_ORDER[index];
    if (contribution.id !== expectedId || contribution.zOrder !== MAP_Z_ORDER[contribution.id]) {
      throw new Error(`Invalid z-order for map contribution ${contribution.id}.`);
    }
  }
  return ordered;
}

function managedHits(
  contributions: readonly ManagedMapContribution[],
): ManagedMapHitContribution[] {
  const hits = contributions.flatMap((contribution) => contribution.hits ?? []);
  const ordered = requireEach(MAP_HIT_PRIORITY, hits, 'managed map hit')
    .sort((left, right) => left.priority - right.priority);
  for (let index = 0; index < ordered.length; index += 1) {
    const hit = ordered[index];
    if (hit.id !== MAP_HIT_PRIORITY[index] || hit.priority !== MAP_HIT_RANK[hit.id]) {
      throw new Error(`Invalid hit priority for map contribution ${hit.id}.`);
    }
  }
  return ordered;
}

function sameDescriptorMetadata(
  left: MapVisibilityDescriptor,
  right: MapVisibilityDescriptor,
): boolean {
  return left.label === right.label &&
    left.exclusiveGroup === right.exclusiveGroup && left.section === right.section;
}

/**
 * Owns the live map/style generations and every cross-family traversal.
 * Contributions never reach through a MapView map ref: the registry passes an
 * explicit generation-stamped context to every lifecycle call.
 */
export class MapContributionRegistry {
  private readonly contributions: readonly ManagedMapContribution[];
  private readonly hits: readonly ManagedMapHitContribution[];
  private mapGeneration = 0;
  private styleGeneration = 0;
  private context: MapContributionContext | null = null;
  private hitBindings: HitBinding[] = [];
  private hitEnabled: () => boolean = () => true;
  private visibilityState = new Map<string, MapVisibilityDescriptor>();
  private descriptorOwners = new Map<string, ManagedMapContribution[]>();

  constructor(contributions: readonly ManagedMapContribution[]) {
    this.contributions = managedOrder(contributions);
    this.hits = managedHits(this.contributions);
  }

  attach(map: maplibregl.Map, hitEnabled: () => boolean = () => true): MapContributionContext {
    if (this.context) this.dispose();
    this.mapGeneration += 1;
    this.styleGeneration = 0;
    this.context = Object.freeze({ map, mapGeneration: this.mapGeneration, styleGeneration: 0 });
    this.hitEnabled = hitEnabled;
    this.attachHits();
    return this.context;
  }

  /** Reinstall bottom-to-top after a style load and reapply data/visibility. */
  synchronizeStyle(): MapVisibilityDescriptor[] {
    const previous = this.requireContext();
    if (previous.styleGeneration > 0) {
      for (const contribution of [...this.contributions].reverse()) contribution.cleanup(previous);
    }
    this.styleGeneration += 1;
    const context = Object.freeze({
      map: previous.map,
      mapGeneration: this.mapGeneration,
      styleGeneration: this.styleGeneration,
    });
    this.context = context;
    for (const contribution of this.contributions) {
      contribution.install(context);
      contribution.synchronizeData(context);
    }
    this.reconcileVisibility(context);
    return this.visibilityDescriptors();
  }

  /** Push current model state through one family, or all families. */
  synchronizeData(id?: MapFamilyId): void {
    const context = this.context;
    if (!context || context.styleGeneration === 0) return;
    for (const contribution of this.contributions) {
      if (!id || contribution.id === id) contribution.synchronizeData(context);
    }
  }

  setCaptureTransients(hidden: boolean): void {
    const context = this.context;
    if (!context || context.styleGeneration === 0) return;
    for (const contribution of this.contributions) {
      contribution.setCaptureTransient?.(context, hidden);
    }
  }

  toggleVisibility(id: string): MapVisibilityDescriptor[] {
    const target = this.visibilityState.get(id);
    const context = this.context;
    if (!target || !context || context.styleGeneration === 0) return this.visibilityDescriptors();
    const nextVisible = !target.visible;
    if (nextVisible && target.exclusiveGroup) {
      for (const [otherId, other] of this.visibilityState) {
        if (otherId !== id && other.visible && other.exclusiveGroup === target.exclusiveGroup) {
          this.setVisibility(context, otherId, false);
        }
      }
    }
    this.setVisibility(context, id, nextVisible);
    return this.visibilityDescriptors();
  }

  visibilityDescriptors(): MapVisibilityDescriptor[] {
    return [...this.visibilityState.values()].map((entry) => ({
      ...entry,
      layerIds: [...entry.layerIds],
    }));
  }

  dispose(): void {
    const context = this.context;
    if (!context) return;
    this.detachHits(context.map);
    if (context.styleGeneration > 0) {
      for (const contribution of [...this.contributions].reverse()) contribution.cleanup(context);
    }
    context.map.getCanvas().style.cursor = '';
    this.context = null;
    this.styleGeneration = 0;
  }

  private requireContext(): MapContributionContext {
    if (!this.context) throw new Error('Map contribution registry is not attached.');
    return this.context;
  }

  private reconcileVisibility(context: MapContributionContext): void {
    const next = new Map<string, MapVisibilityDescriptor>();
    const owners = new Map<string, ManagedMapContribution[]>();
    for (const contribution of this.contributions) {
      for (const descriptor of contribution.visibility?.(context) ?? []) {
        const existing = next.get(descriptor.id);
        if (existing && !sameDescriptorMetadata(existing, descriptor)) {
          throw new Error(`Conflicting visibility descriptor ${descriptor.id}.`);
        }
        const previous = this.visibilityState.get(descriptor.id);
        next.set(descriptor.id, {
          ...descriptor,
          layerIds: [...(existing?.layerIds ?? []), ...descriptor.layerIds],
          visible: previous?.visible ?? existing?.visible ?? descriptor.visible,
        });
        owners.set(descriptor.id, [...(owners.get(descriptor.id) ?? []), contribution]);
      }
    }
    this.visibilityState = next;
    this.descriptorOwners = owners;
    for (const descriptor of next.values()) {
      this.applyLayerVisibility(context, descriptor);
      this.notifyVisibility(context, descriptor.id, descriptor.visible);
    }
  }

  private setVisibility(context: MapContributionContext, id: string, visible: boolean): void {
    const current = this.visibilityState.get(id);
    if (!current || current.visible === visible) return;
    const next = { ...current, visible };
    this.visibilityState.set(id, next);
    this.applyLayerVisibility(context, next);
    this.notifyVisibility(context, id, visible);
  }

  private applyLayerVisibility(
    context: MapContributionContext,
    descriptor: MapVisibilityDescriptor,
  ): void {
    for (const layerId of descriptor.layerIds) {
      if (context.map.getLayer(layerId)) {
        context.map.setLayoutProperty(layerId, 'visibility', descriptor.visible ? 'visible' : 'none');
      }
    }
  }

  private notifyVisibility(
    context: MapContributionContext,
    id: string,
    visible: boolean,
  ): void {
    for (const owner of this.descriptorOwners.get(id) ?? []) {
      owner.visibilityChanged?.(context, id, visible);
    }
  }

  private attachHits(): void {
    const context = this.requireContext();
    const { map } = context;
    const hoverEnter: HitListener = () => {
      if (this.hitEnabled()) map.getCanvas().style.cursor = 'pointer';
    };
    const hoverLeave: HitListener = () => {
      if (this.hitEnabled()) map.getCanvas().style.cursor = '';
    };
    for (const hit of this.hits) {
      const guard = hitGuardLayers(hit.id, this.hits);
      const click: HitListener = (event) => {
        if (!this.hitEnabled()) return;
        const above = guard.filter((layerId) => map.getLayer(layerId));
        if (above.length && map.queryRenderedFeatures(event.point, { layers: above }).length) return;
        const id = event.features?.[0]?.properties?.id;
        if (typeof id === 'string') hit.select(id);
      };
      this.bindHit(map, 'click', hit.layerIds, click);
      const hoverLayers = hit.hoverLayerIds ?? hit.layerIds;
      this.bindHit(map, 'mouseenter', hoverLayers, hoverEnter);
      this.bindHit(map, 'mouseleave', hoverLayers, hoverLeave);
    }
  }

  private bindHit(
    map: maplibregl.Map,
    type: HitBinding['type'],
    layerIds: readonly string[],
    listener: HitListener,
  ): void {
    const layers = [...layerIds];
    map.on(type, layers, listener);
    this.hitBindings.push({ type, layerIds: layers, listener });
  }

  private detachHits(map: maplibregl.Map): void {
    for (const binding of this.hitBindings) {
      map.off(binding.type, [...binding.layerIds], binding.listener);
    }
    this.hitBindings = [];
  }
}
import type maplibregl from 'maplibre-gl';
