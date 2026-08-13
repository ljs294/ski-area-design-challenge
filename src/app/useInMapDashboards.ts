import { useEffect, useRef, useState, type MutableRefObject, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import type { SkiNetwork } from '../network';
import { snowmakingPipeSegments, snowmakingPipeStats } from '../snowmakingNetwork';
import type { SavedDam, SavedLift, SavedPond, SavedSnowmakingNode, SavedTrail,
  TerrainRecord } from '../types';
import type { SavedSnowgun, SavedSnowmakingPipe, SnowmakingLakeSource } from '../types/snowmaking';
import { dashboardBounds, setDashboardMapData, type DashboardMapData,
  type SnowmakingMapPresentation } from './dashboardMapLayers';
import type { DashboardKind, SnowmakingDashboardMode } from './dashboardMode';
import type { MapContributionRegistry } from './mapContribution';
import type { MapHitHoverTarget } from './mapContribution';
import type { Units } from './SettingsContext';
import type { SnowmakingPipeHoverState } from './SnowmakingPipeHover';
import { connectedGunIdsInLasso, normalizeLassoRect,
  type LassoPoint, type SnowmakingLassoSelection } from './snowmakingLasso';

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
  pipes: readonly SavedSnowmakingPipe[];
  guns: readonly SavedSnowgun[];
  coverDisplay: CoverDisplayGeoJSON | null;
  terrainRecord: TerrainRecord | null;
}

export function useInMapDashboards(input: InMapDashboardInput) {
  const [active, setActive] = useState<DashboardKind | null>(null);
  const [liftId, setLiftId] = useState<string | null>(null);
  const [edgeId, setEdgeId] = useState<string | null>(null);
  const [snowSelection, setSnowSelection] = useState<
    { kind: 'node' | 'gun'; id: string } |
    { kind: 'pipe'; id: string; segmentId: string | null } | null>(null);
  const [snowMode, setSnowMode] = useState<SnowmakingDashboardMode>('inspect');
  const [snowPresentation, setSnowPresentation] =
    useState<SnowmakingMapPresentation | null>(null);
  const [snowHover, setSnowHover] = useState<SnowmakingPipeHoverState | null>(null);
  const [snowLasso, setSnowLasso] = useState<SnowmakingLassoSelection | null>(null);
  const activeRef = useRef(active), networkRef = useRef(input.network);
  const presentationRef = useRef(snowPresentation);
  const pipesRef = useRef(input.pipes);
  const suppressSnowClickRef = useRef(false);
  activeRef.current = active;
  networkRef.current = input.network;
  presentationRef.current = snowPresentation;
  pipesRef.current = input.pipes;

  const data: DashboardMapData = {
    kind: active, dark: input.dark, units: input.units, network: input.network,
    selectedLiftId: liftId, selectedEdgeId: edgeId, dams: input.dams, ponds: input.ponds,
    lakes: input.lakes, trails: input.trails, lifts: input.lifts, nodes: input.nodes,
    pipes: input.pipes, guns: input.guns, coverDisplay: input.coverDisplay,
    terrainRecord: input.terrainRecord, selectedSnowmaking: snowSelection,
    snowmakingPresentation: snowPresentation,
    snowmakingLasso: snowLasso,
  };
  const dataRef = useRef(data);
  dataRef.current = data;
  const syncRef = useRef((map: maplibregl.Map | null) =>
    setDashboardMapData(map, dataRef.current));

  type LassoGesture = { start: LassoPoint; current: LassoPoint; moved: boolean;
    restored: boolean; priorDragPan: boolean };

  useEffect(() => {
    if (active !== 'snowmaking') {
      presentationRef.current?.setHoveredSegment(null);
      setSnowMode('inspect');
      setSnowHover(null);
      setSnowLasso(null);
      suppressSnowClickRef.current = false;
    }
  }, [active]);

  useEffect(() => {
    if (active !== 'snowmaking' || snowMode !== 'analysis') {
      setSnowLasso(null);
      suppressSnowClickRef.current = false;
      return;
    }
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
    const geoBounds = (rect: ReturnType<typeof normalizeLassoRect>) => {
      const sw = map.unproject([rect.minX, rect.maxY]);
      const ne = map.unproject([rect.maxX, rect.minY]);
      return [sw.lng, sw.lat, ne.lng, ne.lat] as const;
    };
    const updatePreview = (end: LassoPoint) => {
      if (!currentGesture) return;
      currentGesture.current = end;
      const dx = end.x - currentGesture.start.x, dy = end.y - currentGesture.start.y;
      if (!currentGesture.moved && Math.hypot(dx, dy) < 4) return;
      currentGesture.moved = true;
      const rect = normalizeLassoRect(currentGesture.start, end);
      const gunIds = connectedGunIdsInLasso(input.guns,
        (point) => map.project(point), rect);
      const selected = new Set(presentationRef.current?.selectedGunIds ?? []);
      const canvasBounds = canvas.getBoundingClientRect();
      const clientX = canvasBounds.left + end.x, clientY = canvasBounds.top + end.y;
      const maxAnchorX = Math.max(8, window.innerWidth - 190);
      const maxAnchorY = Math.max(8, window.innerHeight - 86);
      setSnowLasso({ rect, gunIds, geoBounds: geoBounds(rect),
        anchor: { x: Math.min(Math.max(8, clientX), maxAnchorX),
          y: Math.min(Math.max(8, clientY), maxAnchorY) },
        selectedGunCount: gunIds.filter((id) => selected.has(id)).length,
        unselectedGunCount: gunIds.filter((id) => !selected.has(id)).length,
        add: () => {
          const presentation = presentationRef.current;
          if (presentation) presentation.setGuns([...new Set([
            ...presentation.selectedGunIds, ...gunIds,
          ])]);
          setSnowLasso(null);
        },
        remove: () => {
          const presentation = presentationRef.current;
          if (presentation) {
            const enclosed = new Set(gunIds);
            presentation.setGuns([...presentation.selectedGunIds].filter((id) => !enclosed.has(id)));
          }
          setSnowLasso(null);
        },
        cancel: () => setSnowLasso(null),
      });
    };
    const onMouseDown = (event: maplibregl.MapMouseEvent) => {
      if (event.originalEvent.button !== 0) return;
      currentGesture = { start: { x: event.point.x, y: event.point.y },
        current: { x: event.point.x, y: event.point.y }, moved: false,
        restored: false, priorDragPan: map.dragPan.isEnabled() };
      map.dragPan.disable();
      setSnowLasso(null);
    };
    const onMouseMove = (event: maplibregl.MapMouseEvent) => {
      if (currentGesture) updatePreview({ x: event.point.x, y: event.point.y });
    };
    const finish = (point: LassoPoint) => {
      if (!currentGesture) return;
      updatePreview(point);
      const finished = currentGesture;
      restore(finished);
      currentGesture = null;
      if (!finished.moved) return;
      suppressSnowClickRef.current = true;
    };
    const onMouseUp = (event: maplibregl.MapMouseEvent) => finish({ x: event.point.x, y: event.point.y });
    const onWindowMouseUp = (event: MouseEvent) => finish(screenPoint(event.clientX, event.clientY));
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      restore(currentGesture);
      currentGesture = null;
      setSnowLasso(null);
      suppressSnowClickRef.current = false;
    };
    map.on('mousedown', onMouseDown);
    map.on('mousemove', onMouseMove);
    map.on('mouseup', onMouseUp);
    window.addEventListener('mouseup', onWindowMouseUp);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      restore(currentGesture);
      currentGesture = null;
      map.off('mousedown', onMouseDown);
      map.off('mousemove', onMouseMove);
      map.off('mouseup', onMouseUp);
      window.removeEventListener('mouseup', onWindowMouseUp);
      window.removeEventListener('keydown', onKeyDown);
      setSnowLasso(null);
      suppressSnowClickRef.current = false;
    };
  }, [active, snowMode, input.mapRef, input.guns]);
  useEffect(() => {
    const presentation = active === 'trails' ? 'dashboard-trails'
      : active === 'snowmaking' ? snowMode === 'analysis'
        ? 'dashboard-snowmaking-analysis' : 'dashboard-snowmaking' : null;
    input.registryRef.current?.setPresentation(presentation);
    syncRef.current(input.mapRef.current);
  });

  const clear = () => {
    presentationRef.current?.setHoveredSegment(null);
    setLiftId(null); setEdgeId(null); setSnowSelection(null); setSnowHover(null);
  };
  const change = (kind: DashboardKind | null) => {
    setActive(kind); clear();
    if (kind === 'snowmaking') setSnowMode('inspect');
  };
  const close = () => { setActive(null); setSnowMode('inspect'); clear(); };
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
    } else if (snowMode === 'analysis' && kind === 'gun') presentationRef.current?.toggleGun(id);
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
    snowHover, snowLasso, setSnowMode, setSnowPresentation, sync: syncRef.current, change, close, fit,
    selectLift, selectEdge, selectSnow, setLiftId, setEdgeId, setSnowSelection,
    hoverSnowPipe,
    openAnalysis: () => { setSnowMode('analysis'); setActive('snowmaking'); } };
}
