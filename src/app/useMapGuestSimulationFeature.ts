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
import { setGuestCompactFrame, setRepresentativeGuestPresentation, setGuestMotionRoutes } from './guestLayers';
import { analyzeGuestConnectivity } from './guestConnectivity';
import type { SimulationTimeDiscontinuity } from './developerConsoleCommands';
import type { DualSimulationControls } from './useDualClockRuntime';
import type { GuestSimulationRuntime } from './useGuestSimulationRuntime';
import type { SavedTrail } from '../types/trails';
import { dualGuestPresentation } from './dualGuestPresentation';
import { guestVibePresentation, withGuestEconomyControls } from './guestVibePresentation';

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
  inspectGuest?(id: string): void;
  activate(): boolean;
  release(): void;
  openDock(): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  synchronizeMap(): void;
}) {
  const [portal, setPortal] = useState<import('./guestPortalPlacement').PlacedGuestPortal | null>(() => {
    const saved = options.dual?.initialPortal;
    return saved ? { ...saved, version: 1, kind: 'guest-entrance', type: 'guest-entrance', semantics: 'guest-entrance',
      direction: 'inbound', accepts: 'guests', label: 'Guest Entrance', capacityGuestsPerTick: saved.capacityPerMinute / 60,
      openFromTick: 0, openUntilTick: Number.MAX_SAFE_INTEGER } : null;
  });
  const [selectedGuestId, setSelectedGuestId] = useState<string | null>(null);
  const [nextDayTicketPriceCents, setNextDayTicketPriceCents] = useState(options.dual?.initialTicketPriceCents ?? 10_000);
  const followedGuestIdRef = useRef<string | null>(null);
  const followMapRef = useRef<maplibregl.Map | null>(null);
  const stopFollowingOnUserMoveRef = useRef((event: maplibregl.MapLibreEvent) => {
    if (event.originalEvent) followedGuestIdRef.current = null;
  });
  const demand = useMemo(() => ({ dayType: options.clock.weekday === 0 || options.clock.weekday === 6 ? 'weekend' as const : 'weekday' as const,
    basePotentialGuests: options.clock.weekday === 0 || options.clock.weekday === 6 ? 1_300 : 900,
    ticketPriceCents: nextDayTicketPriceCents, referencePriceCents: 10_000, reputation: 0.6,
    resortValue: 0.5, availableCapacityGuests: 50_000 }), [nextDayTicketPriceCents, options.clock.weekday]);
  const weeklyDailyDemand = useMemo(() => {
    const baseline = options.clock.weekday === 0 || options.clock.weekday === 6 ? 1_300 : 900;
    const scale = demand.basePotentialGuests / baseline;
    return [900, 900, 900, 900, 900, 1_300, 1_300].map((value) => Math.max(0, Math.round(value * scale)));
  }, [demand.basePotentialGuests, options.clock.weekday]);
  const publishRenderFrame = useCallback<NonNullable<Parameters<typeof useGuestSimulationRuntime>[0]['publishRenderFrame']>>(
    (frame, edgePaths, portalLngLat) => setGuestCompactFrame(options.mapRef.current, frame, edgePaths, portalLngLat),
    [options.mapRef]);
  const legacyRuntime = useGuestSimulationRuntime({ saveKey: options.dual ? null : options.saveKey, gameSaveUpdatedAt: options.saveRevision,
    network: options.network, portal: options.dual ? null : portal, clock: options.clock, snowGrid: options.snowGrid, roads: options.roads,
    operationsRevision: options.operationsRevision, weatherRevision: options.weatherRevision,
    demand, weeklyDailyDemand, timeDiscontinuity: options.timeDiscontinuity, restorePortal: setPortal,
    publishRenderFrame });
  const runtime: GuestSimulationRuntime = options.dual ? { ...legacyRuntime,
    status: options.dual.error ? 'error' : options.dual.ready ? 'ready' : 'starting', message: options.dual.error ?? 'Representative guest simulation',
    points: options.dual.publication?.points ?? [], committedSecond: options.dual.publication?.clock.macroSecond ?? 0,
    dualCheckpoint: options.dual.checkpoint } : legacyRuntime;
  const connectivity = useMemo(() => analyzeGuestConnectivity(options.network, portal, options.roads),
    [options.network, options.roads, portal]);
  const updateDualResort = options.dual?.updateResort, dualReady = options.dual?.ready;
  useEffect(() => {
    if (!dualReady || !updateDualResort) return;
    updateDualResort({ revision: resortRevision(options.network.edges, options.trails ?? []), edges: options.network.edges, trails: options.trails ?? [],
      portal: portal ? { id: portal.id, nodeId: portal.nodeId, lngLat: portal.lngLat,
        capacityPerMinute: portal.capacityGuestsPerTick * 60 } : null,
      dailyDemand: 900, dailyDemandByWeekday: [1300, 900, 900, 900, 900, 900, 1300],
      ticketPriceCents: nextDayTicketPriceCents, amenities: defaultDualAmenities(portal?.nodeId ?? null) });
  }, [dualReady, updateDualResort, options.network, options.trails, portal, nextDayTicketPriceCents]);
  useEffect(() => {
    const publication = options.dual?.publication;
    if (options.dual) setGuestMotionRoutes(options.mapRef.current, options.dual.geometry);
    setRepresentativeGuestPresentation(options.mapRef.current, !!options.dual,
      publication && publication.clock.speed >= 8 && publication.advance?.state !== 'running' ? publication.flow : null, options.network.edges);
  }, [options.dual, options.mapRef, options.network]);
  const controller = useGuestPortalController({ mapRef: options.mapRef, network: options.network, portal,
    points: runtime.points, reducedMotion: options.reducedMotion, connectivity, setPortal,
    selectGuest: options.inspectGuest, activate: options.activate, release: options.release,
    openDock: options.openDock, acquireInteractions: options.acquireInteractions, synchronizeMap: options.synchronizeMap });
  const selectGuest = useCallback((id: string) => {
    setSelectedGuestId(id);
    options.dual?.select(id);
    if (options.dual) return;
    followedGuestIdRef.current = id;
    const point = runtime.points.find((guest) => guest.id === id), map = options.mapRef.current;
    if (map && followMapRef.current !== map) {
      followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current);
      map.on('movestart', stopFollowingOnUserMoveRef.current);
      followMapRef.current = map;
    }
    if (point && map) map.easeTo({ center: [point.lng, point.lat], zoom: Math.max(map.getZoom(), 16) });
  }, [options.mapRef, options.dual, runtime.points]);
  const clearSelectedGuest = useCallback(() => {
    followedGuestIdRef.current = null;
    followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current);
    followMapRef.current = null;
    setSelectedGuestId(null);
    options.dual?.select(null);
  }, [options.dual]);
  useEffect(() => () => { followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current); }, []);
  useEffect(() => {
    const followedGuestId = followedGuestIdRef.current;
    const map = options.mapRef.current;
    if (!followedGuestId || !map) return;
    const point = runtime.points.find((guest) => guest.id === followedGuestId);
    if (point) map.easeTo({ center: [point.lng, point.lat], duration: 250 });
  }, [options.mapRef, runtime.points]);
  useEffect(() => {
    if (followedGuestIdRef.current && options.dual?.publication?.autoTrack) followedGuestIdRef.current = options.dual.publication.selected?.id ?? null;
  }, [options.dual?.publication?.selected?.id, options.dual?.publication?.autoTrack]);
  return { portal, connectivity, selectedGuestId: options.dual ? options.dual.publication?.selected?.id ?? null : selectedGuestId, runtime, controller, points: runtime.points, selectGuest,
    vibe: withGuestEconomyControls(options.dual ? dualGuestPresentation(options.dual.publication)
      : guestVibePresentation(runtime.snapshot, selectedGuestId), nextDayTicketPriceCents, setNextDayTicketPriceCents),
    inspectionProps: options.dual ? { inspection: options.dual.publication?.selected, autoTracking: options.dual.publication?.autoTrack,
      onAutoTrack: () => options.dual?.select(null, true),
      onStopAutoTrack: () => options.dual?.select(options.dual.publication?.selected?.id ?? null, false),
      onFollow: () => {
        options.dual?.follow(); followedGuestIdRef.current = options.dual?.publication?.selected?.id ?? null;
        const map = options.mapRef.current;
        if (map && followMapRef.current !== map) {
          followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current);
          map.on('movestart', stopFollowingOnUserMoveRef.current); followMapRef.current = map;
        }
      } } : {},
    clearSelectedGuest, nextDayTicketPriceCents, setNextDayTicketPriceCents };
}
