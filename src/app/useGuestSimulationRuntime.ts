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
import type { SnowGrid } from '../types/snow';
import { conditionSnapshotFromSkiNetwork } from './guestConditionAdapter';
import { createConditionSnapshot, type ConditionSnapshot } from '../guestSimulation/conditions';
import type { GuestSimulationWorkerDemandInput } from './guestSimulationWorkerProtocol';

const DEFAULT_PHASE3_DEMAND: GuestSimulationWorkerDemandInput = Object.freeze({
  dayType: 'weekday',
  basePotentialGuests: 1_000,
  ticketPriceCents: 10_000,
  referencePriceCents: 10_000,
  reputation: 0.60,
  resortValue: 0.50,
  operatingFraction: 1,
  conditionFactor: 1,
  availableCapacityGuests: 50_000,
  maxGuests: 50_000,
  maxParties: 20_000,
  bucketSeconds: 10 * 60,
});

function withGuestOccupancy(base: ConditionSnapshot, network: SkiNetwork,
  snapshot: GuestSimulationEngineSnapshot | null): ConditionSnapshot {
  if (!snapshot) return base;
  const counts = new Map<string, number>();
  for (const guest of snapshot.guests) {
    if (!guest.currentResourceId || guest.status === 'departed' || guest.status === 'scheduled') continue;
    const edgeId = network.edgeById.has(guest.currentResourceId) ? guest.currentResourceId
      : network.edges.find((edge) => edge.kind === 'lift' && edge.liftId === guest.currentResourceId)?.id;
    if (edgeId) counts.set(edgeId, (counts.get(edgeId) ?? 0) + 1);
  }
  return createConditionSnapshot({ revision: base.revision, tick: base.tick, edges: base.edges.map((condition) => {
    const edge = network.edgeById.get(condition.edgeId);
    const capacity = edge?.kind === 'lift' ? Math.max(1, Math.round(edge.capacityPph * edge.travelTimeS / 3_600))
      : Math.max(20, Math.round((edge?.travelTimeS ?? 120) / 6));
    return { edgeId: condition.edgeId, revision: condition.revision, baseDifficulty: condition.baseDifficulty,
      grooming: condition.grooming, snowQuality: condition.snowQuality, coverage: condition.coverage,
      occupancy: { guests: counts.get(condition.edgeId) ?? 0, capacity } };
  }) });
}

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
  const values = [portal ? `portal|${portal.id}|${portal.nodeId}|${portal.capacityGuestsPerTick}|${portal.openFromTick}|${portal.openUntilTick}` : 'portal|none',
    ...[...network.nodes].sort((a, b) => a.id.localeCompare(b.id)).map((node) =>
      `node|${node.id}|${node.kind}|${node.lngLat.join(',')}|${node.liftBases.join(',')}|${node.liftTops.join(',')}`),
    ...[...network.edges].sort((a, b) => a.id.localeCompare(b.id)).map((edge) =>
      `edge|${edge.id}|${edge.kind}|${edge.from}|${edge.to}|${edge.open ? 1 : 0}|${edge.travelTimeS}|${edge.lengthM}|${edge.kind === 'lift' ? `${edge.liftTypeId}|${edge.capacityPph}|${edge.rideTimeS}` : edge.kind === 'trail' ? edge.difficulty : ''}`)];
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
  readonly snowGrid?: SnowGrid | null;
  /** Optional day-start market inputs. They are frozen into the day roster. */
  readonly demand?: GuestSimulationWorkerDemandInput;
  restorePortal?(portal: PlacedGuestPortal): void;
}): GuestSimulationRuntime {
  const restorePortal = options.restorePortal;
  const [snapshot, setSnapshot] = useState<GuestSimulationEngineSnapshot | null>(null);
  const [status, setStatus] = useState<GuestSimulationRuntime['status']>('unavailable');
  const [message, setMessage] = useState('Place a connected Guest Entrance to start visitor simulation.');
  const snapshotRef = useRef<GuestSimulationEngineSnapshot | null>(null);
  const clientRef = useRef<GuestSimulationClient | null>(null);
  const initializingRef = useRef<Promise<GuestSimulationEngineSnapshot> | null>(null);
  const revision = useMemo(() => topologyRevision(options.network, options.portal), [options.network, options.portal]);
  const guestNetwork = useMemo(() => options.portal
    ? guestNetworkFromSkiNetwork(options.network, options.portal) : null, [options.network, options.portal]);
  const demand = useMemo(() => Object.freeze({ ...DEFAULT_PHASE3_DEMAND, ...options.demand }), [options.demand]);
  const demandRef = useRef(demand);
  demandRef.current = demand;
  const currentTick = options.clock.absoluteGameMinute * 60;
  const dayStart = Math.floor(currentTick / 86_400) * 86_400;
  snapshotRef.current = snapshot;
  const conditionSnapshot = useMemo(() => withGuestOccupancy(conditionSnapshotFromSkiNetwork(options.network,
    options.snowGrid, { tick: currentTick, revision: Math.max(0, currentTick - dayStart) }),
  options.network, snapshotRef.current), [currentTick, dayStart, options.network, options.snowGrid]);
  const conditionSnapshotRef = useRef(conditionSnapshot);
  const lastAdvanceKeyRef = useRef<string | null>(null);
  conditionSnapshotRef.current = conditionSnapshot;
  const currentTickRef = useRef(currentTick);
  const networkRef = useRef(options.network);
  currentTickRef.current = currentTick;
  networkRef.current = options.network;

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
    const previous = snapshotRef.current;
    const openingReputation = previous && previous.demandPlan.endTick <= dayStart
      ? previous.phase3.economy.closing?.nextDayReputation : undefined;
    clientRef.current?.dispose();
    clientRef.current = null;
    initializingRef.current = null;
    lastAdvanceKeyRef.current = null;
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
    const initialize = () => client.initialize({ type: 'initialize' as const, runId: `guest-${options.saveKey ?? 'session'}-${dayStart}`,
      seed: `${options.saveKey ?? 'unsaved'}:${dayStart}`, demand: demandRef.current, openingReputation, network: guestNetwork,
      startTick: dayStart, endTick: dayStart + 43_200, environmentRevision: 1, topologyRevision: revision,
      conditionSnapshot: conditionSnapshotFromSkiNetwork(networkRef.current, null, { tick: dayStart, revision: 0 }) });
    const pending = isDesktop && options.saveKey && options.gameSaveUpdatedAt
      ? loadGuestSimulationCheckpoint(options.saveKey, options.gameSaveUpdatedAt).then((loaded) => {
        if (loaded.status === 'ready') return client.restore(loaded.bytes, revision).catch(() => initialize());
        if (loaded.status === 'corrupt') throw new Error(`Guest checkpoint is corrupt: ${loaded.error}`);
        return initialize();
      }) : initialize();
    initializingRef.current = pending;
    void pending.then((ready) => {
      if (clientRef.current !== client) return;
      setSnapshot(ready); setStatus('ready'); setMessage(`${ready.metrics.population.toLocaleString()} individually simulated guests ready.`);
    }, (error: unknown) => {
      if (clientRef.current !== client) return;
      setStatus('error'); setMessage(error instanceof Error ? error.message : 'Guest simulation failed to start.');
    });
    return () => client.dispose();
  }, [dayStart, guestNetwork, options.gameSaveUpdatedAt, options.portal, options.saveKey, revision]);

  useEffect(() => {
    const client = clientRef.current;
    const initialization = initializingRef.current;
    if (!client || !initialization) return;
    const publishConditions = currentTick % 300 === 0;
    const advanceKey = `${currentTick}|${revision}|${publishConditions ? conditionSnapshot.checksum : 'no-condition-update'}`;
    if (lastAdvanceKeyRef.current === advanceKey) return;
    lastAdvanceKeyRef.current = advanceKey;
    void initialization.then((ready) => {
      if (clientRef.current !== client || currentTick <= ready.tick) return;
      return client.advance(currentTick, 1, revision, publishConditions ? conditionSnapshotRef.current : undefined).then((advanced) => {
        if (clientRef.current === client) setSnapshot(advanced);
      });
    }).catch((error: unknown) => {
      if (clientRef.current !== client) return;
      if (lastAdvanceKeyRef.current === advanceKey) lastAdvanceKeyRef.current = null;
      setStatus('error'); setMessage(error instanceof Error ? error.message : 'Guest simulation advance failed.');
    });
  }, [conditionSnapshot, currentTick, revision]);

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
