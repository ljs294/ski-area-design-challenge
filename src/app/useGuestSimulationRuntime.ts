import { useEffect, useMemo, useRef, useState } from 'react';
import type { SimulationClock } from '../types/simulation';
import type { SkiNetwork } from '../network';
import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import { GuestSimulationClient } from './guestSimulationClient';
import { guestNetworkFromSkiNetwork, guestRenderPoints } from './guestNetworkAdapter';
import type { GuestRenderPoint } from './guestLayers';
import type { PlacedGuestPortal } from './guestPortalPlacement';
import { isDesktop } from '../desktopBridge';
import { loadGuestSimulationCheckpoint, saveGuestSimulationCheckpoint } from '../guestSimulationStorageClient';
import { decodeGuestSimulationReplayState } from '../guestSimulation/replayPersistence';

export interface GuestSimulationRuntime {
  readonly status: 'unavailable' | 'starting' | 'ready' | 'error';
  readonly message: string;
  readonly snapshot: GuestSimulationEngineSnapshot | null;
  readonly points: readonly GuestRenderPoint[];
  snapshotBarrier(): Promise<GuestSimulationEngineSnapshot | null>;
  persistBarrier(saveKey: string, gameSaveUpdatedAt: string): Promise<{ ok: true } | { ok: false; error: string }>;
}

function topologyRevision(network: SkiNetwork, portal: PlacedGuestPortal | null): number {
  let hash = 2_166_136_261;
  const values = [portal ? `portal|${portal.id}|${portal.nodeId}|${portal.capacityGuestsPerTick}` : 'portal|none',
    ...[...network.nodes].sort((a, b) => a.id.localeCompare(b.id)).map((node) =>
      `node|${node.id}|${node.kind}|${node.lngLat.join(',')}|${node.liftBases.join(',')}|${node.liftTops.join(',')}`),
    ...[...network.edges].sort((a, b) => a.id.localeCompare(b.id)).map((edge) =>
      `edge|${edge.id}|${edge.kind}|${edge.from}|${edge.to}|${edge.open ? 1 : 0}|${edge.travelTimeS}|${edge.lengthM}|${edge.kind === 'lift' ? `${edge.capacityPph}|${edge.rideTimeS}` : ''}`)];
  for (const value of values) {
    for (let index = 0; index < value.length; index += 1) hash = Math.imul(hash ^ value.charCodeAt(index), 16_777_619) >>> 0;
  }
  return hash;
}

export function useGuestSimulationRuntime(options: {
  readonly saveKey: string | null;
  readonly network: SkiNetwork;
  readonly portal: PlacedGuestPortal | null;
  readonly clock: SimulationClock;
  readonly gameSaveUpdatedAt?: string | null;
  restorePortal?(portal: PlacedGuestPortal): void;
}): GuestSimulationRuntime {
  const restorePortal = options.restorePortal;
  const [snapshot, setSnapshot] = useState<GuestSimulationEngineSnapshot | null>(null);
  const [status, setStatus] = useState<GuestSimulationRuntime['status']>('unavailable');
  const [message, setMessage] = useState('Place a connected Guest Entrance to start visitor simulation.');
  const clientRef = useRef<GuestSimulationClient | null>(null);
  const initializingRef = useRef<Promise<GuestSimulationEngineSnapshot> | null>(null);
  const revision = useMemo(() => topologyRevision(options.network, options.portal), [options.network, options.portal]);
  const guestNetwork = useMemo(() => options.portal
    ? guestNetworkFromSkiNetwork(options.network, options.portal) : null, [options.network, options.portal]);
  const currentTick = options.clock.absoluteGameMinute * 60;
  const currentTickRef = useRef(currentTick);
  currentTickRef.current = currentTick;

  useEffect(() => {
    if (!isDesktop || options.portal || !options.saveKey || !options.gameSaveUpdatedAt || !restorePortal) return;
    let cancelled = false;
    void loadGuestSimulationCheckpoint(options.saveKey, options.gameSaveUpdatedAt).then((loaded) => {
      if (cancelled) return;
      if (loaded.status === 'corrupt') { setStatus('error'); setMessage(`Guest checkpoint is corrupt: ${loaded.error}`); return; }
      if (loaded.status !== 'ready') return;
      const snapshot = decodeGuestSimulationReplayState(loaded.bytes).snapshot as GuestSimulationEngineSnapshot;
      const connection = snapshot.network?.portalConnections[0];
      const portal = snapshot.network?.portals.find((candidate) => candidate.id === connection?.portalId);
      const node = connection ? options.network.nodeById.get(connection.nodeId) : undefined;
      if (portal && connection && node) restorePortal(Object.freeze({ ...portal, nodeId: connection.nodeId,
        lngLat: Object.freeze([...node.lngLat] as [number, number]) }));
      else { setStatus('error'); setMessage('Guest checkpoint entrance no longer matches the resort topology.'); }
    }).catch((error: unknown) => { if (!cancelled) { setStatus('error');
      setMessage(error instanceof Error ? error.message : 'Guest checkpoint could not be loaded.'); } });
    return () => { cancelled = true; };
  }, [options.gameSaveUpdatedAt, options.network, options.portal, options.saveKey, restorePortal]);

  useEffect(() => {
    clientRef.current?.dispose();
    clientRef.current = null;
    initializingRef.current = null;
    setSnapshot(null);
    if (!guestNetwork || !options.portal || guestNetwork.lifts.length === 0 ||
      !guestNetwork.edges.some((edge) => edge.kind === 'descent' && !edge.closed)) {
      setStatus('unavailable');
      setMessage(options.portal ? 'The Guest Entrance needs an open lift and descent.'
        : 'Place a connected Guest Entrance to start visitor simulation.');
      return;
    }
    const client = new GuestSimulationClient();
    clientRef.current = client;
    setStatus('starting'); setMessage('Starting deterministic guest simulation…');
    const dayStart = Math.floor(currentTickRef.current / 86_400) * 86_400;
    const initialize = () => client.initialize({ type: 'initialize' as const, runId: `guest-${options.saveKey ?? 'session'}-${dayStart}`,
      seed: `${options.saveKey ?? 'unsaved'}:${dayStart}`, guestCount: 1_000, network: guestNetwork,
      startTick: dayStart, endTick: dayStart + 43_200, environmentRevision: 1, topologyRevision: revision });
    const pending = isDesktop && options.saveKey && options.gameSaveUpdatedAt
      ? loadGuestSimulationCheckpoint(options.saveKey, options.gameSaveUpdatedAt).then((loaded) => {
        if (loaded.status === 'ready') return client.restore(loaded.bytes, revision);
        if (loaded.status === 'corrupt') throw new Error(`Guest checkpoint is corrupt: ${loaded.error}`);
        return initialize();
      }) : initialize();
    initializingRef.current = pending;
    void pending.then((ready) => {
      if (clientRef.current !== client) return;
      setSnapshot(ready); setStatus('ready'); setMessage('1,000 individually simulated guests ready.');
    }, (error: unknown) => {
      if (clientRef.current !== client) return;
      setStatus('error'); setMessage(error instanceof Error ? error.message : 'Guest simulation failed to start.');
    });
    return () => client.dispose();
  }, [guestNetwork, options.gameSaveUpdatedAt, options.portal, options.saveKey, revision]);

  useEffect(() => {
    const client = clientRef.current;
    const initialization = initializingRef.current;
    if (!client || !initialization) return;
    void initialization.then((ready) => {
      if (clientRef.current !== client || currentTick <= ready.tick) return;
      return client.advance(currentTick, 1, revision).then((advanced) => {
        if (clientRef.current === client) setSnapshot(advanced);
      });
    }).catch((error: unknown) => {
      if (clientRef.current !== client) return;
      setStatus('error'); setMessage(error instanceof Error ? error.message : 'Guest simulation advance failed.');
    });
  }, [currentTick, revision]);

  const points = useMemo(() => snapshot && options.portal
    ? guestRenderPoints(snapshot, options.network, options.portal) : [], [snapshot, options.network, options.portal]);

  return { status, message, snapshot, points,
    snapshotBarrier: async () => clientRef.current ? clientRef.current.snapshot() : null,
    persistBarrier: async (saveKey, gameSaveUpdatedAt) => {
      if (!isDesktop) return { ok: true };
      const client = clientRef.current;
      if (!client) return { ok: true };
      try {
        const checkpoint = await client.checkpoint();
        const saved = await saveGuestSimulationCheckpoint(saveKey, gameSaveUpdatedAt, checkpoint.bytes);
        return saved.ok ? { ok: true } : saved;
      } catch (error) {
        return { ok: false, error: error instanceof Error ? error.message : 'Guest checkpoint failed.' };
      }
    } };
}
