import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SkiNetwork } from '../network';
import { MAP_HIT_RANK, MAP_Z_ORDER, type ManagedMapContribution } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addGuestLayers, GUEST_LAYER_ID, GUEST_LAYER_IDS, setGuestPointData, setGuestPortalData,
  interpolateGuestPoints, updateGuestPointData, type GuestRenderPoint } from './guestLayers';
import { placeGuestPortal, type PlacedGuestPortal } from './guestPortalPlacement';
import type { GuestConnectivity } from './guestConnectivity';

export interface GuestPortalController {
  readonly armed: boolean;
  readonly error: string | null;
  readonly contribution: ManagedMapContribution;
  readonly connectivity: GuestConnectivity;
  arm(): void;
  cancel(): void;
  remove(): void;
}

export function useGuestPortalController(options: {
  mapRef: RefObject<maplibregl.Map | null>;
  network: SkiNetwork;
  portal: PlacedGuestPortal | null;
  points: readonly GuestRenderPoint[];
  reducedMotion: boolean;
  connectivity: GuestConnectivity;
  setPortal(portal: PlacedGuestPortal | null): void;
  activate(): boolean;
  release(): void;
  openDock(): void;
  acquireInteractions(map: maplibregl.Map): MapInteractionLeaseHandle;
  synchronizeMap(): void;
}): GuestPortalController {
  const [armed, setArmed] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const optionsRef = useRef(options);
  optionsRef.current = options;
  const displayedPointsRef = useRef<readonly GuestRenderPoint[]>(options.points);
  const animationFrameRef = useRef<number | null>(null);
  const animationGenerationRef = useRef(0);
  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'guest', zOrder: MAP_Z_ORDER.guest,
    hits: [{ id: 'guest', priority: MAP_HIT_RANK.guest, layerIds: [GUEST_LAYER_ID],
      select: () => undefined }],
    install: ({ map }) => addGuestLayers(map),
    synchronizeData: ({ map }) => {
      animationGenerationRef.current += 1;
      if (animationFrameRef.current !== null) cancelAnimationFrame(animationFrameRef.current);
      animationFrameRef.current = null;
      displayedPointsRef.current = optionsRef.current.points;
      setGuestPortalData(map, optionsRef.current.portal, optionsRef.current.connectivity);
      setGuestPointData(map, optionsRef.current.points);
    },
    visibility: () => [{ id: 'guest-simulation', label: 'Guests', layerIds: GUEST_LAYER_IDS,
      visible: true, section: 'Master plan' }],
    setCaptureTransient: ({ map }, hidden) => {
      for (const id of GUEST_LAYER_IDS) if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', hidden ? 'none' : 'visible');
    },
    cleanup: () => {},
  };

  useEffect(() => { optionsRef.current.synchronizeMap(); }, [options.connectivity, options.portal]);
  useEffect(() => {
    const map = optionsRef.current.mapRef.current;
    const target = options.points;
    const from = displayedPointsRef.current;
    animationGenerationRef.current += 1;
    const generation = animationGenerationRef.current;
    if (animationFrameRef.current !== null) cancelAnimationFrame(animationFrameRef.current);
    animationFrameRef.current = null;
    if (!map || options.reducedMotion || document.hidden || from.length === 0) {
      displayedPointsRef.current = target;
      setGuestPointData(map, target);
      return;
    }
    const durationMs = 240;
    const frameIntervalMs = 1_000 / 30;
    let startedAt: number | null = null;
    let lastPublishedAt = Number.NEGATIVE_INFINITY;
    const animate = (now: number) => {
      if (animationGenerationRef.current !== generation) return;
      startedAt ??= now;
      const progress = Math.min(1, (now - startedAt) / durationMs);
      if (progress >= 1 || now - lastPublishedAt >= frameIntervalMs) {
        const displayed = interpolateGuestPoints(from, target, progress);
        const previous = displayedPointsRef.current;
        displayedPointsRef.current = displayed;
        updateGuestPointData(map, previous, displayed);
        lastPublishedAt = now;
      }
      if (progress < 1) animationFrameRef.current = requestAnimationFrame(animate);
      else animationFrameRef.current = null;
    };
    animationFrameRef.current = requestAnimationFrame(animate);
    return () => {
      animationGenerationRef.current += 1;
      if (animationFrameRef.current !== null) cancelAnimationFrame(animationFrameRef.current);
      animationFrameRef.current = null;
    };
  }, [options.mapRef, options.points, options.reducedMotion]);
  useLayoutEffect(() => {
    const map = optionsRef.current.mapRef.current;
    if (!armed || !map) return;
    const interaction = optionsRef.current.acquireInteractions(map);
    const click = (event: maplibregl.MapMouseEvent) => {
      const result = placeGuestPortal(optionsRef.current.network, [event.lngLat.lng, event.lngLat.lat]);
      if (!result.portal) { setError(result.error); return; }
      optionsRef.current.setPortal(result.portal);
      setError(null); setArmed(false); optionsRef.current.release();
    };
    const key = (event: KeyboardEvent) => { if (event.key === 'Escape') cancel(); };
    map.on('click', click); window.addEventListener('keydown', key);
    return () => { map.off('click', click); window.removeEventListener('keydown', key); interaction.release(); };
  }, [armed]);
  useEffect(() => () => {
    if (animationFrameRef.current !== null) cancelAnimationFrame(animationFrameRef.current);
    optionsRef.current.release();
  }, []);

  function arm(): void {
    if (!optionsRef.current.activate()) return;
    setError(null); setArmed(true); optionsRef.current.openDock();
  }
  function cancel(): void { setArmed(false); setError(null); optionsRef.current.release(); }
  function remove(): void { optionsRef.current.setPortal(null); cancel(); }
  return { armed, error, contribution: contributionRef.current, connectivity: options.connectivity, arm, cancel, remove };
}
