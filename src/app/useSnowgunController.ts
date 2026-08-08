import { useCallback, useEffect, useMemo, useReducer, useRef, useState,
  type MutableRefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import {
  reconcileSnowgunConnections,
  SNOWGUN_VARIANTS,
  snowgunCatalogValue,
  snowgunHydrantDistanceM,
  snowgunVariant,
} from '../snowmakingGuns';
import type { SavedSnowgun, SavedSnowmakingNode, SnowgunVariantId } from '../types/snowmaking';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { setSnowgunDraftData } from './snowmakingLayers';
import { IDLE_SNOWGUN_TOOL, reduceSnowgunTool, type SnowgunDraftPoint,
  type SnowgunMoveCandidate, type SnowgunTool } from './snowmakingGunControllerModel';
import { snowmakingNetworkProjection, type SnowmakingNetworkDocument } from './snowmakingNetworkDocument';
import type { ToolId } from './toolCoordinator';

export interface SnowgunControllerOptions {
  mapRef: MutableRefObject<maplibregl.Map | null>;
  nodes: readonly SavedSnowmakingNode[];
  guns: readonly SavedSnowgun[];
  network: SnowmakingNetworkDocument;
  canArm(): boolean;
  activate(tool: Extract<ToolId, 'snowmaking-gun'>): boolean;
  release(tool: Extract<ToolId, 'snowmaking-gun'>): void;
  openDock(): void;
  clearSelection(): void;
  acquireInteractions(tool: Extract<ToolId, 'snowmaking-gun'>,
    map: maplibregl.Map): MapInteractionLeaseHandle;
  selectGun(id: string): void;
  clearSelected(id: string): void;
  createId(): string;
  now(): string;
  sampleElevation(point: [number, number]): number | null;
}

export interface SnowgunPreviewItem extends SnowgunDraftPoint {
  hydrantId: string | null;
  hydrantLabel: string | null;
  hoseDistanceM: number | null;
}

export interface SnowgunPlanPreview {
  items: SnowgunPreviewItem[];
  candidate: SnowgunPreviewItem | null;
  totalUsd: number;
  connectedCount: number;
  disconnectedCount: number;
}

export interface SnowgunController {
  readonly tool: SnowgunTool;
  readonly preview: SnowgunPlanPreview;
  arm(): void;
  cancel(): void;
  setVariant(variantId: SnowgunVariantId): void;
  removeDraft(draftId: string): void;
  review(): void;
  back(): void;
  confirm(): void;
  armMove(gunId: string): void;
  confirmMove(): void;
  remove(gunId: string): void;
}

function plannedGuns(guns: readonly SavedSnowgun[], items: readonly SnowgunDraftPoint[],
  nodes: readonly SavedSnowmakingNode[]): SavedSnowgun[] {
  const temporary = items.map((item): SavedSnowgun => ({ ...item, id: item.draftId,
    hydrantId: null, createdAt: item.draftId }));
  return reconcileSnowgunConnections([...guns, ...temporary], nodes).slice(guns.length);
}

function previewItem(gun: SavedSnowgun, source: SnowgunDraftPoint,
  nodes: readonly SavedSnowmakingNode[]): SnowgunPreviewItem {
  const hydrant = gun.hydrantId ? nodes.find((node) => node.id === gun.hydrantId) ?? null : null;
  return { ...source, hydrantId: hydrant?.id ?? null,
    hydrantLabel: hydrant?.labelNumber ? `Hydrant ${hydrant.labelNumber}` : hydrant?.name ?? null,
    hoseDistanceM: hydrant ? snowgunHydrantDistanceM(gun, hydrant) : null };
}

export function useSnowgunController(options: SnowgunControllerOptions): SnowgunController {
  const [tool, dispatch] = useReducer(reduceSnowgunTool, IDLE_SNOWGUN_TOOL);
  const [moveHover, setMoveHover] = useState<SnowgunMoveCandidate | null>(null);
  const optionsRef = useRef(options), toolRef = useRef(tool), draftIdRef = useRef(0);
  optionsRef.current = options; toolRef.current = tool;

  const preview = useMemo((): SnowgunPlanPreview => {
    const items = tool.phase === 'placing' || tool.phase === 'review' ? tool.items : [];
    const connected = plannedGuns(options.guns, items, options.nodes);
    const rows = connected.map((gun, index) => previewItem(gun, items[index], options.nodes));
    let candidate: SnowgunPreviewItem | null = null;
    const rawCandidate = tool.phase === 'placing' ? tool.cursor
      : tool.phase === 'moving' ? tool.candidate ?? moveHover : null;
    if (rawCandidate) {
      const variantId = tool.phase === 'placing' ? tool.variantId
        : tool.phase === 'moving' ? options.guns.find((gun) => gun.id === tool.gunId)?.variantId
          : undefined;
      if (variantId) {
        const draft: SnowgunDraftPoint = { draftId: '__candidate__', variantId,
          point: rawCandidate.point, elevM: rawCandidate.elevM };
        const base = tool.phase === 'moving'
          ? options.guns.filter((gun) => gun.id !== tool.gunId) : options.guns;
        const planned = plannedGuns(base, [...items, draft], options.nodes).at(-1)!;
        candidate = previewItem(planned, draft, options.nodes);
      }
    }
    return { items: rows, candidate, totalUsd: snowgunCatalogValue(rows),
      connectedCount: rows.filter((row) => row.hydrantId).length,
      disconnectedCount: rows.filter((row) => !row.hydrantId).length };
  }, [tool, moveHover, options.guns, options.nodes]);

  const synchronizeDraft = useCallback(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map) return;
    const current = toolRef.current;
    const currentPreview = preview;
    setSnowgunDraftData(map, current.phase === 'idle' ? null : {
      guns: currentPreview.items.map((item) => ({ point: item.point,
        hydrantPoint: item.hydrantId ? optionsRef.current.nodes.find((node) =>
          node.id === item.hydrantId)?.point ?? null : null, connected: !!item.hydrantId })),
      candidate: currentPreview.candidate ? { point: currentPreview.candidate.point,
        hydrantPoint: currentPreview.candidate.hydrantId ? optionsRef.current.nodes.find((node) =>
          node.id === currentPreview.candidate!.hydrantId)?.point ?? null : null,
        connected: !!currentPreview.candidate.hydrantId } : null,
    });
  }, [preview]);

  useEffect(() => { synchronizeDraft(); }, [tool, preview, synchronizeDraft]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || tool.phase === 'idle') return;
    map.on('style.load', synchronizeDraft);
    return () => { map.off('style.load', synchronizeDraft); };
  }, [tool.phase, synchronizeDraft]);

  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!map || (tool.phase !== 'placing' && tool.phase !== 'moving')) return;
    const interaction = optionsRef.current.acquireInteractions('snowmaking-gun', map);
    const candidateAt = (event: maplibregl.MapMouseEvent): SnowgunMoveCandidate => {
      const point: [number, number] = [event.lngLat.lng, event.lngLat.lat];
      return { point, elevM: optionsRef.current.sampleElevation(point) };
    };
    const onMove = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event);
      if (toolRef.current.phase === 'placing') dispatch({ type: 'cursor', candidate });
      else if (toolRef.current.phase === 'moving') setMoveHover(candidate);
    };
    const onClick = (event: maplibregl.MapMouseEvent) => {
      const candidate = candidateAt(event), current = toolRef.current;
      if (current.phase === 'placing') dispatch({ type: 'add', item: {
        draftId: `draft-gun-${++draftIdRef.current}`, variantId: current.variantId,
        point: candidate.point, elevM: candidate.elevM,
      } });
      else if (current.phase === 'moving') {
        dispatch({ type: 'move-candidate', candidate }); setMoveHover(null);
      }
    };
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') cancel();
      else if (event.key === 'Backspace' && toolRef.current.phase === 'placing' &&
        !(event.target instanceof HTMLInputElement) && !(event.target instanceof HTMLSelectElement)) {
        event.preventDefault(); dispatch({ type: 'undo' });
      } else if (event.key === 'Enter' && toolRef.current.phase === 'placing') review();
    };
    map.on('mousemove', onMove); map.on('click', onClick); window.addEventListener('keydown', onKey);
    return () => { map.off('mousemove', onMove); map.off('click', onClick);
      window.removeEventListener('keydown', onKey); interaction.release(); setMoveHover(null); };
  }, [tool.phase]);

  useEffect(() => () => optionsRef.current.release('snowmaking-gun'), []);

  function arm(): void {
    if (!optionsRef.current.canArm() || !optionsRef.current.activate('snowmaking-gun')) return;
    optionsRef.current.clearSelection(); optionsRef.current.openDock();
    dispatch({ type: 'arm', variantId: SNOWGUN_VARIANTS[0].id });
  }
  function cancel(): void {
    dispatch({ type: 'cancel' }); setMoveHover(null); setSnowgunDraftData(optionsRef.current.mapRef.current, null);
    optionsRef.current.release('snowmaking-gun');
  }
  function setVariant(variantId: SnowgunVariantId): void { dispatch({ type: 'variant', variantId }); }
  function removeDraft(draftId: string): void { dispatch({ type: 'remove', draftId }); }
  function review(): void { dispatch({ type: 'review', revision: optionsRef.current.network.revision }); }
  function back(): void { dispatch({ type: 'back' }); }
  function confirm(): void {
    const current = toolRef.current;
    if (current.phase !== 'review' || current.items.length === 0) return;
    const document = optionsRef.current.network;
    if (document.revision !== current.revision) {
      dispatch({ type: 'review-error', revision: document.revision,
        error: 'The snowmaking network changed. Hookups were refreshed; confirm again.' }); return;
    }
    const state = snowmakingNetworkProjection(document.snapshot());
    const createdAt = optionsRef.current.now();
    const built = current.items.map((item): SavedSnowgun => ({ id: optionsRef.current.createId(),
      variantId: item.variantId, point: item.point, elevM: item.elevM,
      hydrantId: null, createdAt }));
    const edit = document.begin();
    edit.replace({ ...state, guns: reconcileSnowgunConnections([...state.guns, ...built], state.nodes) });
    const result = edit.commit();
    if (!result.ok) { dispatch({ type: 'review-error', revision: document.revision,
      error: 'The snowmaking network changed. Hookups were refreshed; confirm again.' }); return; }
    cancel();
  }
  function armMove(gunId: string): void {
    const gun = optionsRef.current.guns.find((candidate) => candidate.id === gunId);
    if (!gun || snowgunVariant(gun.variantId).mount !== 'sled' || !optionsRef.current.canArm() ||
      !optionsRef.current.activate('snowmaking-gun')) return;
    optionsRef.current.openDock(); dispatch({ type: 'move', gunId,
      revision: optionsRef.current.network.revision });
  }
  function confirmMove(): void {
    const current = toolRef.current;
    if (current.phase !== 'moving' || !current.candidate) return;
    const document = optionsRef.current.network;
    if (document.revision !== current.revision) {
      dispatch({ type: 'review-error', revision: document.revision,
        error: 'The snowmaking network changed. The hookup was refreshed; confirm again.' });
      return;
    }
    const state = snowmakingNetworkProjection(document.snapshot());
    const gun = state.guns.find((candidate) => candidate.id === current.gunId);
    if (!gun || snowgunVariant(gun.variantId).mount !== 'sled') { cancel(); return; }
    const moved = { ...gun, point: current.candidate.point, elevM: current.candidate.elevM,
      hydrantId: null };
    const edit = document.begin();
    edit.replace({ ...state, guns: reconcileSnowgunConnections(state.guns.map((candidate) =>
      candidate.id === gun.id ? moved : candidate), state.nodes) });
    if (!edit.commit().ok) return;
    cancel(); optionsRef.current.selectGun(gun.id);
  }
  function remove(gunId: string): void {
    const document = optionsRef.current.network;
    const state = snowmakingNetworkProjection(document.snapshot());
    if (!state.guns.some((gun) => gun.id === gunId)) return;
    const edit = document.begin();
    edit.replace({ ...state, guns: reconcileSnowgunConnections(
      state.guns.filter((gun) => gun.id !== gunId), state.nodes) });
    if (edit.commit().ok) optionsRef.current.clearSelected(gunId);
  }

  return { tool, preview, arm, cancel, setVariant, removeDraft, review, back, confirm,
    armMove, confirmMove, remove };
}
