import { useCallback, useEffect, useMemo, useRef, useState, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SkiNetwork } from '../network';
import type { SimulationClock } from '../types/simulation';
import type { SnowGrid } from '../types/snow';
import type { SavedRoad } from '../types/roads';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { useGuestPortalController } from './useGuestPortalController';
import { useGuestSimulationRuntime } from './useGuestSimulationRuntime';

/** Owns the gameplay-facing guest state while keeping MapView as composition only. */
export function useMapGuestSimulationFeature(options: {
  readonly mapRef: RefObject<maplibregl.Map | null>;
  readonly network: SkiNetwork;
  readonly clock: SimulationClock;
  readonly saveKey: string | null;
  readonly saveRevision: string | null;
  readonly snowGrid?: SnowGrid | null;
  readonly roads?: readonly SavedRoad[];
  activate(): boolean;
  release(): void;
  openDock(): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  synchronizeMap(): void;
}) {
  const [portal, setPortal] = useState<import('./guestPortalPlacement').PlacedGuestPortal | null>(null);
  const [selectedGuestId, setSelectedGuestId] = useState<string | null>(null);
  const [nextDayTicketPriceCents, setNextDayTicketPriceCents] = useState(10_000);
  const followedGuestIdRef = useRef<string | null>(null);
  const followMapRef = useRef<maplibregl.Map | null>(null);
  const stopFollowingOnUserMoveRef = useRef((event: maplibregl.MapLibreEvent) => {
    if (event.originalEvent) followedGuestIdRef.current = null;
  });
  const demand = useMemo(() => ({ dayType: options.clock.weekday === 0 || options.clock.weekday === 6 ? 'weekend' as const : 'weekday' as const,
    basePotentialGuests: options.clock.weekday === 0 || options.clock.weekday === 6 ? 1_300 : 900,
    ticketPriceCents: nextDayTicketPriceCents, referencePriceCents: 10_000, reputation: 0.6,
    resortValue: 0.5, availableCapacityGuests: 50_000 }), [nextDayTicketPriceCents, options.clock.weekday]);
  const runtime = useGuestSimulationRuntime({ saveKey: options.saveKey, gameSaveUpdatedAt: options.saveRevision,
    network: options.network, portal, clock: options.clock, snowGrid: options.snowGrid, roads: options.roads,
    demand, restorePortal: setPortal });
  const controller = useGuestPortalController({ mapRef: options.mapRef, network: options.network, portal,
    points: runtime.points, setPortal, activate: options.activate, release: options.release,
    openDock: options.openDock, acquireInteractions: options.acquireInteractions, synchronizeMap: options.synchronizeMap });
  const selectGuest = useCallback((id: string) => {
    setSelectedGuestId(id);
    followedGuestIdRef.current = id;
    const point = runtime.points.find((guest) => guest.id === id), map = options.mapRef.current;
    if (map && followMapRef.current !== map) {
      followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current);
      map.on('movestart', stopFollowingOnUserMoveRef.current);
      followMapRef.current = map;
    }
    if (point && map) map.easeTo({ center: [point.lng, point.lat], zoom: Math.max(map.getZoom(), 16) });
  }, [options.mapRef, runtime.points]);
  const clearSelectedGuest = useCallback(() => {
    followedGuestIdRef.current = null;
    followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current);
    followMapRef.current = null;
    setSelectedGuestId(null);
  }, []);
  useEffect(() => () => { followMapRef.current?.off('movestart', stopFollowingOnUserMoveRef.current); }, []);
  useEffect(() => {
    const followedGuestId = followedGuestIdRef.current;
    const map = options.mapRef.current;
    if (!followedGuestId || !map) return;
    const point = runtime.points.find((guest) => guest.id === followedGuestId);
    if (point) map.easeTo({ center: [point.lng, point.lat], duration: 250 });
  }, [options.mapRef, runtime.points]);
  return { portal, selectedGuestId, runtime, controller, points: runtime.points, selectGuest,
    clearSelectedGuest, nextDayTicketPriceCents, setNextDayTicketPriceCents };
}
