import { useEffect, useLayoutEffect, useRef, useState, type RefObject } from 'react';
import type maplibregl from 'maplibre-gl';
import type { SkiNetwork } from '../network';
import { MAP_HIT_RANK, MAP_Z_ORDER, type ManagedMapContribution } from './mapContribution';
import type { MapInteractionLeaseHandle } from './mapInteractionLease';
import { addGuestLayers, GUEST_LAYER_ID, GUEST_LAYER_IDS, setGuestPointData, setGuestPortalData,
  type GuestRenderPoint } from './guestLayers';
import { placeGuestPortal, type PlacedGuestPortal } from './guestPortalPlacement';

export interface GuestPortalController {
  readonly armed: boolean;
  readonly error: string | null;
  readonly contribution: ManagedMapContribution;
  arm(): void;
  cancel(): void;
  remove(): void;
}

export function useGuestPortalController(options: {
  mapRef: RefObject<maplibregl.Map | null>;
  network: SkiNetwork;
  portal: PlacedGuestPortal | null;
  points: readonly GuestRenderPoint[];
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
  const contributionRef = useRef<ManagedMapContribution | null>(null);
  if (!contributionRef.current) contributionRef.current = {
    id: 'guest', zOrder: MAP_Z_ORDER.guest,
    hits: [{ id: 'guest', priority: MAP_HIT_RANK.guest, layerIds: [GUEST_LAYER_ID],
      select: () => undefined }],
    install: ({ map }) => addGuestLayers(map),
    synchronizeData: ({ map }) => {
      setGuestPortalData(map, optionsRef.current.portal);
      setGuestPointData(map, optionsRef.current.points);
    },
    visibility: () => [{ id: 'guest-simulation', label: 'Guests', layerIds: GUEST_LAYER_IDS,
      visible: true, section: 'Master plan' }],
    setCaptureTransient: ({ map }, hidden) => {
      for (const id of GUEST_LAYER_IDS) if (map.getLayer(id)) map.setLayoutProperty(id, 'visibility', hidden ? 'none' : 'visible');
    },
    cleanup: () => {},
  };

  useEffect(() => { optionsRef.current.synchronizeMap(); }, [options.portal, options.points]);
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
  useEffect(() => () => optionsRef.current.release(), []);

  function arm(): void {
    if (!optionsRef.current.activate()) return;
    setError(null); setArmed(true); optionsRef.current.openDock();
  }
  function cancel(): void { setArmed(false); setError(null); optionsRef.current.release(); }
  function remove(): void { optionsRef.current.setPortal(null); cancel(); }
  return { armed, error, contribution: contributionRef.current, arm, cancel, remove };
}
