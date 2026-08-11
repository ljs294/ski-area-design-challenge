import { useEffect, useRef, useState, type MutableRefObject, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { CoverDisplayGeoJSON } from '../coverDisplay';
import type { SkiNetwork } from '../network';
import { snowmakingPipeSegments } from '../snowmakingNetwork';
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
    { kind: 'node' | 'pipe' | 'gun'; id: string } | null>(null);
  const [snowMode, setSnowMode] = useState<SnowmakingDashboardMode>('inspect');
  const [snowPresentation, setSnowPresentation] =
    useState<SnowmakingMapPresentation | null>(null);
  const [snowHover, setSnowHover] = useState<SnowmakingPipeHoverState | null>(null);
  const activeRef = useRef(active), networkRef = useRef(input.network);
  const presentationRef = useRef(snowPresentation);
  const pipesRef = useRef(input.pipes);
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
  };
  const dataRef = useRef(data);
  dataRef.current = data;
  const syncRef = useRef((map: maplibregl.Map | null) =>
    setDashboardMapData(map, dataRef.current));

  useEffect(() => {
    if (active !== 'snowmaking') {
      presentationRef.current?.setHoveredSegment(null);
      setSnowMode('inspect');
      setSnowHover(null);
    }
  }, [active]);
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
  const selectSnow = (kind: 'node' | 'pipe' | 'gun', id: string): boolean => {
    if (activeRef.current !== 'snowmaking') return false;
    if (snowMode === 'analysis' && kind === 'gun') presentationRef.current?.toggleGun(id);
    else setSnowSelection({ kind, id });
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
    setSnowHover({ pipe, segmentId, segmentIndex: segment.segmentIndex,
      point: target.point, analysis });
  };
  return { active, setActive, activeRef, liftId, edgeId, snowSelection, snowMode,
    snowHover, setSnowMode, setSnowPresentation, sync: syncRef.current, change, close, fit,
    selectLift, selectEdge, selectSnow, setLiftId, setEdgeId, setSnowSelection,
    hoverSnowPipe,
    openAnalysis: () => { setSnowMode('analysis'); setActive('snowmaking'); } };
}
