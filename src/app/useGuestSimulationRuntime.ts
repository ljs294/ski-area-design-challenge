import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { SimulationClock } from '../types/simulation';
import type { SkiNetwork } from '../network';
import type { GuestSimulationEngineSnapshot } from '../guestSimulation/engine';
import { GuestSimulationClient } from './guestSimulationClient';
import { guestNetworkFromSkiNetwork, guestRenderPoints } from './guestNetworkAdapter';
import type { GuestRenderPoint } from './guestLayers';
import type { GuestRenderPath } from './guestGpuLayer';
import type { PlacedGuestPortal } from './guestPortalPlacement';
import { isDesktop } from '../desktopBridge';
import { loadGuestSimulationCheckpoint, saveGuestSimulationCheckpoint } from '../guestSimulationStorageClient';
import { decodeGuestSimulationReplayState } from '../guestSimulation/replayPersistence';
import type { SnowGrid } from '../types/snow';
import type { SavedRoad } from '../types/roads';
import { guestAccessFromRoads } from './guestAccessAdapter';
import { conditionSnapshotFromSkiNetwork } from './guestConditionAdapter';
import type { GuestSimulationWorkerDemandInput, GuestSimulationRenderFrame } from './guestSimulationWorkerProtocol';
import { weeklyGuestWeighting, type WeeklyGuestWeighting } from '../guestSimulation/weeklyDemand';
import { GUEST_RENDER_STATUS_FLAGS } from '../guestSimulation/guestRenderFrame';
import { guestCheckpointMatchesOperatingWindow, guestOperatingWindowForWeek,
  guestSimulationWindowAfterClockDiscontinuity } from './guestRuntimeSchedule';
import type { SimulationTimeDiscontinuity } from './developerConsoleCommands';

const DEFAULT_DEMAND_BUCKET_SECONDS = 10 * 60;
const DISCONTINUITY_DEMAND_BUCKET_SECONDS = 60;
const MAX_ACTIVE_GUESTS = 10_000;
const RICH_SNAPSHOT_MIN_INTERVAL_MS = 500;
const DEFAULT_WEEKLY_DAILY_DEMAND = Object.freeze([900, 900, 900, 900, 900, 1_300, 1_300]);
const DEFAULT_PHASE3_DEMAND: GuestSimulationWorkerDemandInput = Object.freeze({
  dayType: 'weekday', basePotentialGuests: 1_000, ticketPriceCents: 10_000,
  referencePriceCents: 10_000, reputation: 0.60, resortValue: 0.50, operatingFraction: 1,
  conditionFactor: 1, availableCapacityGuests: 50_000, maxGuests: MAX_ACTIVE_GUESTS,
  maxParties: MAX_ACTIVE_GUESTS, bucketSeconds: DEFAULT_DEMAND_BUCKET_SECONDS,
});

const STATUS_BY_FLAG: readonly [number, string][] = [
  [GUEST_RENDER_STATUS_FLAGS.scheduled, 'scheduled'],
  [GUEST_RENDER_STATUS_FLAGS.arriving, 'arriving'],
  [GUEST_RENDER_STATUS_FLAGS.waitingForRoute, 'waiting-for-route'],
  [GUEST_RENDER_STATUS_FLAGS.choosing, 'choosing'],
  [GUEST_RENDER_STATUS_FLAGS.travellingToLift, 'travelling-to-lift'],
  [GUEST_RENDER_STATUS_FLAGS.liftQueue, 'lift-queue'],
  [GUEST_RENDER_STATUS_FLAGS.liftRide, 'lift-ride'],
  [GUEST_RENDER_STATUS_FLAGS.skiing, 'skiing'],
  [GUEST_RENDER_STATUS_FLAGS.appraising, 'appraising'],
  [GUEST_RENDER_STATUS_FLAGS.departing, 'departing'],
  [GUEST_RENDER_STATUS_FLAGS.departed, 'departed'],
  [GUEST_RENDER_STATUS_FLAGS.facilityQueue, 'facility-queue'],
  [GUEST_RENDER_STATUS_FLAGS.facilityService, 'facility-service'],
  [GUEST_RENDER_STATUS_FLAGS.regrouping, 'regrouping'],
  [GUEST_RENDER_STATUS_FLAGS.incident, 'incident'],
  [GUEST_RENDER_STATUS_FLAGS.patrolResponse, 'patrol-response'],
  [GUEST_RENDER_STATUS_FLAGS.lodging, 'lodging'],
  [GUEST_RENDER_STATUS_FLAGS.roadTravel, 'road-travel'],
];

function clampUnit(value: number): number { return Math.min(1, Math.max(0, value)); }

function pathProgressPosition(path: readonly (readonly [number, number])[], progress: number): readonly [number, number] {
  if (path.length === 0) return [0, 0];
  if (path.length === 1) return path[0]!;
  const lengths: number[] = [];
  let total = 0;
  for (let index = 1; index < path.length; index += 1) {
    const from = path[index - 1]!, to = path[index]!;
    const latitudeScale = Math.cos(((from[1] + to[1]) / 2) * Math.PI / 180);
    const length = Math.hypot((to[0] - from[0]) * latitudeScale, to[1] - from[1]);
    lengths.push(length); total += length;
  }
  if (total <= Number.EPSILON) return path[0]!;
  let remaining = clampUnit(progress) * total;
  for (let index = 0; index < lengths.length; index += 1) {
    const length = lengths[index]!;
    if (remaining <= length || index === lengths.length - 1) {
      const from = path[index]!, to = path[index + 1]!;
      const fraction = length <= Number.EPSILON ? 0 : clampUnit(remaining / length);
      return [from[0] + (to[0] - from[0]) * fraction, from[1] + (to[1] - from[1]) * fraction];
    }
    remaining -= length;
  }
  return path[path.length - 1]!;
}

function statusForFlags(flags: number): string | null {
  const status = STATUS_BY_FLAG.find(([flag]) => (flags & flag) !== 0)?.[1] ?? null;
  return status === 'scheduled' || status === 'departed' ? null : status;
}

/** Project a compact worker frame directly into map points at render cadence. */
export function guestRenderPointsFromCompactFrame(
  frame: GuestSimulationRenderFrame,
  snapshot: GuestSimulationEngineSnapshot,
  network: SkiNetwork,
  portal: PlacedGuestPortal,
): readonly GuestRenderPoint[] {
  const idByOrdinal = new Map(snapshot.guests.map((guest) => [guest.ordinal + 1, guest.id]));
  const points: GuestRenderPoint[] = [];
  for (let index = 0; index < frame.ids.length; index += 1) {
    const id = idByOrdinal.get(frame.ids[index]!);
    const status = statusForFlags(frame.statusFlags[index] ?? 0);
    if (!id || !status) continue;
    const workerEdge = snapshot.network.edges[frame.edgeIndices[index] ?? -1];
    const edge = workerEdge ? network.edgeById.get(workerEdge.id) : undefined;
    const position = edge ? pathProgressPosition(edge.path, frame.progress[index] ?? 0) : portal.lngLat;
    points.push({ id, lng: position[0], lat: position[1], status });
  }
  return points;
}

function topologyRevision(network: SkiNetwork, portal: PlacedGuestPortal | null, roads: readonly SavedRoad[]): number {
  let hash = 2_166_452_261;
  const values = [portal ? `portal|${portal.id}|${portal.nodeId}|${portal.capacityGuestsPerTick}|${portal.openFromTick}|${portal.openUntilTick}` : 'portal|none',
    ...[...network.nodes].sort((a, b) => a.id.localeCompare(b.id)).map((node) =>
      `node|${node.id}|${node.kind}|${node.lngLat.join(',')}|${node.liftBases.join(',')}|${node.liftTops.join(',')}`),
    ...[...network.edges].sort((a, b) => a.id.localeCompare(b.id)).map((edge) =>
      `edge|${edge.id}|${edge.kind}|${edge.from}|${edge.to}|${edge.open ? 1 : 0}|${edge.travelTimeS}|${edge.lengthM}|${edge.kind === 'lift' ? `${edge.liftTypeId}|${edge.capacityPph}|${edge.rideTimeS}` : edge.kind === 'trail' ? edge.difficulty : ''}`)];
  values.push(...[...roads].sort((a, b) => a.id.localeCompare(b.id))
    .map((road) => `road|${road.id}|${road.lengthM}|${road.points.map((point) => point.join(',')).join(';')}`));
  for (const value of values) {
    for (let index = 0; index < value.length; index += 1) hash = Math.imul(hash ^ value.charCodeAt(index), 16_777_619) >>> 0;
  }
  return hash;
}

function weeklyDemandFor(
  supplied: readonly number[] | undefined,
  demand: GuestSimulationWorkerDemandInput | undefined,
): readonly number[] {
  if (supplied) return supplied;
  const values = [...DEFAULT_WEEKLY_DAILY_DEMAND];
  if (!demand || !Number.isFinite(demand.basePotentialGuests)) return values;
  const baseline = demand.dayType === 'weekend' ? 1_300 : 900;
  const scale = demand.basePotentialGuests / baseline;
  return values.map((value) => Math.max(0, Math.round(value * scale)));
}

function weeklyGuestWeightingFor(
  supplied: readonly number[] | undefined,
  demand: GuestSimulationWorkerDemandInput | undefined,
): WeeklyGuestWeighting {
  return weeklyGuestWeighting(weeklyDemandFor(supplied, demand), MAX_ACTIVE_GUESTS);
}

export interface GuestSimulationRuntime {
  readonly status: 'unavailable' | 'starting' | 'ready' | 'error';
  readonly message: string;
  readonly snapshot: GuestSimulationEngineSnapshot | null;
  readonly points: readonly GuestRenderPoint[];
  readonly weeklyGuestWeighting: WeeklyGuestWeighting;
  readonly outcomeWeight: number;
  readonly committedSecond: number;
  readonly backlogSeconds: number;
  snapshotBarrier(): Promise<GuestSimulationEngineSnapshot | null>;
  /**
   * Flush the worker sidecar to the timestamp represented by the save.  The
   * optional expected second is deliberately a barrier input, rather than a
   * value read after the flush: a running simulation may publish a newer
   * commit while an explicit save is waiting on the worker.
   */
  persistBarrier(saveKey: string, gameSaveUpdatedAt: string, expectedSecond?: number):
    Promise<{ ok: true; committedSecond: number } | { ok: false; error: string }>;
}

export function useGuestSimulationRuntime(options: {
  readonly saveKey: string | null;
  readonly network: SkiNetwork;
  readonly portal: PlacedGuestPortal | null;
  readonly clock: SimulationClock;
  readonly gameSaveUpdatedAt?: string | null;
  readonly snowGrid?: SnowGrid | null;
  readonly roads?: readonly SavedRoad[];
  /** Monotonic operational revision supplied by the resort controller. */
  readonly operationsRevision?: number;
  /** Monotonic weather revision; snow-grid identity is used as a fallback. */
  readonly weatherRevision?: number;
  readonly weeklyDailyDemand?: readonly number[];
  readonly timeDiscontinuity?: SimulationTimeDiscontinuity | null;
  readonly demand?: GuestSimulationWorkerDemandInput;
  restorePortal?(portal: PlacedGuestPortal): void;
  /** Presentation sink; called directly from worker completion, outside React state. */
  publishRenderFrame?(frame: GuestSimulationRenderFrame | null, edgePaths: readonly GuestRenderPath[],
    portalLngLat?: readonly [number, number]): void;
}): GuestSimulationRuntime {
  const [snapshot, setSnapshot] = useState<GuestSimulationEngineSnapshot | null>(null);
  const [points, setPoints] = useState<readonly GuestRenderPoint[]>([]);
  const [status, setStatus] = useState<GuestSimulationRuntime['status']>('unavailable');
  const [message, setMessage] = useState('Place a connected Guest Entrance to start visitor simulation.');
  const [committedSecond, setCommittedSecond] = useState(0);
  const [backlogSeconds, setBacklogSeconds] = useState(0);
  const snapshotRef = useRef<GuestSimulationEngineSnapshot | null>(null);
  const clientRef = useRef<GuestSimulationClient | null>(null);
  const initializingRef = useRef<Promise<GuestSimulationEngineSnapshot> | null>(null);
  const advanceInFlightRef = useRef<Promise<void> | null>(null);
  const richSnapshotInFlightRef = useRef<Promise<void> | null>(null);
  const environmentUpdateInFlightRef = useRef<Promise<void> | null>(null);
  const topologyUpdateInFlightRef = useRef<Promise<void> | null>(null);
  const lastRichSnapshotWallRef = useRef(Number.NEGATIVE_INFINITY);
  const operationsRevisionRef = useRef(options.operationsRevision ?? 0);
  const weatherRevisionRef = useRef(options.weatherRevision ?? 0);
  const operationsInputRef = useRef(options.operationsRevision);
  operationsInputRef.current = options.operationsRevision;
  const weatherInputRef = useRef(options.weatherRevision);
  weatherInputRef.current = options.weatherRevision;
  const conditionRevisionRef = useRef(0);
  const lastConditionSnowGridRef = useRef<SnowGrid | null | undefined>(undefined);
  const targetSecondRef = useRef(0);
  const committedSecondRef = useRef(0);
  const currentSecond = options.clock.season === 'winter'
    ? Number.isFinite(options.clock.elapsedSimSecond) ? options.clock.elapsedSimSecond
      : Math.max(0, options.clock.absoluteGameMinute * 60)
    : 0;
  const currentSecondRef = useRef(currentSecond);
  currentSecondRef.current = currentSecond;
  targetSecondRef.current = Math.max(targetSecondRef.current, currentSecond);
  const winterWeekIndex = options.clock.season === 'winter'
    ? Number.isSafeInteger(options.clock.winterWeek) && (options.clock.winterWeek ?? 0) > 0
      ? (options.clock.winterWeek ?? 1) - 1 : Math.floor(currentSecond / 43_200)
    : 0;
  const operatingWindow = useMemo(() => guestOperatingWindowForWeek(winterWeekIndex), [winterWeekIndex]);
  const discontinuityRevision = options.timeDiscontinuity?.revision ?? null;
  const simulationWindow = useMemo(() => guestSimulationWindowAfterClockDiscontinuity(operatingWindow,
    currentSecondRef.current, discontinuityRevision === null ? null : { revision: discontinuityRevision },
    discontinuityRevision === null ? DEFAULT_DEMAND_BUCKET_SECONDS : DISCONTINUITY_DEMAND_BUCKET_SECONDS),
  [discontinuityRevision, operatingWindow]);
  const simulationStartTick = simulationWindow.startTick;
  const skippedPastOperatingWindow = simulationWindow.startTick >= simulationWindow.endTick;
  const runtimePortal = useMemo(() => options.portal ? Object.freeze({ ...options.portal,
    openFromTick: operatingWindow.startTick, openUntilTick: operatingWindow.endTick }) : null,
  [operatingWindow.endTick, operatingWindow.startTick, options.portal]);
  const revision = useMemo(() => topologyRevision(options.network, runtimePortal, options.roads ?? []),
    [options.network, options.roads, runtimePortal]);
  const guestNetwork = useMemo(() => runtimePortal
    ? guestNetworkFromSkiNetwork(options.network, runtimePortal, operatingWindow) : null,
  [operatingWindow, options.network, runtimePortal]);
  const access = useMemo(() => runtimePortal ? guestAccessFromRoads(options.roads ?? [], runtimePortal) : undefined,
    [runtimePortal, options.roads]);
  const weeklyWeighting = useMemo(() => weeklyGuestWeightingFor(options.weeklyDailyDemand, options.demand),
    [options.demand, options.weeklyDailyDemand]);
  const demand = useMemo(() => Object.freeze({ ...DEFAULT_PHASE3_DEMAND, ...options.demand,
    basePotentialGuests: weeklyWeighting.simulatedRoster,
    outcomeWeight: weeklyWeighting.outcomeWeight,
    maxGuests: Math.min(MAX_ACTIVE_GUESTS, options.demand?.maxGuests ?? MAX_ACTIVE_GUESTS),
    maxParties: Math.min(MAX_ACTIVE_GUESTS, options.demand?.maxParties ?? MAX_ACTIVE_GUESTS),
    ...(discontinuityRevision === null ? {} : { bucketSeconds: DISCONTINUITY_DEMAND_BUCKET_SECONDS })
  }), [discontinuityRevision, options.demand, weeklyWeighting]);
  const demandRef = useRef(demand);
  demandRef.current = demand;
  snapshotRef.current = snapshot;
  const networkRef = useRef(options.network);
  networkRef.current = options.network;
  const portalRef = useRef(options.portal);
  portalRef.current = options.portal;
  const snowGridRef = useRef(options.snowGrid);
  snowGridRef.current = options.snowGrid;
  const publishRenderFrameRef = useRef(options.publishRenderFrame);
  publishRenderFrameRef.current = options.publishRenderFrame;
  const renderEdgePaths = useMemo<readonly GuestRenderPath[]>(() => guestNetwork
    ? guestNetwork.edges.map((edge) => options.network.edgeById.get(edge.id)?.path ?? []) : [],
  [guestNetwork, options.network]);
  // Desired topology may change while the worker is running. The applied
  // revision is acknowledged by the worker and is intentionally separate.
  const topologyRevisionRef = useRef(revision);
  const desiredTopologyRevisionRef = useRef(revision);
  desiredTopologyRevisionRef.current = revision;
  const guestNetworkRef = useRef(guestNetwork);
  guestNetworkRef.current = guestNetwork;
  const renderEdgePathsRef = useRef(renderEdgePaths);
  renderEdgePathsRef.current = renderEdgePaths;
  const portalLngLatRef = useRef(options.portal?.lngLat);
  portalLngLatRef.current = options.portal?.lngLat;
  const accessRef = useRef(access);
  accessRef.current = access;
  const appliedTopologyRevisionRef = useRef<number | null>(null);

  const updateReadyState = useCallback((client: GuestSimulationClient, ready: GuestSimulationEngineSnapshot) => {
    if (clientRef.current !== client) return;
    snapshotRef.current = ready;
    appliedTopologyRevisionRef.current ??= ready.topologyRevision;
    setSnapshot(ready);
    const committed = Math.max(committedSecondRef.current, ready.tick);
    committedSecondRef.current = committed;
    setCommittedSecond(committed);
    const portal = portalRef.current;
    if (portal) setPoints(guestRenderPoints(ready, networkRef.current, portal));
  }, []);

  const refreshRichSnapshot = useCallback(async (client: GuestSimulationClient) => {
    if (richSnapshotInFlightRef.current || clientRef.current !== client) return;
    const now = typeof performance === 'undefined' ? Date.now() : performance.now();
    if (now - lastRichSnapshotWallRef.current < RICH_SNAPSHOT_MIN_INTERVAL_MS) return;
    lastRichSnapshotWallRef.current = now;
    const request = client.snapshot().then((next) => updateReadyState(client, next));
    const pending = request.then(() => undefined, () => undefined);
    richSnapshotInFlightRef.current = pending;
    try { await request; } finally {
      if (richSnapshotInFlightRef.current === pending) richSnapshotInFlightRef.current = null;
    }
  }, [updateReadyState]);

  const drainCompact = useCallback((client: GuestSimulationClient, fixedTargetSecond?: number): Promise<void> => {
    if (advanceInFlightRef.current) return advanceInFlightRef.current;
    const pending = (async () => {
      const initialization = initializingRef.current;
      if (initialization) await initialization;
      if (clientRef.current !== client) return;
      const hasFixedTarget = fixedTargetSecond !== undefined;
      while (clientRef.current === client) {
        if (desiredTopologyRevisionRef.current !== appliedTopologyRevisionRef.current) break;
        // A hot environment revision is posted only after the prior advance
        // has committed.  Wait for its acknowledgement before sending the
        // next target so the worker never receives an old revision envelope.
        const environmentUpdate = environmentUpdateInFlightRef.current;
        if (environmentUpdate) await environmentUpdate;
        if (clientRef.current !== client) return;
        const targetSecond = hasFixedTarget ? fixedTargetSecond : targetSecondRef.current;
        if (targetSecond === undefined || targetSecond <= committedSecondRef.current + Number.EPSILON) break;
        const response = await client.advanceCompact({ targetSecond, maxCpuMs: 8,
          topologyRevision: topologyRevisionRef.current, operationsRevision: operationsRevisionRef.current,
          weatherRevision: weatherRevisionRef.current });
        if (clientRef.current !== client) return;
        committedSecondRef.current = Math.max(committedSecondRef.current, response.committedSecond);
        setCommittedSecond(committedSecondRef.current);
        setBacklogSeconds(Math.max(0, targetSecondRef.current - committedSecondRef.current));
        // Compact frames are consumed by the custom WebGL layer. Keep the
        // rich point list for the bounded hit/dashboard refresh only.
        publishRenderFrameRef.current?.(response.renderFrame, renderEdgePathsRef.current, portalLngLatRef.current);
        if (response.backlogSeconds > 0) {
          setMessage(`Catching up - ${response.backlogSeconds.toFixed(1)} simulation seconds remain.`);
        }
        await refreshRichSnapshot(client);
      }
      setBacklogSeconds(Math.max(0, targetSecondRef.current - committedSecondRef.current));
    })().catch((error: unknown) => {
      if (clientRef.current !== client) return;
      setStatus('error'); setMessage(error instanceof Error ? error.message : 'Guest simulation advance failed.');
    }).finally(() => {
      if (advanceInFlightRef.current === pending) advanceInFlightRef.current = null;
    });
    advanceInFlightRef.current = pending;
    return pending;
  }, [refreshRichSnapshot]);

  useEffect(() => {
    if (!isDesktop || options.portal || !options.saveKey || !options.gameSaveUpdatedAt || !options.restorePortal) return;
    let cancelled = false;
    void loadGuestSimulationCheckpoint(options.saveKey, options.gameSaveUpdatedAt).then((loaded) => {
      if (cancelled) return;
      if (loaded.status === 'corrupt') { setStatus('error'); setMessage(`Guest checkpoint is corrupt: ${loaded.error}`); return; }
      if (loaded.status !== 'ready') return;
      const restored = decodeGuestSimulationReplayState(loaded.bytes).snapshot as GuestSimulationEngineSnapshot;
      const connection = restored.network?.portalConnections[0];
      const portal = restored.network?.portals.find((candidate) => candidate.id === connection?.portalId);
      const node = connection ? options.network.nodeById.get(connection.nodeId) : undefined;
      if (portal && connection && node) options.restorePortal!(Object.freeze({ ...portal, nodeId: connection.nodeId,
        lngLat: Object.freeze([...node.lngLat] as [number, number]) }));
      else { setStatus('error'); setMessage('Guest checkpoint entrance no longer matches the resort topology.'); }
    }).catch((error: unknown) => { if (!cancelled) { setStatus('error');
      setMessage(error instanceof Error ? error.message : 'Guest checkpoint could not be loaded.'); } });
    return () => { cancelled = true; };
  }, [options.gameSaveUpdatedAt, options.network, options.portal, options.restorePortal, options.saveKey]);

  useEffect(() => {
    const previous = snapshotRef.current;
    const repeatVisitors = previous?.phase5to7?.repeatVisitors;
    const initialRevision = desiredTopologyRevisionRef.current;
    const initialSnowGrid = snowGridRef.current;
    const initialPortal = portalRef.current;
    const initialNetwork = guestNetworkRef.current;
    const initialAccess = accessRef.current;
    clientRef.current?.dispose();
    clientRef.current = null;
    initializingRef.current = null;
    advanceInFlightRef.current = null;
    richSnapshotInFlightRef.current = null;
    environmentUpdateInFlightRef.current = null;
    topologyUpdateInFlightRef.current = null;
    lastRichSnapshotWallRef.current = Number.NEGATIVE_INFINITY;
    operationsRevisionRef.current = operationsInputRef.current ?? 0;
    weatherRevisionRef.current = weatherInputRef.current ?? 0;
    conditionRevisionRef.current = 0;
    lastConditionSnowGridRef.current = initialSnowGrid;
    topologyRevisionRef.current = initialRevision;
    appliedTopologyRevisionRef.current = null;
    committedSecondRef.current = simulationStartTick;
    targetSecondRef.current = Math.max(simulationStartTick, currentSecondRef.current);
    setCommittedSecond(simulationStartTick); setBacklogSeconds(0); setSnapshot(null); setPoints([]);
    publishRenderFrameRef.current?.(null, renderEdgePathsRef.current, portalLngLatRef.current);
    if (options.clock.season !== 'winter' || skippedPastOperatingWindow) {
      setStatus('unavailable');
      setMessage(options.clock.season !== 'winter' ? 'Guest simulation resumes during winter.'
        : 'The guest operating day has ended; arrivals resume in the next winter week.');
      return;
    }
    if (!initialNetwork || !initialPortal || initialNetwork.lifts.length === 0 ||
      !initialNetwork.edges.some((edge) => edge.kind === 'descent' && !edge.closed)) {
      setStatus('unavailable');
      setMessage(initialPortal ? 'The Guest Entrance needs an open lift and descent.'
        : 'Place a connected Guest Entrance to start visitor simulation.');
      return;
    }
    const client = new GuestSimulationClient();
    clientRef.current = client;
    setStatus('starting'); setMessage('Starting deterministic guest simulation...');
    const initialize = (carryForward?: GuestSimulationEngineSnapshot) => {
      // A legacy sidecar can describe a previous daily roster. It cannot be
      // replayed into this composite week, but its durable reputation and
      // repeat-visitor memory must survive the roster rebuild.
      const carryReputation = carryForward?.phase3?.economy.closing?.nextDayReputation
        ?? carryForward?.phase3?.economy.openingReputation;
      const carryRepeatVisitors = carryForward?.phase5to7?.repeatVisitors ?? repeatVisitors;
      return client.initialize({ type: 'initialize' as const,
        runId: `guest-${options.saveKey ?? 'session'}-${simulationStartTick}`,
        seed: `${options.saveKey ?? 'unsaved'}:${Math.floor(simulationStartTick / 43_200)}`,
        demand: demandRef.current, network: initialNetwork,
        ...(carryReputation ? { openingReputation: carryReputation } : {}),
        ...(initialAccess || carryRepeatVisitors ? { phase5to7: { ...(initialAccess ? { access: initialAccess } : {}),
          ...(carryRepeatVisitors ? { repeatVisitors: carryRepeatVisitors } : {}) } } : {}),
        startTick: simulationWindow.startTick, endTick: simulationWindow.endTick,
        environmentRevision: 1, topologyRevision: initialRevision,
        operationsRevision: operationsRevisionRef.current, weatherRevision: weatherRevisionRef.current,
        conditionSnapshot: conditionSnapshotFromSkiNetwork(networkRef.current, initialSnowGrid,
          { tick: simulationWindow.startTick, revision: 0 }) });
    };
    const pending = isDesktop && options.saveKey && options.gameSaveUpdatedAt
      ? loadGuestSimulationCheckpoint(options.saveKey, options.gameSaveUpdatedAt).then((loaded) => {
        if (loaded.status === 'ready') {
          let carryForward: GuestSimulationEngineSnapshot | undefined;
          try { carryForward = decodeGuestSimulationReplayState(loaded.bytes).snapshot as GuestSimulationEngineSnapshot; }
          catch { carryForward = undefined; }
          return client.restore(loaded.bytes, initialRevision)
            .then((restored) => guestCheckpointMatchesOperatingWindow(restored, simulationWindow, initialRevision)
              ? restored : initialize(carryForward)).catch(() => initialize(carryForward));
        }
        if (loaded.status === 'corrupt') throw new Error(`Guest checkpoint is corrupt: ${loaded.error}`);
        return initialize();
      }) : initialize();
    initializingRef.current = pending;
    void pending.then((ready) => {
      if (clientRef.current !== client) return;
      updateReadyState(client, ready); setStatus('ready');
      setMessage(`${ready.metrics.population.toLocaleString()} individually simulated guests ready.`);
      void drainCompact(client);
    }, (error: unknown) => {
      if (clientRef.current !== client) return;
      setStatus('error'); setMessage(error instanceof Error ? error.message : 'Guest simulation failed to start.');
    });
    return () => client.dispose();
  }, [drainCompact, options.clock.season, options.gameSaveUpdatedAt, options.portal?.id,
    options.saveKey, simulationStartTick, simulationWindow, skippedPastOperatingWindow,
    updateReadyState]);

  useEffect(() => {
    const client = clientRef.current;
    const network = guestNetworkRef.current;
    if (!client || !network || status !== 'ready' || options.clock.season !== 'winter'
      || revision === appliedTopologyRevisionRef.current) return;
    const prior = topologyUpdateInFlightRef.current ?? environmentUpdateInFlightRef.current
      ?? advanceInFlightRef.current ?? Promise.resolve();
    const pending = prior.then(async () => {
      if (clientRef.current !== client) return;
      const response = await client.updateTopology({ network, topologyRevision: revision });
      if (clientRef.current !== client) return;
      topologyRevisionRef.current = response.migration.topologyRevision;
      appliedTopologyRevisionRef.current = revision;
      committedSecondRef.current = response.committedSecond;
      setCommittedSecond(response.committedSecond);
      publishRenderFrameRef.current?.(response.renderFrame,
        renderEdgePathsRef.current, portalLngLatRef.current);
      await refreshRichSnapshot(client);
      if (desiredTopologyRevisionRef.current === appliedTopologyRevisionRef.current) void drainCompact(client);
    }).catch((error: unknown) => {
      if (clientRef.current !== client) return;
      setStatus('error');
      setMessage(error instanceof Error ? error.message : 'Guest topology update failed.');
    }).finally(() => {
      if (topologyUpdateInFlightRef.current === pending) topologyUpdateInFlightRef.current = null;
    });
    topologyUpdateInFlightRef.current = pending;
  }, [drainCompact, options.clock.season, refreshRichSnapshot, revision, status]);

  useEffect(() => {
    const client = clientRef.current;
    if (!client || status !== 'ready' || options.clock.season !== 'winter') return;
    const snowChanged = lastConditionSnowGridRef.current !== options.snowGrid;
    const nextOperationsRevision = options.operationsRevision ?? operationsRevisionRef.current;
    const nextWeatherRevision = options.weatherRevision ?? (snowChanged
      ? weatherRevisionRef.current + 1 : weatherRevisionRef.current);
    if (!snowChanged && nextOperationsRevision === operationsRevisionRef.current
      && nextWeatherRevision === weatherRevisionRef.current) return;
    const effectiveSecond = Math.max(committedSecondRef.current + 1,
      Math.floor(currentSecondRef.current) + 1);
    if (effectiveSecond >= simulationWindow.endTick) {
      lastConditionSnowGridRef.current = options.snowGrid;
      return;
    }
    const conditionSnapshot = conditionSnapshotFromSkiNetwork(networkRef.current, snowGridRef.current,
      { tick: effectiveSecond, revision: conditionRevisionRef.current + 1 });
    conditionRevisionRef.current += 1;
    // Record the source snapshot immediately to coalesce repeated React
    // renders, but do not publish revision numbers to advance requests until
    // the worker has acknowledged the matching environment update.
    lastConditionSnowGridRef.current = options.snowGrid;
    const priorTopology = topologyUpdateInFlightRef.current;
    const pending = (priorTopology ?? Promise.resolve()).then(async () => {
      if (clientRef.current !== client) return;
      await client.updateEnvironment({ effectiveSecond, topologyRevision: topologyRevisionRef.current,
        operationsRevision: nextOperationsRevision, weatherRevision: nextWeatherRevision,
        conditionSnapshot });
      if (clientRef.current !== client) return;
      operationsRevisionRef.current = nextOperationsRevision;
      weatherRevisionRef.current = nextWeatherRevision;
      void drainCompact(client);
    }).catch((error: unknown) => {
      // A replaced client owns the newer authoritative state; stale worker
      // responses must never overwrite it or surface an obsolete error.
      if (clientRef.current !== client) return;
      setStatus('error');
      setMessage(error instanceof Error ? error.message : 'Guest environment update failed.');
    }).finally(() => {
      if (environmentUpdateInFlightRef.current === pending) environmentUpdateInFlightRef.current = null;
    });
    environmentUpdateInFlightRef.current = pending;
  }, [options.clock.season, options.operationsRevision, options.snowGrid, options.weatherRevision,
    drainCompact, revision, simulationWindow.endTick, status]);

  useEffect(() => {
    const client = clientRef.current;
    if (!client || options.clock.season !== 'winter') return;
    targetSecondRef.current = Math.max(targetSecondRef.current, currentSecond);
    void drainCompact(client);
  }, [currentSecond, drainCompact, options.clock.season]);

  const snapshotBarrier = useCallback(async () => {
    const client = clientRef.current;
    if (!client) return null;
    targetSecondRef.current = Math.max(targetSecondRef.current, currentSecondRef.current);
    await drainCompact(client);
    return clientRef.current === client ? client.snapshot() : null;
  }, [drainCompact]);

  const persistBarrier = useCallback(async (saveKey: string, gameSaveUpdatedAt: string, expectedSecond?: number) => {
    const targetSecond = expectedSecond ?? currentSecondRef.current;
    if (!Number.isFinite(targetSecond) || targetSecond < 0) {
      return { ok: false as const, error: 'Cannot save: the committed simulation timestamp is invalid.' };
    }
    // Snow and the authoritative clock are committed by the game-simulation
    // coordinator before this adapter observes the new clock.  A caller with
    // a future timestamp would otherwise make the guest sidecar race ahead of
    // those two state sources.
    if (expectedSecond !== undefined && expectedSecond > currentSecondRef.current + Number.EPSILON) {
      return { ok: false as const, error: 'Cannot save: the requested timestamp is ahead of the committed game clock.' };
    }
    if (!isDesktop) return { ok: true as const, committedSecond: targetSecond };
    const client = clientRef.current;
    if (!client) return { ok: true as const, committedSecond: targetSecond };
    try {
      // A request already in flight may have been aimed past the save's
      // timestamp.  Let it finish, then refuse a mixed-time save rather than
      // pairing an old GameSave with a newer guest checkpoint.
      if (advanceInFlightRef.current) await advanceInFlightRef.current;
      if (clientRef.current !== client) return { ok: false as const, error: 'Guest simulation was replaced during save.' };
      if (committedSecondRef.current > targetSecond + Number.EPSILON) {
        return { ok: false as const,
          error: 'Simulation advanced while saving; retry so the clock and guest checkpoint share one timestamp.' };
      }
      targetSecondRef.current = Math.max(targetSecondRef.current, targetSecond);
      await drainCompact(client, targetSecond);
      if (clientRef.current !== client) return { ok: false as const, error: 'Guest simulation was replaced during save.' };
      const checkpoint = await client.checkpoint();
      const committedSecond = checkpoint.committedSecond ?? checkpoint.snapshot.tick;
      if (!Number.isFinite(committedSecond) || Math.abs(committedSecond - targetSecond) > Number.EPSILON) {
        return { ok: false as const,
          error: 'Guest checkpoint did not reach the save timestamp; retry the save.' };
      }
      const saved = await saveGuestSimulationCheckpoint(saveKey, gameSaveUpdatedAt, checkpoint.bytes);
      return saved.ok ? { ok: true as const, committedSecond } : saved;
    } catch (error) {
      return { ok: false as const, error: error instanceof Error ? error.message : 'Guest checkpoint failed.' };
    }
  }, [drainCompact]);

  return { status, message, snapshot, points, weeklyGuestWeighting: weeklyWeighting,
    outcomeWeight: weeklyWeighting.outcomeWeight, committedSecond, backlogSeconds,
    snapshotBarrier, persistBarrier };
}
