import { useCallback, useEffect, useRef, useState, type MutableRefObject, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import type { SkiNetwork } from '../network';
import { snowmakingPipeSegments, snowmakingPipeStats } from '../snowmakingNetwork';
import type { SavedDam, SavedLift, SavedPond, SavedSnowmakingNode, SavedTrail,
  TerrainRecord } from '../types';
import type { SavedBuilding } from '../types/buildings';
import type { SavedSnowgun, SavedSnowmakingPipe, SnowmakingLakeSource } from '../types/snowmaking';
import { applyDashboardGunLassoState, applyDashboardMapPresentation, dashboardBounds,
  setDashboardLassoData, setDashboardMapData, type DashboardMapData,
  type SnowmakingMapPresentation } from './dashboardMapLayers';
import type { DashboardKind, SnowgunSelectionPhase, SnowmakingDashboardMode } from './dashboardMode';
import type { MapContributionRegistry } from './mapContribution';
import type { MapHitHoverTarget } from './mapContribution';
import type { Units } from './SettingsContext';
import type { SnowmakingPipeHoverState } from './SnowmakingPipeHover';
import { appendLassoSample, closeLassoPath, connectedGunIdsInLasso,
  type LassoPoint, type SnowmakingLassoSelection, type SnowmakingLassoMapState } from './snowmakingLasso';

export interface InMapDashboardInput {
  mapRef: RefObject<maplibregl.Map | null>;
  registryRef: MutableRefObject<MapContributionRegistry | null>;
  dark: boolean;
  units: Units;
  network: SkiNetwork;
  dams: readonly SavedDam[];
  ponds: readonly SavedPond[];
  lakes: readonly SnowmakingLakeSource[];
  trails: readonly SavedTrail[];
  lifts: readonly SavedLift[];
  nodes: readonly SavedSnowmakingNode[];
  buildings: readonly SavedBuilding[];
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  coverDisplay: CoverDisplayGeoJSON | null;
  terrainRecord: TerrainRecord | null;
}

function samePresentationSource(
  left: SnowmakingMapPresentation | null,
  right: SnowmakingMapPresentation | null,
): boolean {
  if (!left || !right) return left === right;
  if (left.mode !== right.mode || left.showGunTypes !== right.showGunTypes ||
    left.pressureRange?.minPsi !== right.pressureRange?.minPsi ||
    left.pressureRange?.maxPsi !== right.pressureRange?.maxPsi ||
    left.invalidPumpIds.size !== right.invalidPumpIds.size ||
    left.segments.length !== right.segments.length) return false;
  for (const id of left.invalidPumpIds) if (!right.invalidPumpIds.has(id)) return false;
  return left.segments.every((segment, index) => {
    const other = right.segments[index];
    return segment.id === other?.id && segment.flowGpm === other.flowGpm &&
      segment.active === other.active && segment.upstreamPressurePsi === other.upstreamPressurePsi &&
      segment.downstreamPressurePsi === other.downstreamPressurePsi;
  });
}

export function useInMapDashboards(input: InMapDashboardInput) {
  const [active, setActive] = useState<DashboardKind | null>(null);
  const [liftId, setLiftId] = useState<string | null>(null);
  const [edgeId, setEdgeId] = useState<string | null>(null);
  const [snowSelection, setSnowSelection] = useState<
    { kind: 'node' | 'gun'; id: string } |
    { kind: 'pipe'; id: string; segmentId: string | null } | null>(null);
  const [snowMode, setSnowMode] = useState<SnowmakingDashboardMode>('inspect');
  const [snowGunSelectionPhase, setSnowGunSelectionPhase] =
    useState<SnowgunSelectionPhase>('idle');
  const [snowHover, setSnowHover] = useState<SnowmakingPipeHoverState | null>(null);
  const [snowLasso, setSnowLasso] = useState<SnowmakingLassoSelection | null>(null);
  const activeRef = useRef(active), networkRef = useRef(input.network);
  const presentationRef = useRef<SnowmakingMapPresentation | null>(null);
  const pipesRef = useRef(input.pipes);
  const suppressSnowClickRef = useRef(false);
  const lassoIdsRef = useRef<string[]>([]);
  const lassoMapRef = useRef<SnowmakingLassoMapState | null>(null);
  const snowGunSelectionPhaseRef = useRef(snowGunSelectionPhase);
  activeRef.current = active;
  networkRef.current = input.network;
  pipesRef.current = input.pipes;
  snowGunSelectionPhaseRef.current = snowGunSelectionPhase;
  const snowGunSelectionActive = snowGunSelectionPhase !== 'idle';

  const data: DashboardMapData = {
    kind: active, dark: input.dark, units: input.units, network: input.network,
    selectedLiftId: liftId, selectedEdgeId: edgeId, dams: input.dams, ponds: input.ponds,
    lakes: input.lakes, trails: input.trails, lifts: input.lifts, nodes: input.nodes,
    buildings: input.buildings,
    pipes: input.pipes, guns: input.guns, coverDisplay: input.coverDisplay,
    terrainRecord: input.terrainRecord, selectedSnowmaking: snowSelection,
    snowmakingPresentation: presentationRef.current,
    snowmakingLasso: lassoMapRef.current,
  };
  const dataRef = useRef(data);
  dataRef.current = data;
  const syncRef = useRef((map: maplibregl.Map | null) => {
    dataRef.current = { ...dataRef.current,
      snowmakingPresentation: presentationRef.current,
      snowmakingLasso: lassoMapRef.current };
    setDashboardMapData(map, dataRef.current);
  });
  const setSnowPresentation = useCallback((next: SnowmakingMapPresentation) => {
    const previous = presentationRef.current;
    presentationRef.current = next;
    dataRef.current = { ...dataRef.current, snowmakingPresentation: next };
    const map = input.mapRef.current;
    if (!samePresentationSource(previous, next)) setDashboardMapData(map, dataRef.current);
    applyDashboardMapPresentation(map, next, previous, input.guns.map((gun) => gun.id));
  }, [input.guns, input.mapRef]);

  type LassoGesture = { start: LassoPoint; latest: LassoPoint; path: LassoPoint[];
    moved: boolean; restored: boolean; priorDragPan: boolean; frame: number | null;
    previewIds: string[]; projectedGuns: ReadonlyMap<string, LassoPoint> };

  const cancelSnowGunSelection = useCallback(() => setSnowGunSelectionPhase('idle'), []);
  const toggleSnowGunSelection = useCallback(() => setSnowGunSelectionPhase((phase) =>
    phase === 'idle' ? 'armed' : 'idle'), []);

  useEffect(() => {
    if (active !== 'snowmaking') {
      presentationRef.current?.setHoveredSegment(null);
      setSnowMode('inspect');
      setSnowGunSelectionPhase('idle');
      setSnowHover(null);
      setSnowLasso(null);
      suppressSnowClickRef.current = false;
    } else if (snowMode !== 'analysis') {
      setSnowGunSelectionPhase('idle');
    }
  }, [active, snowMode]);

  useEffect(() => {
    if (!snowGunSelectionActive || active !== 'snowmaking' || snowMode !== 'analysis') return;
    const map = input.mapRef.current;
    if (!map) return;
    const canvas = map.getCanvas();
    let currentGesture: LassoGesture | null = null;
    const restore = (target = currentGesture) => {
      if (!target || target.restored) return;
      target.restored = true;
      if (target.priorDragPan) map.dragPan.enable();
      else map.dragPan.disable();
    };
    const screenPoint = (clientX: number, clientY: number): LassoPoint => {
      const bounds = canvas.getBoundingClientRect();
      return { x: clientX - bounds.left, y: clientY - bounds.top };
    };
    const mapRing = (ring: readonly LassoPoint[]): [number, number][] =>
      ring.map((point) => {
        const lngLat = map.unproject([point.x, point.y]);
        return [lngLat.lng, lngLat.lat];
      });
    const anchor = (point: LassoPoint): LassoPoint => {
      const bounds = canvas.getBoundingClientRect();
      const clientX = bounds.left + point.x, clientY = bounds.top + point.y;
      return { x: Math.min(Math.max(8, clientX), Math.max(8, window.innerWidth - 190)),
        y: Math.min(Math.max(8, clientY), Math.max(8, window.innerHeight - 86)) };
    };
    const anchorFromLngLat = (point: [number, number]): LassoPoint => {
      const projected = map.project(point);
      return anchor({ x: projected.x, y: projected.y });
    };
    const clearLasso = () => {
      const previous = currentGesture?.previewIds ?? lassoIdsRef.current;
      applyDashboardGunLassoState(map, [], previous);
      setDashboardLassoData(map, null);
      lassoIdsRef.current = [];
      lassoMapRef.current = null;
      suppressSnowClickRef.current = false;
      setSnowLasso(null);
    };
    const publishPreview = (gesture: LassoGesture, ring: readonly LassoPoint[]) => {
      if (ring.length < 3) return [];
      const gunIds = connectedGunIdsInLasso(input.guns, gesture.projectedGuns, ring);
      applyDashboardGunLassoState(map, gunIds, gesture.previewIds);
      gesture.previewIds = gunIds;
      const state: SnowmakingLassoMapState = { ring: mapRing(ring), gunIds };
      lassoIdsRef.current = gunIds;
      lassoMapRef.current = state;
      setDashboardLassoData(map, state);
      return gunIds;
    };
    const processGesture = (gesture: LassoGesture, final: boolean) => {
      gesture.path = appendLassoSample(gesture.path, gesture.latest);
      if (!gesture.moved && Math.hypot(gesture.latest.x - gesture.start.x,
        gesture.latest.y - gesture.start.y) < 4) return;
      gesture.moved = true;
      const ring = final ? closeLassoPath(gesture.path)
        : gesture.path.length >= 3 ? [...gesture.path, gesture.path[0]] : [];
      const gunIds = publishPreview(gesture, ring);
      if (!final || ring.length < 4) return;
      const selected = new Set(presentationRef.current?.selectedGunIds ?? []);
      const release = map.unproject([gesture.latest.x, gesture.latest.y]);
      const anchorLngLat: [number, number] = [release.lng, release.lat];
      const finalState: SnowmakingLassoSelection = {
        ring: mapRing(ring), gunIds, anchor: anchorFromLngLat(anchorLngLat), anchorLngLat,
        selectedGunCount: gunIds.filter((id) => selected.has(id)).length,
        unselectedGunCount: gunIds.filter((id) => !selected.has(id)).length,
        add: () => {
          const presentation = presentationRef.current;
          if (presentation) presentation.setGuns([...new Set([
            ...presentation.selectedGunIds, ...gunIds,
          ])]);
          clearLasso();
          setSnowGunSelectionPhase('idle');
        },
        remove: () => {
          const presentation = presentationRef.current;
          if (presentation) {
            const enclosed = new Set(gunIds);
            presentation.setGuns([...presentation.selectedGunIds].filter((id) => !enclosed.has(id)));
          }
          clearLasso();
          setSnowGunSelectionPhase('idle');
        },
        cancel: () => { clearLasso(); setSnowGunSelectionPhase('idle'); },
      };
      setSnowLasso(finalState);
      setSnowGunSelectionPhase('review');
    };
    const onMouseDown = (event: maplibregl.MapMouseEvent) => {
      if (snowGunSelectionPhaseRef.current !== 'armed' || event.originalEvent.button !== 0) return;
      clearLasso();
      const projectedGuns = new Map(input.guns.filter((gun) => gun.hydrantId != null)
        .map((gun) => {
          const point = map.project(gun.point);
          return [gun.id, { x: point.x, y: point.y }] as const;
        }));
      currentGesture = { start: { x: event.point.x, y: event.point.y },
        latest: { x: event.point.x, y: event.point.y },
        path: [{ x: event.point.x, y: event.point.y }], moved: false,
        restored: false, priorDragPan: map.dragPan.isEnabled(), frame: null, previewIds: [],
        projectedGuns };
      map.dragPan.disable();
    };
    const onMouseMove = (event: maplibregl.MapMouseEvent) => {
      if (!currentGesture) return;
      currentGesture.latest = { x: event.point.x, y: event.point.y };
      if (currentGesture.frame == null) currentGesture.frame = requestAnimationFrame(() => {
        if (!currentGesture) return;
        currentGesture.frame = null;
        processGesture(currentGesture, false);
      });
    };
    const finish = (point: LassoPoint) => {
      if (!currentGesture) return;
      const finished = currentGesture;
      finished.latest = point;
      if (finished.frame != null) cancelAnimationFrame(finished.frame);
      finished.frame = null;
      processGesture(finished, true);
      restore(finished);
      currentGesture = null;
      if (!finished.moved) return;
      suppressSnowClickRef.current = true;
    };
    const onMouseUp = (event: maplibregl.MapMouseEvent) => finish({ x: event.point.x, y: event.point.y });
    const onWindowMouseUp = (event: MouseEvent) => finish(screenPoint(event.clientX, event.clientY));
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      if (currentGesture?.frame != null) cancelAnimationFrame(currentGesture.frame);
      restore(currentGesture);
      currentGesture = null;
      clearLasso();
      suppressSnowClickRef.current = false;
      setSnowGunSelectionPhase('idle');
    };
    const onMoveStart = () => {
      if (!currentGesture) return;
      if (currentGesture.frame != null) cancelAnimationFrame(currentGesture.frame);
      restore(currentGesture);
      currentGesture = null;
      clearLasso();
      suppressSnowClickRef.current = false;
    };
    const onMove = () => {
      if (snowGunSelectionPhaseRef.current !== 'review') return;
      setSnowLasso((selection) => selection ? { ...selection,
        anchor: anchorFromLngLat(selection.anchorLngLat) } : null);
    };
    map.on('mousedown', onMouseDown);
    map.on('mousemove', onMouseMove);
    map.on('mouseup', onMouseUp);
    map.on('movestart', onMoveStart);
    map.on('move', onMove);
    window.addEventListener('mouseup', onWindowMouseUp);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      if (currentGesture?.frame != null) cancelAnimationFrame(currentGesture.frame);
      restore(currentGesture);
      currentGesture = null;
      map.off('mousedown', onMouseDown);
      map.off('mousemove', onMouseMove);
      map.off('mouseup', onMouseUp);
      map.off('movestart', onMoveStart);
      map.off('move', onMove);
      window.removeEventListener('mouseup', onWindowMouseUp);
      window.removeEventListener('keydown', onKeyDown);
      clearLasso();
      suppressSnowClickRef.current = false;
    };
  }, [snowGunSelectionActive, active, snowMode, input.mapRef, input.guns]);
  useEffect(() => {
    const presentation = active === 'trails' ? 'dashboard-trails'
      : active === 'snowmaking' && snowGunSelectionActive ? 'dashboard-snowmaking-select'
        : active === 'snowmaking' ? snowMode === 'analysis'
        ? 'dashboard-snowmaking-analysis' : 'dashboard-snowmaking' : null;
    input.registryRef.current?.setPresentation(presentation);
  }, [active, snowMode, snowGunSelectionActive, input.registryRef]);
  useEffect(() => {
    syncRef.current(input.mapRef.current);
  }, [active, snowMode, liftId, edgeId, snowSelection,
    input.dark, input.units, input.network, input.dams, input.ponds, input.lakes,
    input.trails, input.lifts, input.nodes, input.buildings, input.pipes, input.guns,
    input.coverDisplay, input.terrainRecord, input.mapRef, input.registryRef]);

  const clear = () => {
    presentationRef.current?.setHoveredSegment(null);
    setLiftId(null); setEdgeId(null); setSnowSelection(null); setSnowHover(null);
  };
  const change = (kind: DashboardKind | null) => {
    setSnowGunSelectionPhase('idle'); setActive(kind); clear();
    if (kind === 'snowmaking') setSnowMode('inspect');
  };
  const close = () => {
    setSnowGunSelectionPhase('idle'); setActive(null); setSnowMode('inspect'); clear();
  };
  const fit = () => {
    const bounds = dashboardBounds(dataRef.current);
    if (bounds) input.mapRef.current?.fitBounds(bounds, { padding: 64, duration: 500 });
  };
  const selectLift = (id: string): boolean => {
    if (activeRef.current !== 'trails') return false;
    setLiftId(id); setEdgeId(networkRef.current.liftEdgeIds.get(id) ?? null); return true;
  };
  const selectEdge = (id: string): boolean => {
    if (activeRef.current !== 'trails' || !networkRef.current.edgeById.has(id)) return false;
    setLiftId(null); setEdgeId(id); return true;
  };
  const selectSnow = (kind: 'node' | 'pipe' | 'gun', id: string,
    segmentId?: string): boolean => {
    if (activeRef.current !== 'snowmaking') return false;
    if (snowMode === 'analysis' && kind === 'gun' && suppressSnowClickRef.current) {
      suppressSnowClickRef.current = false;
    } else if (snowMode === 'analysis' && kind === 'gun') {
      if (snowGunSelectionPhaseRef.current === 'armed' &&
        input.guns.some((gun) => gun.id === id && gun.hydrantId != null)) {
        presentationRef.current?.toggleGun(id);
        setSnowGunSelectionPhase('idle');
      }
    }
    else setSnowSelection(kind === 'pipe'
      ? { kind, id, segmentId: segmentId ?? null } : { kind, id });
    return true;
  };
  const hoverSnowPipe = (target: MapHitHoverTarget | null): void => {
    if (activeRef.current !== 'snowmaking' || !target ||
      typeof target.properties.segmentId !== 'string') {
      presentationRef.current?.setHoveredSegment(null);
      setSnowHover(null);
      return;
    }
    const segmentId = target.properties.segmentId;
    const pipe = pipesRef.current.find((candidate) => candidate.id === target.featureId);
    const segment = pipe ? snowmakingPipeSegments(pipe)
      .find((candidate) => candidate.id === segmentId) : null;
    if (!pipe || !segment) {
      presentationRef.current?.setHoveredSegment(null);
      setSnowHover(null);
      return;
    }
    presentationRef.current?.setHoveredSegment(segmentId);
    const analysis = presentationRef.current?.segments.find((candidate) =>
      candidate.id === segmentId) ?? null;
    const flowFrom = target.properties.flowFrom, flowTo = target.properties.flowTo;
    setSnowHover({ pipe, segmentId, segmentIndex: segment.segmentIndex,
      segmentStats: snowmakingPipeStats(segment.vertices),
      point: target.point, analysis, direction: analysis && typeof flowFrom === 'string' &&
        typeof flowTo === 'string' ? { from: flowFrom, to: flowTo } : null });
  };
  return { active, setActive, activeRef, liftId, edgeId, snowSelection, snowMode,
    snowHover, snowLasso, snowGunSelectionPhase, setSnowMode, setSnowPresentation,
    toggleSnowGunSelection, cancelSnowGunSelection, sync: syncRef.current, change, close, fit,
    selectLift, selectEdge, selectSnow, setLiftId, setEdgeId, setSnowSelection,
    hoverSnowPipe,
    openAnalysis: () => {
      setSnowGunSelectionPhase('idle'); setSnowMode('analysis'); setActive('snowmaking');
    } };
}
