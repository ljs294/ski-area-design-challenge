import { useEffect, useLayoutEffect, useReducer, useRef, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { LiftTypeId, SavedLift } from '../types/lifts';
import { haversineMeters } from '../geo';
import { nextLiftIdentifier, nextLiftName } from '../lifts';
import { addLiftLayers, setLiftData, setLiftDraftData, liftsToGeoJSON, LIFT_BUILT_LAYER_IDS } from './liftLayers';
import type { DraftLine } from './liftLayers';
import { MAP_HIT_RANK, MAP_Z_ORDER } from './mapContribution';
import type { ManagedMapContribution, MapVisibilityDescriptor } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import {
  IDLE_LIFT_TOOL,
  liftFromDraft,
  reduceLiftTool,
  type DraftLift,
  type CommittedLiftPatch,
  type LiftTool,
} from './liftControllerModel';

const MIN_LIFT_M = 50;

export interface LiftCollectionCommands {
  add(lift: SavedLift): void;
  patch(id: string, patch: CommittedLiftPatch): void;
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
  startPlacement(): void;
  setType(liftTypeId: LiftTypeId): void;
  cancel(): void;
  dispose(): void;
  patchDraft(patch: Partial<DraftLift>): void;
  retryElevation(): void;
  confirm(): Promise<void>;
  select(id: string): void;
  patch(id: string, patch: CommittedLiftPatch): void;
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
  const anchorSampleTokenRef = useRef(0);
  const cursorSampleTokenRef = useRef(0);
  const cursorFrameRef = useRef<number | null>(null);
  const draftFrameRef = useRef<number | null>(null);
  const pendingCursorRef = useRef<[number, number] | null>(null);
  const cancelRef = useRef<() => void>(() => {});
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
        layerIds: ['lift-line-hit', 'lift-terminals', 'dashboard-lift-hit'],
        select: (id) => select(id),
      }],
      install: ({ map }) => addLiftLayers(map),
      synchronizeData: ({ map }) => {
        setLiftData(map, liftsToGeoJSON(liftsRef.current, null));
        setLiftDraftData(map, draftLineOf(stateRef.current));
      },
      visibility: (): MapVisibilityDescriptor[] =>
        optionsRef.current.structuresVisible() ? [{
          id: 'lifts',
          label: 'Ski lifts',
          layerIds: LIFT_BUILT_LAYER_IDS,
          visible: true,
          section: 'Structures',
        }] : [],
      setCaptureTransient: ({ map }, hidden) =>
        setLiftDraftData(map, hidden ? null : draftLineOf(stateRef.current)),
      cleanup: () => {},
    };
  }

  useEffect(() => { optionsRef.current.synchronizeMap(); }, [options.lifts]);

  useEffect(() => {
    if (draftFrameRef.current != null) return;
    draftFrameRef.current = requestAnimationFrame(() => {
      draftFrameRef.current = null;
      const map = optionsRef.current.mapRef.current;
      if (map) setLiftDraftData(map, draftLineOf(stateRef.current));
    });
  }, [state]);

  useLayoutEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (state.phase !== 'armed' && state.phase !== 'anchored')) return;
    const interaction = optionsRef.current.acquireInteractions(map);
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      const current = stateRef.current;
      if (current.phase === 'armed') {
        dispatch({ type: 'anchor', point });
        sampleLiveAnchor(point);
        return;
      }
      if (current.phase !== 'anchored' || haversineMeters(current.a, point) < MIN_LIFT_M) return;
      const points: [[number, number], [number, number]] = [current.a, point];
      cancelLiveSamples();
      dispatch({
        type: 'review',
        points,
        identifier: nextLiftIdentifier(liftsRef.current),
        name: nextLiftName(liftsRef.current),
      });
      sampleElevations(points);
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      if (stateRef.current.phase !== 'anchored') return;
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      dispatch({ type: 'move', point });
      scheduleCursorSample(point);
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancelRef.current();
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
    cancelLiveSamples();
    if (draftFrameRef.current != null) cancelAnimationFrame(draftFrameRef.current);
    optionsRef.current.release();
  }, []);

  function cancelLiveSamples(): void {
    anchorSampleTokenRef.current += 1;
    cursorSampleTokenRef.current += 1;
    pendingCursorRef.current = null;
    if (cursorFrameRef.current != null) {
      cancelAnimationFrame(cursorFrameRef.current);
      cursorFrameRef.current = null;
    }
  }

  function sampleLiveAnchor(point: [number, number]): void {
    const map = optionsRef.current.mapRef.current;
    const zoom = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
    const token = ++anchorSampleTokenRef.current;
    void optionsRef.current.sampleTerrain(point[0], point[1], zoom).then(
      (sample) => {
        if (token !== anchorSampleTokenRef.current) return;
        if (sample) dispatch({ type: 'anchor-sample-succeeded', elevation: sample.elevation });
        else dispatch({ type: 'anchor-sample-failed' });
      },
      () => {
        if (token === anchorSampleTokenRef.current) dispatch({ type: 'anchor-sample-failed' });
      },
    );
  }

  function scheduleCursorSample(point: [number, number]): void {
    pendingCursorRef.current = point;
    // Invalidate an in-flight response immediately; waiting until the next
    // animation frame would let the old elevation briefly attach to this point.
    cursorSampleTokenRef.current += 1;
    if (cursorFrameRef.current != null) return;
    cursorFrameRef.current = requestAnimationFrame(() => {
      cursorFrameRef.current = null;
      const pending = pendingCursorRef.current;
      pendingCursorRef.current = null;
      if (!pending) return;
      const map = optionsRef.current.mapRef.current;
      const zoom = map ? Math.min(14, Math.max(10, Math.round(map.getZoom()))) : 13;
      const token = cursorSampleTokenRef.current;
      void optionsRef.current.sampleTerrain(pending[0], pending[1], zoom).then(
        (sample) => {
          if (token !== cursorSampleTokenRef.current) return;
          if (sample) dispatch({ type: 'cursor-sample-succeeded', elevation: sample.elevation });
          else dispatch({ type: 'cursor-sample-failed' });
        },
        () => {
          if (token === cursorSampleTokenRef.current) dispatch({ type: 'cursor-sample-failed' });
        },
      );
    });
  }

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
    dispatch({ type: 'open' });
  }

  function startPlacement(): void {
    dispatch({ type: 'start' });
  }

  function setType(liftTypeId: LiftTypeId): void {
    dispatch({ type: 'set-type', liftTypeId });
  }

  function cancel(): void {
    sampleTokenRef.current += 1;
    cancelLiveSamples();
    dispatch({ type: 'cancel' });
    optionsRef.current.release();
  }

  function dispose(): void {
    sampleTokenRef.current += 1;
    cancelLiveSamples();
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
      cancelLiveSamples();
      optionsRef.current.commands.add(lift);
      dispatch({ type: 'cancel' });
      optionsRef.current.release();
      select(lift.id);
      try {
        await new Promise(requestAnimationFrame);
        await optionsRef.current.clearCover(lift);
      } catch { /* Cover is best effort after the committed lift. */ }
    });
  }

  function select(id: string): void {
    sampleTokenRef.current += 1;
    optionsRef.current.select(id);
  }

  function patch(id: string, patchValue: CommittedLiftPatch): void {
    optionsRef.current.commands.patch(id, patchValue);
  }

  function remove(id: string): void {
    optionsRef.current.commands.remove(id);
    optionsRef.current.clearSelected(id);
  }

  cancelRef.current = cancel;

  return {
    state,
    contribution: contributionRef.current,
    arm,
    startPlacement,
    setType,
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
