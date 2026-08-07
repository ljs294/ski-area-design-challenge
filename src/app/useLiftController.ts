import { useEffect, useReducer, useRef, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SavedLift } from '../types/lifts';
import { haversineMeters } from '../geo';
import { nextLiftName } from '../lifts';
import { addLiftLayers, setLiftData, liftsToGeoJSON, LIFT_BUILT_LAYER_IDS } from './liftLayers';
import type { DraftLine } from './liftLayers';
import { MAP_HIT_RANK, MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution, MapVisibilityDescriptor } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import {
  IDLE_LIFT_TOOL,
  liftFromDraft,
  reduceLiftTool,
  type DraftLift,
  type LiftTool,
} from './liftControllerModel';

const MIN_LIFT_M = 50;

export interface LiftCollectionCommands {
  add(lift: SavedLift): void;
  patch(id: string, patch: Partial<SavedLift>): void;
  remove(id: string): void;
}

export interface LiftControllerOptions {
  mapRef: RefObject<maplibregl.Map | null>;
  lifts: readonly SavedLift[];
  commands: LiftCollectionCommands;
  canArm(): boolean;
  activate(): boolean;
  release(): void;
  clearSelection(): void;
  select(id: string): void;
  clearSelected(id: string): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  sampleTerrain(
    lng: number,
    lat: number,
    zoom: number,
  ): Promise<{ elevation: number } | null>;
  runConstruction(operation: () => Promise<void>): Promise<unknown>;
  clearCover(lift: SavedLift): Promise<void>;
  createId(): string;
  now(): string;
  structuresVisible(): boolean;
  synchronizeMap(): void;
}

export interface LiftController {
  readonly state: LiftTool;
  readonly contribution: ManagedMapContribution;
  arm(): void;
  cancel(): void;
  dispose(): void;
  patchDraft(patch: Partial<DraftLift>): void;
  retryElevation(): void;
  confirm(): Promise<void>;
  select(id: string): void;
  patch(id: string, patch: Partial<SavedLift>): void;
  remove(id: string): void;
}

function draftLineOf(tool: LiftTool): DraftLine | null {
  if (tool.phase === 'anchored') return { points: [tool.a, tool.cursor ?? tool.a] };
  if (tool.phase === 'review') return { points: tool.draft.points };
  return null;
}

/** Lift workflow owner. Presentation and committed collection ownership stay outside. */
export function useLiftController(options: LiftControllerOptions): LiftController {
  const [state, dispatch] = useReducer(reduceLiftTool, IDLE_LIFT_TOOL);
  const stateRef = useRef<LiftTool>(state);
  const liftsRef = useRef<readonly SavedLift[]>(options.lifts);
  const optionsRef = useRef(options);
  const sampleTokenRef = useRef(0);
  stateRef.current = state;
  liftsRef.current = options.lifts;
  optionsRef.current = options;

  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) {
    contributionRef.current = {
      id: 'lift',
      zOrder: MAP_Z_ORDER.lift,
      hits: [{
        id: 'lift',
        priority: MAP_HIT_RANK.lift,
        layerIds: ['lift-line-casing', 'lift-terminals'],
        select: (id) => select(id),
      }],
      install: ({ map }) => addLiftLayers(map),
      synchronizeData: ({ map }) => setLiftData(
        map,
        liftsToGeoJSON(liftsRef.current, draftLineOf(stateRef.current)),
      ),
      visibility: (): MapVisibilityDescriptor[] =>
        optionsRef.current.structuresVisible() ? [{
          id: 'lifts',
          label: 'Ski lifts',
          layerIds: LIFT_BUILT_LAYER_IDS,
          visible: true,
          section: 'Structures',
        }] : [],
      setCaptureTransient: ({ map }, hidden) => setLiftData(
        map,
        liftsToGeoJSON(liftsRef.current, hidden ? null : draftLineOf(stateRef.current)),
      ),
      cleanup: () => {},
    };
  }

  useEffect(() => {
    optionsRef.current.synchronizeMap();
  }, [state, options.lifts]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (state.phase !== 'armed' && state.phase !== 'anchored')) return;
    const interaction = optionsRef.current.acquireInteractions(map);
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = stateRef.current;
      if (current.phase === 'armed') {
        dispatch({ type: 'anchor', point });
        return;
      }
      if (current.phase !== 'anchored' || haversineMeters(current.a, point) < MIN_LIFT_M) return;
      const points: [[number, number], [number, number]] = [current.a, point];
      dispatch({ type: 'review', points, name: nextLiftName([...liftsRef.current]) });
      sampleElevations(points);
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      if (stateRef.current.phase !== 'anchored') return;
      dispatch({ type: 'move', point: [event.lngLat.lng, event.lngLat.lat] });
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancel();
    };
    map.on('click', onClick);
    map.on('mousemove', onMove);
    window.addEventListener('keydown', onKey);
    return () => {
      map.off('click', onClick);
      map.off('mousemove', onMove);
      window.removeEventListener('keydown', onKey);
      interaction.release();
    };
  }, [state.phase]);

  useEffect(() => () => {
    sampleTokenRef.current += 1;
    optionsRef.current.release();
  }, []);

  function sampleElevations(points: [[number, number], [number, number]]): void {
    const map = optionsRef.current.mapRef.current;
    const zoom = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++sampleTokenRef.current;
    dispatch({ type: 'sample-started' });
    void Promise.all(points.map(([lng, lat]) =>
      optionsRef.current.sampleTerrain(lng, lat, zoom))).then(
      ([first, second]) => {
        if (token !== sampleTokenRef.current) return;
        if (!first || !second) {
          dispatch({ type: 'sample-failed' });
          return;
        }
        dispatch({ type: 'sample-succeeded', elevations: [first.elevation, second.elevation] });
      },
      () => {
        if (token === sampleTokenRef.current) dispatch({ type: 'sample-failed' });
      },
    );
  }

  function arm(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate()) return;
    optionsRef.current.clearSelection();
    dispatch({ type: 'arm' });
  }

  function cancel(): void {
    sampleTokenRef.current += 1;
    dispatch({ type: 'cancel' });
    optionsRef.current.release();
  }

  function dispose(): void {
    sampleTokenRef.current += 1;
    optionsRef.current.release();
  }

  function patchDraft(patch: Partial<DraftLift>): void {
    dispatch({ type: 'patch', patch });
  }

  function retryElevation(): void {
    const current = stateRef.current;
    if (current.phase === 'review') sampleElevations(current.draft.points);
  }

  async function confirm(): Promise<void> {
    const current = stateRef.current;
    if (current.phase !== 'review') return;
    const lift = liftFromDraft(
      current.draft,
      liftsRef.current,
      optionsRef.current.createId(),
      optionsRef.current.now(),
    );
    await optionsRef.current.runConstruction(async () => {
      sampleTokenRef.current += 1;
      optionsRef.current.commands.add(lift);
      try {
        await new Promise(requestAnimationFrame);
        await optionsRef.current.clearCover(lift);
      } finally {
        dispatch({ type: 'cancel' });
        optionsRef.current.release();
      }
    });
  }

  function select(id: string): void {
    sampleTokenRef.current += 1;
    optionsRef.current.select(id);
  }

  function patch(id: string, patchValue: Partial<SavedLift>): void {
    optionsRef.current.commands.patch(id, patchValue);
  }

  function remove(id: string): void {
    optionsRef.current.commands.remove(id);
    optionsRef.current.clearSelected(id);
  }

  return {
    state,
    contribution: contributionRef.current,
    arm,
    cancel,
    dispose,
    patchDraft,
    retryElevation,
    confirm,
    select,
    patch,
    remove,
  };
}
