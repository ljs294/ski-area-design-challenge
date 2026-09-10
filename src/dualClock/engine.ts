import type { NetworkEdge } from '../network';
import { createConditionSnapshot, scoreConditionAwareRoute } from '../guestSimulation/conditions';
import { constantElasticityDemandMultiplier } from '../guestSimulation/probability';
import { SNOW_SURFACE_QUALITY } from '../guestSimulation/snowSurfaceQuality';
import { createNeedState, advanceNeedState, relieveNeedState } from '../guestSimulation/needs';
import { createNaturalSnowStepper } from '../snowSimulation';
import type { SnowGrid } from '../types/snow';
import { weatherLocalParts, localWeatherDateKey, weatherInstantForLocal } from '../weather/localTime';
import { createDualClock, microRate, winterBounds } from './clock';
import { prepareFootprint, prepareTrailCoverage, prepareRoute, routePosition, stableHash, type PreparedRoute, type TrailFootprint } from './geometry';
import { applyTrafficWear, refreshTrailSignals } from './wear';
import { addFreshSnow } from './snowAdd';
import { deriveGuestSpeedZ, representativeGuestDuration } from './guestMovement';
import { drainRepresentativeEvents } from './representativeEvents';
import { enqueueTrailGuest, hasTrailEntrance, reconcileTrailQueues as reconcileTrailQueueState, releaseTrailGuest as releaseTrailGuestState,
  trailEntrySpacing } from './trailQueue';
import { validateDualCheckpoint } from './validation';
export { validateDualCheckpoint } from './validation';
import { DEFAULT_DUAL_CONFIG, type DualInitialization, type DualCheckpoint, type DualPublication,
  type ResortSimulationInput, type RepresentativeGuest, type DualSpeed,
  type MacroSecond, type MicroSecond, type OperationalSignal, type AdvanceRequest, type TransitRoute, type MacroCohort, type SnowAddResult,
  DEFAULT_GUEST_MOVEMENT } from './model';

const EMPTY_FLOW = () => ({ admitted: 0, active: 0, departed: 0, turnedAway: 0, ticketRevenueCents: 0,
  amenityRevenueCents: 0, completedRuns: 0, queues: {}, trails: {} });
const ARRIVAL_WEIGHTS = [0.38, 0.3, 0.16, 0.09, 0.04, 0.02, 0.01, 0];
const clamp = (n: number) => Math.max(0, Math.min(1, n));
/** Domain runtime: all authoritative mutations happen here, inside the owning worker. */
export class DualClockEngine {
  state: DualCheckpoint;
  snow: SnowGrid | null;
  exposure: Float64Array;
  geometryRevision = 0;
  private resort: ResortSimulationInput;
  private edges = new Map<string, NetworkEdge>();
  private outgoing = new Map<string, NetworkEdge[]>();
  private routes = new Map<string, PreparedRoute>();
  private footprints = new Map<string, TrailFootprint>();
  private trailCoverage = new Map<string, TrailFootprint>();
  private routeCache = new Map<string, string[] | null>();
  private quality = new Map<string, { coverage: number; quality: number; depth: number }>();
  private scoreCache = new Map<string, number>();
  /** Lifts whose top can reach the entrance through an eligible ski itinerary. */
  private viableLiftIds = new Set<string>();
  private snowStepper: ReturnType<typeof createNaturalSnowStepper> | null = null;
  private weatherByHour = new Map<number, DualInitialization['weather'][number]>();
  private closedForDay = false;

  constructor(input: DualInitialization) {
    this.resort = input.resort;
    if (input.checkpoint) {
      validateDualCheckpoint(input.checkpoint);
      this.state = structuredClone(input.checkpoint);
      this.state.clock.paused = true;
      if (this.state.advance?.state === 'running') this.state.advance.state = 'suspended';
    } else {
      const config = structuredClone(input.config ?? DEFAULT_DUAL_CONFIG);
      this.state = { version: 1, seed: input.seed, config, clock: createDualClock(input.at, input.timezone, config), resortRevision: input.resort.revision, nextTicketPriceCents: input.resort.ticketPriceCents,
        snow: null, flow: EMPTY_FLOW(), cohorts: [], guests: [], dailyLedgers: [], transitRoutes: [], snowLoss: { trafficM3: 0, traceCutoffM3: 0 }, nextCohortId: 1, nextGuestId: 1,
        lastAdmissionMinute: Math.floor(Date.parse(input.at) / 60000) - 1,
        lastWeatherHour: Math.floor(Date.parse(input.at) / 3600000), admissionResidual: 0,
        serviceCredits: {}, amenityCredits: {}, amenityInventory: {}, dailyPrices: {}, signals: [], history: [],
        selectedGuestId: null, autoTrack: false, advance: null, portal: input.resort.portal, trailQueues: {} };
    }
    this.normalizeGuestMovement();
    this.normalizeDailyLiftBoardings(!input.checkpoint);
    const savedSnow = this.state.snow;
    this.snow = savedSnow ? { bounds: savedSnow.bounds, width: savedSnow.width, height: savedSnow.height,
      depthM: Float32Array.from(savedSnow.depthM), surface: Uint8Array.from(savedSnow.surface) }
      : input.snow ? { ...input.snow, depthM: input.snow.depthM.slice(), surface: input.snow.surface.slice() } : null;
    this.exposure = savedSnow ? Float64Array.from(savedSnow.exposure) : new Float64Array(this.snow?.depthM.length ?? 0);
    this.state.snow = null; // Large arrays are encoded only at explicit save barriers.
    this.setWeather(input.weather);
    this.setResort({ ...input.resort, portal: input.resort.portal ?? this.state.portal,
      ticketPriceCents: input.checkpoint ? this.state.nextTicketPriceCents : input.resort.ticketPriceCents });
    const hour = weatherLocalParts(this.state.clock.at, this.state.clock.timezone).hour;
    this.closedForDay = this.state.clock.season !== 'winter' || hour < this.state.config.openingHour || hour >= this.state.config.closingHour;
    if (!input.checkpoint && !this.closedForDay && Date.parse(input.at) % 60000 === 0) this.macroMinute(Date.parse(input.at));
    if (input.terrain) this.setTerrain(input.terrain);
  }
  setTerrain(terrain: NonNullable<DualInitialization['terrain']>): void {
    if (!this.snow) return;
    if (Object.entries(this.snow.bounds).some(([key, value]) => value !== terrain.bounds?.[key as keyof SnowGrid['bounds']])) {
      throw new Error('The simulation checkpoint and terrain bounds do not match. Reload the matching terrain package.');
    }
    this.snowStepper = createNaturalSnowStepper(terrain, this.snow);
  }
  setWeather(hours: DualInitialization['weather']): void {
    const earliest = this.state.lastWeatherHour - 1;
    for (const key of this.weatherByHour.keys()) if (key < earliest) this.weatherByHour.delete(key);
    for (const hour of hours) { const key = Math.floor(Date.parse(hour.at) / 3600000); if (key >= earliest) this.weatherByHour.set(key, hour); }
  }
  setInitialTimezone(timezone: string): void {
    if (this.state.clock.macroSecond !== 0 || timezone === this.state.clock.timezone) return;
    const local = weatherLocalParts(this.state.clock.at, this.state.clock.timezone);
    const at = weatherInstantForLocal(local, timezone);
    this.state.clock = { ...createDualClock(at, timezone, this.state.config), speed: this.state.clock.speed, revision: this.state.clock.revision + 1 };
    this.state.lastAdmissionMinute = Math.floor(Date.parse(at) / 60000) - 1;
    this.state.lastWeatherHour = Math.floor(Date.parse(at) / 3600000);
    this.normalizeDailyLiftBoardings(false);
  }
  setResort(resort: ResortSimulationInput): void {
    this.geometryRevision++;
    const incoming = new Map(resort.edges.map(edge => [edge.id, edge]));
    // Freeze only journeys already in progress. New admissions use the edited network immediately.
    for (const edge of this.edges.values()) {
      const next = incoming.get(edge.id);
      if (next && JSON.stringify(next.path) === JSON.stringify(edge.path)) continue;
      const guests = this.state.guests.filter(g => g.edgeId === edge.id);
      const cohorts = this.state.cohorts.filter(c => c.edgeId === edge.id && c.status === 'travel');
      if (!guests.length && !cohorts.length) continue;
      const id = `${edge.id}@${this.state.resortRevision}`, route = this.routes.get(edge.id)!, fp = this.footprints.get(edge.id);
      this.state.transitRoutes.push({ id, kind: edge.kind, from: edge.from, to: edge.to, path: edge.path,
        lengthM: edge.lengthM, travelTimeS: edge.travelTimeS, trailId: edge.kind === 'trail' ? edge.trailId : '',
        trailName: edge.kind === 'trail' ? edge.trailName : '', liftName: edge.kind === 'lift' ? edge.liftName : '',
        lanes: route.lanes.map(lane => lane.map(p => [p[0], p[1]])), distances: Array.from(route.distances),
        ...(fp ? { footprint: { indices: Array.from(fp.indices), areas: Array.from(fp.areas), areaM2: fp.areaM2, slopeDeg: fp.slopeDeg } } : {}) });
      for (const guest of guests) { guest.laneEdgeId = edge.id; guest.edgeId = id; }
      for (const cohort of cohorts) cohort.edgeId = id;
    }
    this.resort = resort; this.state.portal = resort.portal; this.state.resortRevision = resort.revision;
    this.state.nextTicketPriceCents = resort.ticketPriceCents;
    this.edges = new Map(resort.edges.map(edge => [edge.id, edge]));
    this.outgoing.clear(); this.routes.clear(); this.footprints.clear(); this.trailCoverage.clear(); this.routeCache.clear();
    const trails = new Map(resort.trails.map(trail => [trail.id, trail]));
    for (const edge of resort.edges) {
      const list = this.outgoing.get(edge.from) ?? []; list.push(edge); this.outgoing.set(edge.from, list);
      const trail = edge.kind === 'trail' ? trails.get(edge.trailId) : undefined;
      this.routes.set(edge.id, prepareRoute(edge, trail));
      if (this.snow) { const fp = prepareFootprint(edge, trail, this.snow); if (fp) this.footprints.set(edge.id, fp); }
      if (this.snow && trail && !this.trailCoverage.has(trail.id)) {
        const coverage = prepareTrailCoverage(edge, trail, this.snow); if (coverage) this.trailCoverage.set(trail.id, coverage);
      }
    }
    for (const list of this.outgoing.values()) list.sort((a, b) => a.id.localeCompare(b.id));
    for (const amenity of resort.amenities) this.state.amenityInventory[amenity.id] ??= amenity.inventory;
    for (const transit of this.state.transitRoutes) {
      this.routes.set(transit.id, { lanes: transit.lanes, distances: Float64Array.from(transit.distances), length: transit.distances.at(-1) ?? 0 });
      if (transit.footprint) this.footprints.set(transit.id, { ...transit.footprint, edgeId: transit.id, trailId: transit.trailId,
        lengthM: transit.lengthM, indices: Uint32Array.from(transit.footprint.indices), areas: Float64Array.from(transit.footprint.areas) });
    }
    for (const cohort of this.state.cohorts) if (cohort.status === 'queue' && !this.edges.has(cohort.edgeId!)) {
      cohort.status = 'choosing'; cohort.edgeId = null;
    }
    this.refreshConditions();
    this.reconcileTrailQueues();
    this.releaseUnviableLiftQueues();
    this.refreshLiftTelemetry(this.operatingAt(Date.parse(this.state.clock.at)));
  }
  private journeyEdge(id: string): NetworkEdge | TransitRoute | undefined {
    return this.edges.get(id) ?? this.state.transitRoutes.find(route => route.id === id);
  }
  setSpeed(speed: DualSpeed): void {
    this.state.clock.speed = speed;
    if (this.state.advance?.state === 'running') this.state.advance.state = 'cancelled';
    this.changePresentation(speed >= 8);
  }
  pause(): void {
    this.state.clock.paused = true;
    if (this.state.advance?.state === 'running') this.state.advance.state = 'suspended';
  }
  play(): void { if (this.state.clock.season === 'winter') this.state.clock.paused = false; }
  beginAdvance(request: AdvanceRequest): void {
    if (!Number.isFinite(Date.parse(request.target)) || Date.parse(request.target) <= Date.parse(this.state.clock.at)) throw new Error('Advance destination must be in the future.');
    this.state.advance = { request, startedAt: this.state.clock.at, fraction: 0, state: 'running', baseline: {
      admitted: this.state.flow.admitted, runs: this.state.flow.completedRuns,
      revenueCents: this.state.flow.ticketRevenueCents + this.state.flow.amenityRevenueCents } };
    this.state.clock.paused = false;
    if (request.destination === 'winter') {
      const bounds = winterBounds(request.target, this.state.clock.timezone, this.state.config.winterWeeks);
      this.state.clock.winterStart = bounds.start; this.state.clock.winterEnd = bounds.end;
    }
  }
  cancelAdvance(): void { if (this.state.advance) this.state.advance.state = 'cancelled'; this.pause(); }
  resumeAdvance(): void { if (this.state.advance?.state === 'suspended') { this.state.advance.state = 'running'; this.state.clock.paused = false; } }
  select(id: string | null, autoTrack = false): void {
    this.state.selectedGuestId = id; this.state.autoTrack = autoTrack;
    if (autoTrack) this.autoTrack();
  }
  follow(): void {
    if (this.state.advance?.state === 'running') this.state.advance.state = 'suspended';
    this.state.clock.speed = 1; this.state.clock.paused = false;
    this.changePresentation(false);
  }
  settlePresentation(): void { this.changePresentation(this.state.advance?.state === 'running' || this.state.clock.speed >= 8); }
  acknowledge(id: string): void { const signal = this.state.signals.find(s => s.id === id); if (signal) signal.acknowledged = true; }
  signal(signal: OperationalSignal): void {
    const previous = this.state.signals.find(s => s.id === signal.id);
    if (signal.resolved) {
      if (previous) { this.state.signals = this.state.signals.filter(s => s.id !== signal.id);
        this.state.history.push({ ...previous, resolved: true }); this.state.history = this.state.history.slice(-256); }
      return;
    }
    if (previous) { previous.message = signal.message; return; }
    this.state.signals.push(signal);
    if (signal.severity !== 'advisory') {
      if (this.state.advance?.state === 'running') this.state.advance.state = 'suspended';
      if (signal.severity === 'critical') this.pause(); else { this.state.clock.speed = 1; this.state.clock.paused = false; this.changePresentation(false); }
    }
  }
  private refreshConditions(): void {
    this.quality.clear();
    for (const [id, fp] of this.footprints) {
      let covered = 0, quality = 0, depth = 0;
      for (let j = 0; j < fp.indices.length; j++) {
        const i = fp.indices[j], area = fp.areas[j];
        if (this.snow!.depthM[i] >= 0.02) covered += area;
        quality += area * (SNOW_SURFACE_QUALITY[this.snow!.surface[i]] ?? 0);
        depth += area * this.snow!.depthM[i];
      }
      this.quality.set(id, { coverage: covered / Math.max(1, fp.areaM2), quality: quality / Math.max(1, fp.areaM2), depth: depth / Math.max(1, fp.areaM2) });
    }
    this.routeCache.clear();
    this.scoreCache.clear();
    this.refreshViableLifts();
    refreshTrailSignals(this.snow, this.trailCoverage.values(), this.resort.edges, this.state.signals,
      this.state.clock.at, this.state.config, signal => this.signal(signal));
  }
  private normalizeDailyLiftBoardings(newGame: boolean): void {
    const state = this.state, day = localWeatherDateKey(state.clock.at, state.clock.timezone), existing = state.dailyLiftBoardings;
    if (!existing) {
      state.dailyLiftBoardings = newGame
        ? { date: day, counts: {}, accuracy: 'complete' }
        : { date: day, counts: {}, accuracy: 'partial', trackingSince: state.clock.at };
      return;
    }
    if (existing.date !== day) {
      state.dailyLiftBoardings = { date: day, counts: {}, accuracy: 'complete' };
      return;
    }
    existing.counts ??= {};
    if (existing.accuracy === 'partial') existing.trackingSince ??= state.clock.at;
  }
  /** Normalize additive movement state while preserving all old deadlines. */
  private normalizeGuestMovement(): void {
    this.state.config.guestMovement ??= structuredClone(DEFAULT_GUEST_MOVEMENT);
    this.state.trailQueues ??= {};
    for (const queue of Object.values(this.state.trailQueues)) queue.entries ??= [];
  }
  private operatingAt(atMs: number): boolean {
    const hour = weatherLocalParts(atMs, this.state.clock.timezone).hour;
    return this.state.clock.season === 'winter' && atMs < Date.parse(this.state.clock.winterEnd)
      && hour >= this.state.config.openingHour && hour < this.state.config.closingHour;
  }
  private surfaceEligible(edge: NetworkEdge): boolean {
    return edge.kind !== 'lift' && edge.open && (this.quality.get(edge.id)?.coverage ?? 1) >= 0.2;
  }
  /** A route is a descent only if it contains a trail; paths alone do not qualify a lift. */
  private hasEligibleDescent(from: string, targets: ReadonlySet<string>): boolean {
    const visited = new Set<string>(), queue: { node: string; skied: boolean }[] = [{ node: from, skied: false }];
    visited.add(`${from}|0`);
    for (let cursor = 0; cursor < queue.length; cursor++) {
      const current = queue[cursor]!;
      for (const edge of this.outgoing.get(current.node) ?? []) {
        if (!this.surfaceEligible(edge)) continue;
        const skied = current.skied || edge.kind === 'trail';
        if (skied && targets.has(edge.to)) return true;
        const key = `${edge.to}|${skied ? 1 : 0}`;
        if (!visited.has(key)) { visited.add(key); queue.push({ node: edge.to, skied }); }
      }
    }
    return false;
  }
  private refreshViableLifts(): void {
    this.viableLiftIds.clear();
    const portal = this.resort.portal?.nodeId;
    if (!portal) return;
    const returnSafe = new Set([portal]);
    let changed = true;
    while (changed) {
      changed = false;
      for (const edge of this.resort.edges) {
        if (edge.kind !== 'lift' || !edge.open || this.viableLiftIds.has(edge.id)) continue;
        if (!this.hasEligibleDescent(edge.to, returnSafe)) continue;
        this.viableLiftIds.add(edge.id); returnSafe.add(edge.from); changed = true;
      }
    }
    for (const edge of this.resort.edges) if (edge.kind === 'lift' && edge.open) {
      const id = `no-descent:${edge.id}`, previous = this.state.signals.find(signal => signal.id === id);
      if (this.viableLiftIds.has(edge.id)) {
        if (previous) this.signal({ ...previous, resolved: true });
      } else this.signal({ id, entityId: edge.liftId, entityKind: 'lift', title: 'No viable return',
        message: 'Guests are held at the base because no snow-covered descent returns to the entrance.',
        severity: 'advisory', at: this.state.clock.at, acknowledged: false, resolved: false });
    }
  }
  private isLiftViable(edge: NetworkEdge): boolean { return edge.kind === 'lift' && this.viableLiftIds.has(edge.id); }
  private releaseUnviableLiftQueues(): void {
    for (const cohort of this.state.cohorts) if (cohort.status === 'queue') {
      const edge = cohort.edgeId ? this.edges.get(cohort.edgeId) : null;
      if (!edge || !this.isLiftViable(edge)) { cohort.status = 'choosing'; cohort.edgeId = null; }
    }
  }
  private reconcileTrailQueues(now: number = this.state.clock.microSecond, expireCooldown = true): void {
    const queues = this.state.trailQueues;
    if (!queues) return;
    reconcileTrailQueueState(queues, this.state.guests, now, trailEntrySpacing(this.state.config), {
      hasEntrance: queue => hasTrailEntrance(this.edges, queue, edge => this.surfaceEligible(edge)),
      isEligible: (guest, queue) => {
        const edge = guest.edgeId ? this.edges.get(guest.edgeId) : undefined;
        return edge?.kind === 'trail' && edge.trailId === queue.trailId && edge.from === queue.entryNode && this.surfaceEligible(edge);
      },
    }, expireCooldown);
  }
  private refreshLiftTelemetry(operating: boolean): void {
    this.normalizeDailyLiftBoardings(false);
    const daily = this.state.dailyLiftBoardings!;
    for (const edge of this.resort.edges) if (edge.kind === 'lift') {
      const ledger = this.state.flow.queues[edge.id] ??= { guests: 0, waitSeconds: 0, boarded: 0 };
      const viable = this.isLiftViable(edge);
      const available = operating && edge.open && viable && edge.capacityPph > 0;
      ledger.guests = this.state.cohorts.filter(cohort => cohort.status === 'queue' && cohort.edgeId === edge.id)
        .reduce((sum, cohort) => sum + cohort.count, 0);
      ledger.riders = this.state.cohorts.filter(cohort => cohort.status === 'travel' && cohort.edgeId &&
        (cohort.edgeId === edge.id || cohort.edgeId.startsWith(`${edge.id}@`))).reduce((sum, cohort) => sum + cohort.count, 0);
      ledger.waitSeconds = available ? ledger.guests / edge.capacityPph * 3600 : 0;
      ledger.serviceAvailable = available;
      ledger.dailyBoarded = daily.counts[edge.id] ?? 0;
      ledger.dailyAccuracy = daily.accuracy;
      if (daily.accuracy === 'partial') ledger.trackingSince = daily.trackingSince;
      else delete ledger.trackingSince;
      const unavailable = !edge.open || !operating ? 'closed' : !viable ? 'no-descent' : edge.capacityPph <= 0 ? 'capacity' : null;
      if (unavailable) ledger.serviceUnavailableReason = unavailable;
      else delete ledger.serviceUnavailableReason;
    }
  }
  addSnow(meters: number): SnowAddResult {
    if (!this.snow) throw new Error('Snow addition requires an initialized snow grid.');
    const before = this.snow, applied = addFreshSnow(before, meters);
    for (let index = 0; index < before.depthM.length; index++) if (applied.grid.depthM[index]! > before.depthM[index]!) this.exposure[index] = 0;
    this.snow = applied.grid;
    this.refreshConditions();
    this.releaseUnviableLiftQueues();
    this.refreshLiftTelemetry(this.operatingAt(Date.parse(this.state.clock.at)));
    return applied.result;
  }
  private route(from: string, target: string, exit = false): string[] | null {
    if (from === target) return [];
    const key = `${from}|${target}|${exit}`;
    if (this.routeCache.has(key)) return this.routeCache.get(key)!;
    const visited = new Set([from]), queue = [from], previous = new Map<string, NetworkEdge>();
    for (let cursor = 0; cursor < queue.length; cursor++) {
      for (const edge of this.outgoing.get(queue[cursor]) ?? []) {
        if (edge.kind === 'lift' || !edge.open || (!exit && (this.quality.get(edge.id)?.coverage ?? 1) < 0.2) || visited.has(edge.to)) continue;
        previous.set(edge.to, edge); visited.add(edge.to); queue.push(edge.to);
        if (edge.to === target) {
          const path: string[] = []; let node = target;
          while (node !== from) { const step = previous.get(node)!; path.push(step.id); node = step.from; }
          path.reverse(); this.routeCache.set(key, path); return path;
        }
      }
    }
    this.routeCache.set(key, null); return null;
  }
  private nextEdge(node: string, ability: number, identity: string, ordinal: number, leaving: boolean): NetworkEdge | null {
    const base = this.resort.portal?.nodeId;
    if (!base) return null;
    if (leaving) { const route = this.route(node, base, true); return route?.length ? this.edges.get(route[0]) ?? null : null; }
    const candidates = (this.outgoing.get(node) ?? []).filter(edge => edge.kind === 'lift'
      ? this.isLiftViable(edge) : this.surfaceEligible(edge));
    const useful = candidates.filter(edge => edge.kind === 'lift' || edge.kind === 'trail');
    if (!useful.length) {
      for (const lift of this.resort.edges) if (this.isLiftViable(lift)) {
        const route = this.route(node, lift.from); if (route?.length) return this.edges.get(route[0]) ?? null;
      }
      return null;
    }
    const scored = useful.map(edge => {
      const key = `${edge.id}|${Math.round(ability * 10)}`;
      const cached = this.scoreCache.get(key);
      if (cached !== undefined) return { edge, weight: cached };
      const q = this.quality.get(edge.id), baseDifficulty = edge.kind === 'trail'
        ? ({ green: 0.2, blue: 0.45, black: 0.7, red: 0.92 }[edge.difficulty]) : 0.1;
      const conditions = createConditionSnapshot({ edges: [{ edgeId: edge.id, baseDifficulty,
        coverage: q?.coverage ?? 1, snowQuality: q?.quality ?? 0.75, grooming: 0.5 }] });
      const score = scoreConditionAwareRoute(conditions, [edge.id], { ability, targetDifficulty: ability });
      const weight = Math.max(0.01, score.score); this.scoreCache.set(key, weight);
      return { edge, weight };
    });
    let value = stableHash(`${this.state.seed}|${identity}|${ordinal}|${node}`) / 4294967296 * scored.reduce((sum, item) => sum + item.weight, 0);
    for (const item of scored) { value -= item.weight; if (value <= 0) return item.edge; }
    return scored[scored.length - 1].edge;
  }
  private activity(guest: RepresentativeGuest, text: string): void {
    guest.thought = text; guest.history.push({ at: this.state.clock.at, text });
    const selected = this.state.guests.find(g => g.id === this.state.selectedGuestId);
    guest.history = guest.history.slice(-(selected?.groupId === guest.groupId ? 128 : 32));
  }
  private spawn(count: number, nodeId: string, day: string, forceSamples?: number): void {
    const selected = this.state.guests.find(g => g.id === this.state.selectedGuestId);
    const detailed = this.state.clock.speed < 8 && this.state.advance?.state !== 'running';
    if (!detailed && !this.state.autoTrack && !forceSamples) return;
    const sampleRate = Math.min(1, this.state.config.representativeLimit / Math.max(1, this.resort.dailyDemand));
    const available = this.state.config.representativeLimit - this.state.guests.filter(g => g.status !== 'departed').length;
    const samples = Math.min(available, forceSamples ?? Math.min(Math.ceil(count * sampleRate), detailed ? count : selected && selected.status !== 'departed' ? 0 : 4));
    for (let i = 0; i < samples; i++) {
      const ordinal = this.state.nextGuestId++, id = `representative-${ordinal}`;
      this.state.guests.push({ id, ordinal, groupId: `${day}:party-${Math.floor(ordinal / 4)}`, status: 'walking',
        nextPlan: 'Choose a lift', thought: 'Ready to explore the mountain.', satisfaction: 0.8, runs: 0,
        spendingCents: this.state.dailyPrices[day] ?? this.resort.ticketPriceCents, trackingBeganAt: this.state.clock.at, history: [],
        needs: createNeedState({ hunger: 0.2 + (stableHash(id) % 500) / 1000, thirst: 0.15 }), needsSecond: Math.floor(this.state.clock.microSecond), personalBudgetCents: 5000,
        edgeId: null, route: [], routeIndex: 0, nodeId, started: this.state.clock.microSecond, due: this.state.clock.microSecond,
        admissionDay: day, lastPosition: this.resort.portal ? [...this.resort.portal.lngLat] : undefined,
        ability: 0.2 + stableHash(id) / 4294967296 * 0.7,
        speedZ: deriveGuestSpeedZ(this.state.seed, id, this.state.config.guestMovement) });
    }
  }
  private changePresentation(aggregate: boolean): void {
    const state = this.state;
    if ((state.presentationMode === 'aggregate') === aggregate) return;
    const selected = state.guests.find(g => g.id === state.selectedGuestId);
    state.presentationMode = aggregate ? 'aggregate' : 'individual';
    if (aggregate) {
      state.guests = state.guests.filter(g => g.groupId === selected?.groupId);
      this.reconcileTrailQueues();
      return;
    }
    // Reconstruct while hidden from current cohort locations. No old visible marker is repositioned.
    for (const cohort of state.cohorts) this.trackCohort(cohort);
  }
  private trackCohort(cohort: MacroCohort, forceSamples?: number): void {
      const state = this.state;
      const begin = state.guests.length;
      this.spawn(cohort.count, cohort.nodeId, localWeatherDateKey(state.clock.at, state.clock.timezone), forceSamples);
      for (let i = begin; i < state.guests.length; i++) {
        const guest = state.guests[i], edge = cohort.edgeId ? this.journeyEdge(cohort.edgeId) : null;
        guest.groupId = `tracked-cohort-${cohort.id}`; guest.ability = cohort.ability;
        const position = edge?.path[0] ?? this.outgoing.get(cohort.nodeId)?.[0]?.path[0];
        if (position) guest.lastPosition = [...position];
        if (edge && cohort.status === 'travel') {
          const progress = clamp((Date.parse(state.clock.at) / 1000 - cohort.started) / Math.max(1e-9, cohort.due - cohort.started));
          const duration = edge.kind === 'lift' ? edge.travelTimeS : representativeGuestDuration(state.seed, guest, edge, state.config.guestMovement);
          guest.edgeId = edge.id; guest.started = state.clock.microSecond - progress * duration;
          guest.due = guest.started + duration; guest.status = edge.kind === 'trail' ? 'skiing' : edge.kind === 'lift' ? 'lift-ride' : 'walking';
          if (edge.kind === 'trail') guest.lastTrailId = edge.trailId;
        }
        guest.spendingCents = 0;
        this.activity(guest, 'Detailed tracking started from the current mountain flow. Earlier purchases and runs are not reconstructed.');
      }
  }
  private autoTrack(): void {
    const state = this.state, selected = state.guests.find(g => g.id === state.selectedGuestId);
    if (selected && selected.status !== 'departed') return;
    if (!state.guests.some(g => g.status !== 'departed') && state.cohorts.length) this.trackCohort(state.cohorts[0], Math.min(4, state.cohorts[0].count));
    state.selectedGuestId = state.guests.find(g => g.status !== 'departed')?.id ?? state.selectedGuestId;
  }
  private macroMinute(atMs: number, accounting = true): void {
    const state = this.state, local = weatherLocalParts(atMs, state.clock.timezone);
    const day = localWeatherDateKey(new Date(atMs).toISOString(), state.clock.timezone), minute = Math.floor(atMs / 60000);
    if (state.dailyLedgers.at(-1)?.date !== day) {
      state.dailyLedgers.push({ date: day, admissions: 0, ticketRevenueCents: 0, amenityRevenueCents: 0 });
      if (state.dailyLedgers.length > 366) state.dailyLedgers.shift();
    }
    this.normalizeDailyLiftBoardings(false);
    const daily = state.dailyLedgers[state.dailyLedgers.length - 1];
    const operating = this.operatingAt(atMs);
    this.closedForDay = !operating;
    if (accounting && minute > state.lastAdmissionMinute) {
      state.lastAdmissionMinute = minute;
      if (operating && this.resort.portal) {
        state.dailyPrices[day] ??= this.resort.ticketPriceCents;
        const weight = ARRIVAL_WEIGHTS[local.hour - state.config.openingHour] ?? 0;
        const weekday = new Date(Date.UTC(local.year, local.month - 1, local.day)).getUTCDay();
        const dailyDemand = this.resort.dailyDemandByWeekday?.[weekday] ?? this.resort.dailyDemand;
        state.admissionResidual += Math.min(50000, dailyDemand * constantElasticityDemandMultiplier(Math.max(1, state.dailyPrices[day]), 10000)) * weight / 60;
        const demand = Math.floor(state.admissionResidual + 1e-9); state.admissionResidual = Math.max(0, state.admissionResidual - demand);
        const count = Math.min(demand, this.resort.portal.capacityPerMinute);
        state.flow.turnedAway += demand - count;
        if (count > 0) {
          const id = state.nextCohortId++;
          const closing = Date.parse(weatherInstantForLocal({ ...local, hour: state.config.closingHour, minute: 0, second: 0 }, state.clock.timezone)) / 1000;
          state.cohorts.push({ id, admissionId: `${state.seed}:${this.resort.portal.id}:${minute}`, count, nodeId: this.resort.portal.nodeId, edgeId: null, due: atMs / 1000, started: atMs / 1000,
            status: 'choosing', leaveAt: Math.min(closing, atMs / 1000 + 4 * 3600 + stableHash(`${state.seed}|${id}`) % 7200), ability: 0.2 + (id % 4) * 0.2, runs: 0, budgetCents: 5000 });
          state.flow.admitted += count; state.flow.active += count;
          state.flow.ticketRevenueCents += count * state.dailyPrices[day];
          daily.admissions += count; daily.ticketRevenueCents += count * state.dailyPrices[day];
          this.spawn(count, this.resort.portal.nodeId, day);
        }
      }
    }
    const now = atMs / 1000, traffic = new Map<string, number>();
    for (const cohort of state.cohorts) {
      if (cohort.status === 'amenity' && cohort.due <= now) cohort.status = 'choosing';
      if (cohort.status === 'travel' && cohort.due <= now) {
        const edge = this.journeyEdge(cohort.edgeId!);
        if (edge) {
          cohort.nodeId = edge.to;
          if (edge.kind === 'trail') {
            traffic.set(edge.id, (traffic.get(edge.id) ?? 0) + cohort.count * edge.lengthM);
            const trail = state.flow.trails[edge.trailId] ??= { guests: 0, passages: 0 };
            trail.passages += cohort.count;
            if (!(this.outgoing.get(edge.to) ?? []).some(next => next.kind === 'trail' && next.trailId === edge.trailId)) {
              state.flow.completedRuns += cohort.count; cohort.runs++;
            }
          }
        }
        cohort.edgeId = null; cohort.status = 'choosing';
      }
      if (cohort.status !== 'choosing') continue;
      const leaving = !operating || now >= cohort.leaveAt;
      if (leaving && cohort.nodeId === this.resort.portal?.nodeId) {
        state.flow.departed += cohort.count; state.flow.active -= cohort.count; cohort.count = 0; continue;
      }
      const edge = this.nextEdge(cohort.nodeId, cohort.ability, `cohort-${cohort.id}`, cohort.runs, leaving);
      const strandedId = `unreachable:${cohort.nodeId}`;
      if (!edge) {
        if (leaving) this.signal({ id: strandedId, entityId: cohort.nodeId, entityKind: 'resort', title: 'Exit route unavailable',
          message: 'Guests cannot reach the entrance from this junction. Restore an exit route before resuming.',
          severity: 'critical', at: state.clock.at, acknowledged: false, resolved: false });
        continue;
      }
      const stranded = state.signals.find(signal => signal.id === strandedId);
      if (stranded) this.signal({ ...stranded, resolved: true });
      cohort.edgeId = edge.id; cohort.started = now;
      if (edge.kind === 'lift') { cohort.status = 'queue'; cohort.due = now; }
      else { cohort.status = 'travel'; cohort.due = now + Math.max(1, edge.travelTimeS); }
    }
    if (operating && accounting) this.serveAmenities(now, local.hour, minute);
    // FIFO service credits are physical passengers, not representative sprite counts.
    this.releaseUnviableLiftQueues();
    for (const edge of this.resort.edges) if (edge.kind === 'lift') {
      const queued = state.cohorts.filter(c => c.status === 'queue' && c.edgeId === edge.id).sort((a, b) => a.due - b.due || a.id - b.id);
      const open = operating && edge.open && this.isLiftViable(edge) && edge.capacityPph > 0;
      let credit = open ? (state.serviceCredits[edge.id] ?? 0) + (accounting ? edge.capacityPph / 60 : 0) : 0;
      const ledger = state.flow.queues[edge.id] ??= { guests: 0, waitSeconds: 0, boarded: 0 };
      for (const cohort of queued) {
        if (!open || !this.isLiftViable(edge)) { cohort.status = 'choosing'; cohort.edgeId = null; continue; }
        const boarded = Math.min(cohort.count, Math.floor(credit));
        if (!boarded) break;
        credit -= boarded; ledger.boarded += boarded;
        state.dailyLiftBoardings!.counts[edge.id] = (state.dailyLiftBoardings!.counts[edge.id] ?? 0) + boarded;
        if (boarded < cohort.count) {
          cohort.count -= boarded;
          state.cohorts.push({ ...cohort, id: state.nextCohortId++, count: boarded, status: 'travel', started: now, due: now + edge.rideTimeS });
        } else { cohort.status = 'travel'; cohort.started = now; cohort.due = now + edge.rideTimeS; }
      }
      state.serviceCredits[edge.id] = credit % 1; // Unused past seats cannot be banked.
    }
    state.cohorts = state.cohorts.filter(c => c.count > 0);
    for (const trail of Object.values(state.flow.trails)) trail.guests = 0;
    for (const cohort of state.cohorts) {
      const edge = cohort.edgeId ? this.edges.get(cohort.edgeId) : null;
      if (edge?.kind === 'trail' && cohort.status === 'travel') (state.flow.trails[edge.trailId] ??= { guests: 0, passages: 0 }).guests += cohort.count;
    }
    if (this.snow && traffic.size) {
      for (const [id, distance] of traffic) {
        const fp = this.footprints.get(id); if (!fp) continue;
        const loss = applyTrafficWear(this.snow, this.exposure, fp, distance, state.config);
        state.snowLoss.trafficM3 += loss.trafficLossM3; state.snowLoss.traceCutoffM3 += loss.cutoffLossM3;
      }
      this.refreshConditions();
    }
    refreshTrailSignals(this.snow, this.trailCoverage.values(), this.resort.edges, this.state.signals,
      new Date(atMs).toISOString(), this.state.config, signal => this.signal(signal));
    this.refreshLiftTelemetry(operating);
    // Only current-day price is needed after all admissions for older days have finished.
    state.dailyPrices = Object.fromEntries(Object.entries(state.dailyPrices).filter(([key]) => key === day));
  }
  private serveAmenities(now: number, hour: number, minute: number): void {
    const state = this.state;
    for (const amenity of this.resort.amenities) {
      if (hour < amenity.opens || hour >= amenity.closes) continue;
      const curve = hour >= 11 && hour <= 13 ? 0.2 : 0.04;
      const expected = state.flow.active * curve / 60 * (0.9 + (stableHash(`${state.seed}|${minute}|${amenity.id}`) % 201) / 1000);
      const credit = (state.amenityCredits[amenity.id] ?? 0) + Math.min(expected, amenity.capacityPerHour / 60);
      let available = Math.min(Math.floor(credit), state.amenityInventory[amenity.id] ?? 0), sales = 0;
      // Guests must already have travelled to this facility's network entrance.
      const eligible = state.cohorts.filter(c => (c.status === 'queue' || c.status === 'choosing') && c.nodeId === amenity.nodeId
        && c.budgetCents >= amenity.priceCents && c.leaveAt > now).sort((a, b) => a.id - b.id);
      for (const cohort of eligible) {
        const count = Math.min(available, cohort.count); if (!count) break;
        available -= count; sales += count;
        const service = { ...cohort, count, edgeId: null, status: 'amenity' as const,
          started: now, due: now + amenity.accessSeconds + amenity.serviceSeconds, budgetCents: cohort.budgetCents - amenity.priceCents };
        if (count < cohort.count) { cohort.count -= count; state.cohorts.push({ ...service, id: state.nextCohortId++ }); }
        else Object.assign(cohort, service);
      }
      state.amenityCredits[amenity.id] = credit % 1; state.amenityInventory[amenity.id] -= sales;
      state.flow.amenityRevenueCents += sales * amenity.priceCents;
      state.dailyLedgers[state.dailyLedgers.length - 1].amenityRevenueCents += sales * amenity.priceCents;
    }
  }
  /**
   * Chronological representative processing. Queue releases have lower
   * priority than movement completions at the same instant, so every arrival
   * at an entrance is ordered before a release scheduled for that instant.
   */
  private microAdvanceChronological(to: number, headless: boolean): void {
    const state = this.state, selected = state.guests.find(g => g.id === state.selectedGuestId);
    const visible = (guest: RepresentativeGuest) => !(headless || state.clock.speed >= 8) || guest.groupId === selected?.groupId;
    this.reconcileTrailQueues(state.clock.microSecond, false);
    drainRepresentativeEvents(state.guests, to, visible, (guest, due) => this.advanceRepresentative(guest, due));
    this.reconcileTrailQueues(to);
    if (state.autoTrack) this.autoTrack();
    state.guests = state.guests.filter(g => g.status !== 'departed' || g.id === state.selectedGuestId);
    this.reconcileTrailQueues(to);
    const used = new Set([...state.guests.map(g => g.edgeId), ...state.cohorts.map(c => c.edgeId)]);
    state.transitRoutes = state.transitRoutes.filter(route => {
      if (used.has(route.id)) return true;
      this.routes.delete(route.id); this.footprints.delete(route.id); this.geometryRevision++; return false;
    });
  }
  /** Process one representative event; the caller orders all guests globally. */
  private advanceRepresentative(guest: RepresentativeGuest, time: number): void {
    const state = this.state;
    const needsSecond = Math.floor(time);
    if (needsSecond > guest.needsSecond) {
      guest.needs = advanceNeedState(guest.needs, needsSecond - guest.needsSecond); guest.needsSecond = needsSecond;
    }
    if (guest.status === 'trail-queue') {
      releaseTrailGuestState(guest, state.trailQueues, this.edges, time, trailEntrySpacing(state.config),
        edge => this.surfaceEligible(edge), (representative, edge) => representativeGuestDuration(state.seed, representative, edge, state.config.guestMovement));
      return;
    }
    const completedEdge = guest.edgeId ? this.journeyEdge(guest.edgeId) : undefined;
    if (completedEdge) {
      guest.nodeId = completedEdge.to;
      guest.lastPosition = [...completedEdge.path[completedEdge.path.length - 1]];
      if (completedEdge.kind === 'trail') {
        guest.lastTrailId = completedEdge.trailId;
        if (!(this.outgoing.get(completedEdge.to) ?? []).some(e => e.kind === 'trail' && e.trailId === completedEdge.trailId)) {
          guest.runs++; guest.satisfaction = clamp(guest.satisfaction * 0.9 + (this.quality.get(completedEdge.id)?.quality ?? 0.8) * 0.1);
          this.activity(guest, `Finished ${completedEdge.trailName}.`);
        }
      }
      else if (completedEdge.kind === 'path') {
        guest.lastTrailId = guest.transferTrailContinuationId;
        guest.transferTrailContinuationId = undefined;
      } else guest.lastTrailId = undefined;
    }
    guest.edgeId = null;
    delete guest.laneEdgeId;
    if (guest.status === 'resting') {
      const service = this.resort.amenities.find(a => a.id === guest.amenityId);
      if (service) guest.needs = relieveNeedState(guest.needs, service.relief);
      else guest.needs = relieveNeedState(guest.needs, { fatigue: 0.2 });
      delete guest.amenityId; this.activity(guest, 'Feeling rested and ready for another run.');
    }
    const leaving = this.closedForDay || guest.admissionDay !== localWeatherDateKey(state.clock.at, state.clock.timezone);
    if (leaving && guest.nodeId === this.resort.portal?.nodeId) {
      guest.status = 'departed'; guest.completedAt = state.clock.at; guest.nextPlan = 'Visit completed';
      this.activity(guest, 'Heading home after a day on the mountain.');
      return;
    }
    const localHour = weatherLocalParts(state.clock.at, state.clock.timezone).hour;
    const amenity = !leaving && this.resort.amenities.find(a => a.nodeId === guest.nodeId && localHour >= a.opens && localHour < a.closes
      && guest.needs[a.need] >= 0.5 && guest.personalBudgetCents >= a.priceCents && (state.amenityInventory[a.id] ?? 0) > 0);
    if (amenity) {
      guest.personalBudgetCents -= amenity.priceCents; guest.spendingCents += amenity.priceCents;
      guest.amenityId = amenity.id; guest.status = 'resting'; guest.started = time; guest.due = time + amenity.accessSeconds + amenity.serviceSeconds;
      guest.lastTrailId = undefined;
      guest.nextPlan = 'Finish at the café, then return to the slopes'; this.activity(guest, `${amenity.label} at the base café.`);
      return;
    }
    if (!leaving && guest.runs > 0 && guest.runs % 3 === 0 && guest.lastRestRun !== guest.runs) {
      guest.lastRestRun = guest.runs;
      guest.status = 'resting'; guest.started = time; guest.due = time + 60; guest.nextPlan = 'Rest, then choose another run';
      guest.lastTrailId = undefined;
      this.activity(guest, 'Taking a short break.');
      return;
    }
    const edge = this.nextEdge(guest.nodeId, guest.ability, guest.groupId, guest.runs, leaving);
    if (!edge) {
      guest.status = 'walking'; guest.started = time; guest.due = time + 10;
      guest.lastTrailId = undefined;
      guest.nextPlan = leaving ? 'Find a route to the exit' : 'Wait for a suitable route';
      return;
    }
    const from = guest.lastPosition, destination = edge.path[0];
    if (from && destination) {
      const distance = Math.hypot((destination[0] - from[0]) * 111320 * Math.cos(from[1] * Math.PI / 180), (destination[1] - from[1]) * 111320);
      if (distance > 0.1) {
        const id = `transfer:${from.join(',')}:${destination.join(',')}`;
        const continuation = edge.kind === 'trail' && guest.lastTrailId === edge.trailId ? edge.trailId : undefined;
        if (!this.routes.has(id)) {
          const path: [number, number][] = [[...from], [...destination]];
          state.transitRoutes.push({ id, kind: 'path', from: guest.nodeId, to: guest.nodeId, path,
            lengthM: distance, travelTimeS: distance / 1.4, trailId: '', trailName: '', liftName: '', lanes: [path], distances: [0, distance] });
          this.routes.set(id, { lanes: [path], distances: Float64Array.of(0, distance), length: distance }); this.geometryRevision++;
        }
        const transfer = this.state.transitRoutes.find(route => route.id === id)!;
        guest.edgeId = id; guest.started = time; guest.due = time + representativeGuestDuration(state.seed, guest, transfer, state.config.guestMovement);
        guest.status = 'walking'; guest.nextPlan = 'Walk across the junction'; guest.transferTrailContinuationId = continuation;
        return;
      }
    }
    if (edge.kind === 'lift' && guest.status !== 'lift-queue') {
      guest.status = 'lift-queue'; guest.started = time;
      guest.lastTrailId = undefined;
      // Lift departure slots remain aggregate-compatible; trail entry uses its own FIFO clock.
      guest.due = Math.ceil((time + 1) / 6) * 6 + (guest.ordinal % 4) * 6;
      guest.nextPlan = `Ride ${edge.liftName}`; this.activity(guest, `Waiting for ${edge.liftName}.`);
      return;
    }
    if (edge.kind === 'lift' && !this.isLiftViable(edge)) {
      guest.status = 'walking'; guest.started = time; guest.due = time + 10;
      guest.nextPlan = 'Wait for a suitable route';
      return;
    }
    if (edge.kind === 'trail' && guest.lastTrailId !== edge.trailId) {
      const trailName = enqueueTrailGuest(state.trailQueues ??= {}, guest, edge, time, trailEntrySpacing(state.config));
      if (trailName) this.activity(guest, `Waiting to enter ${trailName}.`);
      return;
    }
    guest.edgeId = edge.id; guest.started = time;
    guest.due = time + (edge.kind === 'lift' ? Math.max(1, edge.travelTimeS) : representativeGuestDuration(state.seed, guest, edge, state.config.guestMovement));
    guest.status = edge.kind === 'lift' ? 'lift-ride' : edge.kind === 'trail' ? 'skiing' : 'walking';
    if (edge.kind === 'trail') guest.lastTrailId = edge.trailId;
    else guest.lastTrailId = undefined;
    guest.nextPlan = leaving ? 'Finish and return to the entrance' : edge.kind === 'lift' ? 'Choose a run at the top' : 'Reach the next junction';
  }
  /** Resumable, canonical macro boundaries. Uncommitted weather tiles stay isolated. */
  *advanceTo(targetMs: number, headless = false): Generator<void, void> {
    targetMs = Math.floor(targetMs);
    const state = this.state;
    this.changePresentation(headless || state.clock.speed >= 8);
    while (Date.parse(state.clock.at) < targetMs) {
      const wasPaused = state.clock.paused;
      const before = Date.parse(state.clock.at);
      let next = Math.min(targetMs, (Math.floor(before / 60000) + 1) * 60000);
      // Travel and service finish at their own deadlines, between fixed accounting intervals.
      // Round upward only to the clock's millisecond precision, never to a whole minute.
      let eventBoundary = Infinity;
      for (const cohort of state.cohorts) if (cohort.status === 'travel' || cohort.status === 'amenity') {
        const due = Math.ceil(cohort.due * 1000);
        if (due > before) eventBoundary = Math.min(eventBoundary, due);
      }
      next = Math.min(next, eventBoundary);
      const end = Date.parse(state.clock.winterEnd);
      if (state.clock.season === 'winter' && before < end) next = Math.min(next, end);
      const hour = Math.floor(next / 3600000);
      let stagedSnow: SnowGrid | null = null, stagedExposure: Float64Array | null = null;
      if (hour > state.lastWeatherHour && this.snowStepper && this.snow) {
        const weather = this.weatherByHour.get(hour - 1);
        if (!weather) throw new Error(`Prepared weather is missing for ${new Date((hour - 1) * 3600000).toISOString()}.`);
        stagedExposure = this.exposure.slice();
        stagedSnow = yield* this.snowStepper(this.snow, weather, stagedExposure);
      }
      // No yields inside the commit: the pair and all corresponding effects publish together.
      if (stagedSnow && stagedExposure) { this.snow = stagedSnow; this.exposure = stagedExposure; this.refreshConditions(); }
      if (hour > state.lastWeatherHour) this.weatherByHour.delete(hour - 1);
      state.lastWeatherHour = Math.max(hour, state.lastWeatherHour);
      const delta = (next - before) / 1000;
      state.clock.macroSecond = (state.clock.macroSecond + delta) as MacroSecond;
      state.clock.microSecond = (state.clock.microSecond + delta * microRate(state.clock, headless, state.config)) as MicroSecond;
      state.clock.at = new Date(next).toISOString();
      if (state.clock.season === 'summer' && next >= Date.parse(state.clock.winterStart) && next < end) state.clock.season = 'winter';
      if (next % 60000 === 0 || next === eventBoundary) this.macroMinute(next, next % 60000 === 0);
      this.microAdvanceChronological(state.clock.microSecond, headless);
      state.clock.revision++;
      if (state.clock.season === 'winter' && next === end) {
        if (state.advance?.state === 'running') state.advance.state = 'completed';
        state.clock.season = 'summer'; this.pause();
      } else if (state.clock.season === 'summer' && next >= Date.parse(state.clock.winterStart) && next < end) {
        state.clock.season = 'winter';
      }
      if (state.advance) state.advance.fraction = clamp((next - Date.parse(state.advance.startedAt)) /
        (Date.parse(state.advance.request.target) - Date.parse(state.advance.startedAt)));
      if (state.clock.paused) this.settlePresentation();
      yield;
      if (!wasPaused && state.clock.paused) return;
      if (state.advance?.state === 'suspended') return;
      if (state.clock.season === 'summer' && next === end) return;
    }
    if (state.advance?.state === 'running' && Date.parse(state.clock.at) >= Date.parse(state.advance.request.target)) {
      state.advance.state = 'completed'; this.pause();
      if (state.advance.request.destination === 'winter') {
        const bounds = winterBounds(state.clock.at, state.clock.timezone, state.config.winterWeeks);
        state.clock.winterStart = bounds.start; state.clock.winterEnd = bounds.end; state.clock.season = 'winter';
      }
      this.changePresentation(state.clock.speed >= 8);
    }
  }
  publication(render = true): DualPublication {
    const state = this.state, selected = state.guests.find(g => g.id === state.selectedGuestId) ?? null;
    const points = render && state.clock.speed < 8 && state.advance?.state !== 'running' ? state.guests.flatMap(g => {
      if (g.status === 'departed') return [];
      const route = g.edgeId ? this.routes.get(g.edgeId) : null;
      if (route && !route.length) return [];
      const identity = `${state.seed}|${g.id}|${g.laneEdgeId ?? g.edgeId}`;
      const waiting = g.status === 'trail-queue';
      const progress = waiting ? 0 : (state.clock.microSecond - g.started) / Math.max(1e-9, g.due - g.started);
      const position = route ? routePosition(route, progress, identity)
        : (g.lastPosition ?? this.outgoing.get(g.nodeId)?.[0]?.path[0] ?? this.resort.portal?.lngLat);
      return position ? [{ id: g.id, lng: position[0], lat: position[1], status: g.status,
        ...(route && g.edgeId && !waiting ? { motion: { routeId: this.edges.has(g.edgeId) ? `${g.edgeId}@${state.resortRevision}` : g.edgeId,
          lane: stableHash(identity) % route.lanes.length,
          progress: clamp(progress), duration: g.due - g.started } } : {}) }] : [];
    }) : [];
    return structuredClone({ clock: state.clock, flow: state.flow, signals: state.signals,
      guests: state.guests.slice(0, 12), selected, autoTrack: state.autoTrack, advance: state.advance, points });
  }
  geometry(): Record<string, PreparedRoute> {
    return Object.fromEntries([...this.routes].map(([id, route]) => [this.edges.has(id) ? `${id}@${this.state.resortRevision}` : id, route]));
  }
  checkpoint(): DualCheckpoint {
    return { ...structuredClone(this.state), snow: this.snow ? { bounds: { ...this.snow.bounds }, width: this.snow.width,
      height: this.snow.height, depthM: Array.from(this.snow.depthM), surface: Array.from(this.snow.surface), exposure: Array.from(this.exposure) } : null };
  }
}
