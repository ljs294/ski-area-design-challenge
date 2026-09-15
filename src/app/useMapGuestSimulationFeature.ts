import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SkiNetwork } from '../network';
import { resortRevision } from '../dualClock/revision';
import { defaultDualAmenities } from '../dualClock/amenities';
import type { SimulationClock } from '../types/simulation';
import type { SnowGrid } from '../types/snow';
import type { SavedRoad } from '../types/roads';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { useGuestPortalController } from './useGuestPortalController';
import { useGuestSimulationRuntime } from './useGuestSimulationRuntime';
import { GUEST_LAYER_ID, getGuestRenderedPosition, setGuestCompactFrame, setRepresentativeGuestPresentation, setGuestMotionRoutes } from './guestLayers';
import { analyzeGuestConnectivity } from './guestConnectivity';
import type { SimulationTimeDiscontinuity } from './developerConsoleCommands';
import type { DualSimulationControls } from './useDualClockRuntime';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';
import type { SavedTrail } from '../types/trails';
import { dualGuestPresentation } from './dualGuestPresentation';
import { guestVibePresentation, withGuestEconomyControls } from './guestVibePresentation';
import { canonicalResortSimulationInput } from './resortSimulationInput';
import type { IntegratedBenchmarkScenario } from '../integratedBenchmarkScenario';

/** Owns the gameplay-facing guest state while keeping MapView as composition only. */
export function useMapGuestSimulationFeature(options: {
  readonly mapRef: RefObject<maplibregl.Map | null>;
  readonly network: SkiNetwork;
  readonly clock: SimulationClock;
  readonly saveKey: string | null;
  readonly saveRevision: string | null;
  readonly snowGrid?: SnowGrid | null;
  readonly roads?: readonly SavedRoad[];
  readonly operationsRevision?: number;
  readonly weatherRevision?: number;
  readonly timeDiscontinuity?: SimulationTimeDiscontinuity | null;
  readonly reducedMotion: boolean;
  readonly dual?: DualSimulationControls;
  readonly trails?: readonly SavedTrail[];
  readonly integratedBenchmarkScenario?: IntegratedBenchmarkScenario | null;
  inspectGuest?(id: string): void;
  activate(): boolean;
  release(): void;
  openDock(): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  synchronizeMap(): void;
  onPresentationCommitted?: () => void;
}) {
  const [portal, setPortal] = useState<import('./guestPortalPlacement').PlacedGuestPortal | null>(() => {
    const saved = options.dual?.initialPortal;
    return saved ? { ...saved, version: 1, kind: 'guest-entrance', type: 'guest-entrance', semantics: 'guest-entrance',
      direction: 'inbound', accepts: 'guests', label: 'Guest Entrance', capacityGuestsPerTick: saved.capacityPerMinute / 60,
      openFromTick: 0, openUntilTick: Number.MAX_SAFE_INTEGER } : null;
  });
  const [selectedGuestId, setSelectedGuestId] = useState<string | null>(null);
  const [nextDayTicketPriceCents, setNextDayTicketPriceCents] = useState(options.dual?.initialTicketPriceCents ?? 10_000);
  const [isFollowing, setIsFollowing] = useState(false);
  const followedGuestIdRef = useRef<string | null>(null);
  const followMapRef = useRef<maplibregl.Map | null>(null);
  const followFrameRef = useRef<number | null>(null);
  const followStateRef = useRef(false);
  const stopFollowingRef = useRef<() => void>(() => {});
  const runtimePointsRef = useRef<GuestSimulationRuntime['points']>([]);
  const followedGuestStatusRef = useRef<string | null>(null);
  const demand = useMemo(() => ({ dayType: options.clock.weekday === 0 || options.clock.weekday === 6 ? 'weekend' as const : 'weekday' as const,
    basePotentialGuests: options.clock.weekday === 0 || options.clock.weekday === 6 ? 1_300 : 900,
    ticketPriceCents: nextDayTicketPriceCents, referencePriceCents: 10_000, reputation: 0.6,
    resortValue: 0.5, availableCapacityGuests: 50_000 }), [nextDayTicketPriceCents, options.clock.weekday]);
  const weeklyDailyDemand = useMemo(() => {
    const baseline = options.clock.weekday === 0 || options.clock.weekday === 6 ? 1_300 : 900;
    const scale = demand.basePotentialGuests / baseline;
    return [900, 900, 900, 900, 900, 1_300, 1_300].map((value) => Math.max(0, Math.round(value * scale)));
  }, [demand.basePotentialGuests, options.clock.weekday]);
  const guestMapRef = options.mapRef;
  const onPresentationCommitted = options.onPresentationCommitted;
  const publishRenderFrame = useCallback<NonNullable<Parameters<typeof useGuestSimulationRuntime>[0]['publishRenderFrame']>>(
    (frame, edgePaths, portalLngLat) => { const map = guestMapRef.current; setGuestCompactFrame(map, frame, edgePaths, portalLngLat); if (map?.getLayer(GUEST_LAYER_ID)) onPresentationCommitted?.(); },
    [guestMapRef, onPresentationCommitted]);
  const legacyRuntime = useGuestSimulationRuntime({ enabled: !options.dual,
    saveKey: options.dual ? null : options.saveKey, gameSaveUpdatedAt: options.saveRevision,
    network: options.network, portal: options.dual ? null : portal, clock: options.clock, snowGrid: options.snowGrid, roads: options.roads,
    operationsRevision: options.operationsRevision, weatherRevision: options.weatherRevision,
    demand, weeklyDailyDemand, timeDiscontinuity: options.timeDiscontinuity, restorePortal: setPortal,
    publishRenderFrame });
  const runtime: GuestSimulationRuntime = options.dual ? { ...legacyRuntime,
    status: options.dual.error ? 'error' : options.dual.ready ? 'ready' : 'starting', message: options.dual.error ?? 'Representative guest simulation',
    points: options.dual.publication?.points ?? [], committedSecond: options.dual.publication?.clock.macroSecond ?? 0,
    dualCheckpoint: options.dual.checkpoint } : legacyRuntime;
  runtimePointsRef.current = runtime.points;
  const selectedPublication = options.dual?.publication?.selected;
  const selectedGuestIdForFollow = selectedPublication?.id;
  const selectedGuestStatusForFollow = selectedPublication?.status;
  followedGuestStatusRef.current = selectedPublication?.id === followedGuestIdRef.current
    ? selectedPublication.status
    : runtime.snapshot?.guests.find((guest) => guest.id === followedGuestIdRef.current)?.status ?? null;
  const connectivity = useMemo(() => analyzeGuestConnectivity(options.network, portal, options.roads),
    [options.network, options.roads, portal]);
  const updateDualResort = options.dual?.updateResort, dualReady = options.dual?.ready;
  useEffect(() => {
    if (!dualReady || !updateDualResort) return;
    updateDualResort(canonicalResortSimulationInput({
      revision: resortRevision(options.network.edges, options.trails ?? []),
      edges: options.network.edges, trails: options.trails ?? [],
      portal: portal ? { id: portal.id, nodeId: portal.nodeId, lngLat: portal.lngLat,
        capacityPerMinute: portal.capacityGuestsPerTick * 60 } : null,
      ticketPriceCents: nextDayTicketPriceCents, amenities: defaultDualAmenities(portal?.nodeId ?? null),
    }, options.integratedBenchmarkScenario));
  }, [dualReady, updateDualResort, options.network, options.trails, portal, nextDayTicketPriceCents,
    options.integratedBenchmarkScenario]);
  useEffect(() => {
    const publication = options.dual?.publication;
    if (options.dual) setGuestMotionRoutes(options.mapRef.current, options.dual.geometry);
    setRepresentativeGuestPresentation(options.mapRef.current, !!options.dual,
      publication && publication.clock.speed >= 8 && publication.advance?.state !== 'running' ? publication.flow : null, options.network.edges);
  }, [options.dual, options.mapRef, options.network]);
  const controller = useGuestPortalController({ mapRef: options.mapRef, network: options.network, portal,
    points: runtime.points, reducedMotion: options.reducedMotion, connectivity, setPortal,
    selectGuest: options.inspectGuest, activate: options.activate, release: options.release,
    openDock: options.openDock, acquireInteractions: options.acquireInteractions, synchronizeMap: options.synchronizeMap,
    onPresentationCommitted: options.onPresentationCommitted });
  const scheduleFollowFrame = useCallback(() => {
    if (!followStateRef.current || followFrameRef.current !== null) return;
    const run = () => {
      followFrameRef.current = null;
      if (!followStateRef.current) return;
      const id = followedGuestIdRef.current, map = options.mapRef.current;
      if (!id || !map) { scheduleFollowFrame(); return; }
      if (followedGuestStatusRef.current === 'departed') {
        stopFollowingRef.current();
        return;
      }
      const rendered = getGuestRenderedPosition(map, id);
      const fallback = runtimePointsRef.current.find((guest) => guest.id === id);
      const point = rendered ?? (fallback ? [fallback.lng, fallback.lat] as const : null);
      if (point) map.jumpTo({ center: [point[0], point[1]] });
      scheduleFollowFrame();
    };
    followFrameRef.current = typeof requestAnimationFrame === 'function'
      ? requestAnimationFrame(run) : window.setTimeout(run, 16);
  }, [options.mapRef]);
  const stopFollowingOnUserMove = useCallback(() => {
    stopFollowingRef.current();
  }, []);
  const attachFollowMap = useCallback((map: maplibregl.Map | null) => {
    if (followMapRef.current === map) return;
    followMapRef.current?.off('dragstart', stopFollowingOnUserMove);
    followMapRef.current = map;
    map?.on('dragstart', stopFollowingOnUserMove);
  }, [stopFollowingOnUserMove]);
  const stopFollowing = useCallback(() => {
    followStateRef.current = false;
    followedGuestIdRef.current = null;
    if (followFrameRef.current !== null) {
      if (typeof cancelAnimationFrame === 'function') cancelAnimationFrame(followFrameRef.current);
      else window.clearTimeout(followFrameRef.current);
      followFrameRef.current = null;
    }
    attachFollowMap(null);
    setIsFollowing(false);
  }, [attachFollowMap]);
  stopFollowingRef.current = stopFollowing;
  const startFollowing = useCallback((id: string) => {
    followedGuestIdRef.current = id;
    followStateRef.current = true;
    setIsFollowing(true);
    attachFollowMap(options.mapRef.current);
    const map = options.mapRef.current;
    const rendered = getGuestRenderedPosition(map, id);
    const fallback = runtimePointsRef.current.find((guest) => guest.id === id);
    const point = rendered ?? (fallback ? [fallback.lng, fallback.lat] as const : null);
    if (map && point) map.jumpTo({ center: [point[0], point[1]] });
    scheduleFollowFrame();
  }, [attachFollowMap, options.mapRef, scheduleFollowFrame]);
  const selectGuest = useCallback((id: string) => {
    setSelectedGuestId(id);
    options.dual?.select(id);
    startFollowing(id);
  }, [options.dual, startFollowing]);
  const clearSelectedGuest = useCallback(() => {
    stopFollowing();
    setSelectedGuestId(null);
    options.dual?.select(null);
  }, [options.dual, stopFollowing]);
  useEffect(() => () => {
    stopFollowingRef.current();
  }, []);
  useEffect(() => {
    followedGuestStatusRef.current = selectedGuestIdForFollow &&
      selectedGuestIdForFollow === followedGuestIdRef.current
      ? selectedGuestStatusForFollow ?? null
      : runtime.snapshot?.guests.find((guest) => guest.id === followedGuestIdRef.current)?.status ?? null;
    if (followedGuestStatusRef.current === 'departed') stopFollowing();
  }, [selectedGuestIdForFollow, selectedGuestStatusForFollow, runtime.snapshot, stopFollowing]);
  useEffect(() => {
    if (!options.dual?.publication?.autoTrack) return;
    const id = options.dual.publication.selected?.id;
    if (id && id !== followedGuestIdRef.current) startFollowing(id);
  }, [options.dual?.publication?.autoTrack, options.dual?.publication?.selected?.id, startFollowing]);
  return { portal, connectivity, selectedGuestId: options.dual ? options.dual.publication?.selected?.id ?? null : selectedGuestId, runtime, controller, points: runtime.points, selectGuest,
    vibe: withGuestEconomyControls(options.dual ? dualGuestPresentation(options.dual.publication)
      : guestVibePresentation(runtime.snapshot, selectedGuestId), nextDayTicketPriceCents, setNextDayTicketPriceCents),
    inspectionProps: options.dual ? { inspection: options.dual.publication?.selected, autoTracking: options.dual.publication?.autoTrack,
      onAutoTrack: () => options.dual?.select(null, true),
      onStopAutoTrack: () => options.dual?.select(options.dual.publication?.selected?.id ?? null, false),
      following: isFollowing, onStartFollowing: () => {
        const id = options.dual?.publication?.selected?.id;
        if (id) startFollowing(id);
      }, onStopFollowing: stopFollowing,
      ...(options.dual.weatherReady ? { onFollow: () => {
        const id = options.dual?.publication?.selected?.id;
        if (!options.dual) return;
        void options.dual.follow().then(() => {
          if (id && options.dual?.weatherReady) startFollowing(id);
        });
      } } : {}) } : {},
    following: isFollowing, stopFollowing, clearSelectedGuest, nextDayTicketPriceCents, setNextDayTicketPriceCents };
}
