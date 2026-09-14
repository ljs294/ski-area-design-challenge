import { expect, test } from '../support/deterministicApp';
import type { Page } from '@playwright/test';
import { seedPreparedResort, preparedTerrainFixture } from '../support/preparedResort';
import { installWorkerProbe, workerEntries, type WorkerProbeEntry } from '../support/workerProbe';
import { buildSkiNetwork, type TrailEdge } from '../../../src/network';
import { sanitizeTrails } from '../../../src/trails';
import { generateBareSnowGrid } from '../../../src/snow';
import { DualClockEngine } from '../../../src/dualClock/engine';
import { dualFixture, fixtureHour } from '../../../src/dualClock/fixtures';
import { routeLanePosition, type PreparedRoute } from '../../../src/dualClock/geometry';
import type { DualCheckpoint, TrailQueueState } from '../../../src/types/dualClock';
import { weatherTerrainBinding } from '../../../src/weather/terrainBinding';
import type { SavedLift } from '../../../src/types/lifts';
import type { SavedTrail } from '../../../src/types/trails';
import { GUEST_GPU_BYTES_PER_GUEST } from '../../../src/app/guestGpuLayer';
import { mkdirSync, writeFileSync } from 'node:fs';

type MotionPoint = { id: string; lng: number; lat: number; status: string;
  motion?: { routeId: string; lane: number; progress: number; duration: number } };
type RouteProbe = { id: string; lanes: [number, number][][]; distances: number[]; length: number };
type GpuProbe = { count: number; hitCount: number; statusCodes: number[]; motion: MotionPoint[];
  routes: RouteProbe[]; hit: { id: string; x: number; y: number } | null; dpr: number;
  terrainElevation: number | null; alignmentErrorPx: number | null };

/** A bent path long enough that the first three queue releases remain visible. */
function curvedGuestFixture() {
  const base: [number, number] = [-121.495, 46.90205];
  const top: [number, number] = [-121.495, 46.90315];
  const centerline: [number, number][] = [
    top,
    [-121.49472, 46.90283],
    [-121.49524, 46.90248],
    base,
  ];
  const west = -121.49565, east = -121.49435, south = 46.90195, north = 46.90325;
  const lift: SavedLift = { id: 'e2e-guest-curved-lift', identifier: 'A', name: 'Curved Summit',
    liftTypeId: 'detachable-six-pack', points: [base, top], endpointElevM: [1000, 1250],
    lengthM: 125, verticalM: 250, status: 'complete', createdAt: '2026-01-01T00:00:00.000Z' };
  const rawTrail: SavedTrail = { id: 'e2e-guest-curved-trail', name: 'Curved Return', brushWidthM: 36,
    areaM2: 4_500, lengthM: 130, verticalM: 250, avgSlopeDeg: 55, maxSlopeDeg: 55, difficulty: 'blue',
    status: 'complete', createdAt: '2026-01-01T00:00:00.000Z', parts: [{
      polygon: [[[west, south], [east, south], [east, north], [west, north], [west, south]]],
      centerline, centerlineElevM: [1250, 1160, 1080, 1000],
    }] };
  const trail = sanitizeTrails([rawTrail])[0]!;
  const terrain = preparedTerrainFixture();
  const network = buildSkiNetwork([trail], [lift]);
  const liftEdge = network.edges.find((edge) => edge.kind === 'lift');
  const trailEdge = network.edges.find((edge): edge is TrailEdge => edge.kind === 'trail');
  if (!liftEdge || liftEdge.kind !== 'lift' || !trailEdge) throw new Error('Curved guest fixture did not build a lift/trail pair.');
  const fixture = dualFixture(600);
  fixture.terrain = terrain;
  const snow = generateBareSnowGrid(terrain);
  // The movement fixture starts on a covered run so entry eligibility does
  // not discard the hand-authored queue before the release assertions.
  snow.depthM.fill(1);
  snow.surface.fill(1);
  fixture.snow = snow;
  fixture.resort = { revision: 1, edges: network.edges, trails: [trail],
    portal: { id: 'e2e-guest-portal', nodeId: liftEdge.from, lngLat: base, capacityPerMinute: 1000 },
    dailyDemand: 600, ticketPriceCents: 10000, amenities: [] };
  fixture.weather = Array.from({ length: 72 }, (_, index) =>
    fixtureHour(new Date(Date.parse(fixture.at) + index * 3_600_000).toISOString()));
  const initial = new DualClockEngine(fixture).checkpoint();
  const guests = initial.guests.slice(0, 3);
  if (guests.length !== 3) throw new Error(`Expected three representative guests, got ${guests.length}.`);
  const queueKey = `${trail.id}|${trailEdge.from}`;
  const entries = guests.map((guest, index) => ({ guestId: guest.id, arrivalMicroSecond: 0,
    ordinal: guest.ordinal, releaseMicroSecond: index * 2 }));
  guests.forEach((guest, index) => {
    guest.nodeId = trailEdge.from;
    guest.edgeId = trailEdge.id;
    guest.lastPosition = [...trailEdge.path[0]!];
    guest.lastTrailId = undefined;
    guest.status = 'trail-queue';
    guest.started = 0;
    guest.due = index * 2;
    guest.trailQueueKey = queueKey;
    guest.trailQueueArrivalMicroSecond = 0;
    guest.trailQueueReleaseMicroSecond = index * 2;
    guest.nextPlan = `Enter ${trail.name}`;
  });
  initial.clock.paused = true;
  initial.clock.microSecond = 0 as typeof initial.clock.microSecond;
  initial.trailQueues = { [queueKey]: { trailId: trail.id, entryNode: trailEdge.from,
    nextReleaseMicroSecond: 6, entries } satisfies TrailQueueState };
  return { checkpoint: initial, terrain, lift, trail, trailEdge, centerline, weather: weatherFixture(terrain) };
}

function weatherFixture(terrain: ReturnType<typeof preparedTerrainFixture>) {
  return { terrainKey: terrain.key, manifest: {
    schemaVersion: 1, terrainKey: terrain.key, terrainBinding: weatherTerrainBinding(terrain), timezone: 'UTC',
    historicalStartYear: 1991, historicalEndYear: 2020, quality: 'limited', sourceSummary: 'fixture',
    sourceVersion: 'fixture-v1', generatorVersion: 2, contentHash: 'guest-movement-e2e', complete: true,
    createdAt: '2026-01-01T00:00:00.000Z',
  }, historicalYears: [{ year: 1992, hours: Array.from({ length: 72 }, (_, index) =>
    fixtureHour(new Date(Date.UTC(1992, 0, 1, index)).toISOString())) }] };
}

async function putDualCheckpoint(page: Parameters<typeof seedPreparedResort>[0],
  checkpoint: DualCheckpoint, weather: ReturnType<typeof weatherFixture>): Promise<void> {
  await page.evaluate(async ({ checkpoint: value, weather: packageValue }) => {
    const save = JSON.parse(localStorage.getItem('gamesave:e2e-save')!);
    save.schemaVersion = 17;
    save.dualClock = value;
    localStorage.setItem('gamesave:e2e-save', JSON.stringify(save));
    await new Promise<void>((resolve, reject) => {
      const request = indexedDB.open('mountain-planner-weather', 3);
      request.onupgradeneeded = () => {
        for (const [name, keyPath] of [['packages', 'terrainKey'], ['manifests', 'contentHash'],
          ['chunks', 'id'], ['active', 'terrainKey']] as const) {
          if (!request.result.objectStoreNames.contains(name)) {
            const store = request.result.createObjectStore(name, { keyPath });
            if (name === 'chunks') store.createIndex('contentHash', 'contentHash', { unique: false });
          }
        }
      };
      request.onerror = () => reject(request.error ?? new Error('Unable to seed weather fixture'));
      request.onsuccess = () => {
        const database = request.result, transaction = database.transaction('packages', 'readwrite');
        transaction.objectStore('packages').put(packageValue);
        transaction.oncomplete = () => { database.close(); resolve(); };
        transaction.onerror = () => reject(transaction.error ?? new Error('Unable to write weather fixture'));
      };
    });
  }, { checkpoint, weather });
}

async function gpuProbe(page: Parameters<typeof seedPreparedResort>[0]): Promise<GpuProbe> {
  return page.evaluate((floatsPerGuest) => {
    const empty: GpuProbe = { count: 0, hitCount: 0, statusCodes: [], motion: [], routes: [], hit: null,
      dpr: window.devicePixelRatio || 1, terrainElevation: null, alignmentErrorPx: null };
    const map = (window as unknown as { appMap?: {
      getLayer(id: string): { implementation?: Record<string, unknown> } | undefined;
      project(point: [number, number]): { x: number; y: number };
      queryTerrainElevation(point: [number, number]): number | null;
      getSource?(id: string): { _data?: { geojson?: {
        type?: string; features?: { id?: string | number; properties?: Record<string, unknown>;
          geometry?: { type?: string; coordinates?: unknown[] } }[]
      } } } | undefined;
    } }).appMap;
    const layer = map?.getLayer('guest-simulation-dots')?.implementation;
    if (!layer) return empty;
    const routes = Object.entries((layer.motionRoutes as Record<string, {
      lanes: [number, number][][]; distances: Float64Array | number[]; length: number
    }> | undefined) ?? {}).map(([id, route]) => ({ id, lanes: route.lanes.map((lane) => lane.map((point) => [...point] as [number, number])),
      distances: Array.from(route.distances), length: route.length }));
    const pathPosition = (path: [number, number][], progress: number): [number, number] => {
      if (path.length < 2) return path[0] ?? [0, 0];
      const lengths = path.slice(1).map((point, index) => Math.hypot(
        (point[0] - path[index]![0]) * Math.cos(((point[1] + path[index]![1]) / 2) * Math.PI / 180),
        point[1] - path[index]![1]));
      const total = lengths.reduce((sum, length) => sum + length, 0);
      let remaining = Math.max(0, Math.min(1, progress)) * total;
      for (let index = 0; index < lengths.length; index += 1) {
        const length = lengths[index]!;
        if (remaining <= length || index === lengths.length - 1) {
          const from = path[index]!, to = path[index + 1]!;
          const t = length > 0 ? Math.max(0, Math.min(1, remaining / length)) : 0;
          return [from[0] + (to[0] - from[0]) * t, from[1] + (to[1] - from[1]) * t];
        }
        remaining -= length;
      }
      return path.at(-1)!;
    };
    const routeForPath = (path: [number, number][]): string | undefined => Object.entries(
      (layer.motionRoutes as Record<string, { lanes: [number, number][][] }> | undefined) ?? {},
    ).find(([, route]) => {
      const lane = route.lanes[0] ?? [];
      return lane.length === path.length && lane.every((point, index) =>
        point[0] === path[index]?.[0] && point[1] === path[index]?.[1]);
    })?.[0];
    const compact = layer.nextFrame as { ids?: Uint32Array; edgeIndices?: Int32Array;
      progress?: Float32Array; statusFlags?: Uint32Array } | null | undefined;
    const edgePaths = layer.edgePaths as [number, number][][] | undefined;
    const statusForFlags = (flags: number, edgePath: [number, number][] | undefined, progress: number): string => {
      if ((flags & 64) !== 0) return 'skiing';
      if ((flags & 32) !== 0) return 'lift-ride';
      if ((flags & 16) !== 0) return 'lift-queue';
      // trail-queue was appended to the movement transport after the legacy
      // flags. Older compact fixtures had no dedicated bit, but still carry a
      // trail edge at progress zero, which is unambiguous here.
      if ((flags & 262_144) !== 0 || (flags === 0 && edgePath && edgePath.length > 1 && progress === 0)) return 'trail-queue';
      return 'walking';
    };
    const compactMotion: MotionPoint[] = compact?.ids && compact.edgeIndices && compact.progress && compact.statusFlags
      ? Array.from(compact.ids, (numericId, index) => {
        const edgePath = edgePaths?.[compact.edgeIndices![index]!] ?? [];
        const progress = compact.progress![index] ?? 0;
        const routeId = routeForPath(edgePath);
        return { id: `representative-${numericId - 1}`, lng: pathPosition(edgePath, progress)[0],
          lat: pathPosition(edgePath, progress)[1], status: statusForFlags(compact.statusFlags![index] ?? 0, edgePath, progress),
          ...(routeId ? { motion: { routeId, lane: 0, progress, duration: 1 } } : {}) };
      }) : [];
    const legacyMotion = Array.from((layer.motionNext as MotionPoint[] | undefined) ?? []).map((point) => ({
      id: point.id, lng: point.lng, lat: point.lat, status: point.status,
      ...(point.motion ? { motion: { ...point.motion } } : {}),
    }));
    // Dual mode intentionally leaves the GeoJSON source empty. Queue points
    // have no motion dictionary entry, but GuestGpuLayer retains their actual
    // coordinates in the legacy interleaved buffer. Use worker publications
    // for the status because legacy status code 4 also represents lift queues.
    const statusById = new Map<string, string>();
    const workerProbes = (window as unknown as { appWorkerProbe?: { publications?: {
      representatives?: { id?: string; status?: string }[]
    }[] }[] }).appWorkerProbe ?? [];
    for (const worker of workerProbes) for (const publication of worker.publications ?? []) {
      for (const representative of publication.representatives ?? []) {
        if (representative.id && representative.status) statusById.set(representative.id, representative.status);
      }
    }
    const mercatorPosition = (x: number, y: number): [number, number] => [
      x * 360 - 180,
      360 / Math.PI * Math.atan(Math.exp((180 - y * 360) * Math.PI / 180)) - 90,
    ];
    const legacyIds = (layer.legacyGuestIds as readonly string[] | undefined) ?? [];
    const pending = layer.pending as Float32Array | undefined;
    const legacyPoints: MotionPoint[] = legacyIds.flatMap((id, index) => {
      const offset = index * floatsPerGuest;
      if (!pending || pending.length < offset + 4) return [];
      const position = mercatorPosition(pending[offset + 2]!, pending[offset + 3]!);
      const status = statusById.get(id) ?? ({ 2: 'skiing', 3: 'lift-ride', 4: 'lift-queue', 6: 'walking' }[
        Number((layer.legacyStatusCodes as ArrayLike<number> | undefined)?.[index] ?? 0)] ?? 'walking');
      return [{ id, lng: position[0], lat: position[1], status }];
    });
    const rendered = new Map(legacyPoints.map((point) => [point.id, point] as const));
    for (const point of legacyMotion) rendered.set(point.id, point);
    const motion = compactMotion.length ? compactMotion : [...rendered.values()];
    let hit: GpuProbe['hit'] = null;
    const hitCount = Number(layer.hitCount ?? 0);
    const hitXs = layer.hitXs as Float32Array | undefined, hitYs = layer.hitYs as Float32Array | undefined;
    if (hitCount > 0 && hitXs && hitYs) {
      const x = hitXs[0]!, y = hitYs[0]!;
      const found = (layer.hitTest as ((point: { x: number; y: number }) => { id: string } | null)).call(layer, { x, y });
      hit = found ? { id: found.id.startsWith('guest-')
        ? `representative-${Number(found.id.slice('guest-'.length))}` : found.id, x, y } : null;
    }
    let alignmentErrorPx: number | null = null;
    let terrainElevation: number | null = null;
    const startedAt = Number(layer.startedAt ?? 0), durationMs = Number(layer.durationMs ?? 0);
    const fraction = durationMs === 0 ? 1 : Math.min(1, (performance.now() - startedAt) / durationMs);
    if (hitCount > 0 && pending && hitXs && hitYs) {
      for (let guestIndex = 0; guestIndex < Number(layer.count ?? 0); guestIndex += 1) {
        const offset = guestIndex * floatsPerGuest;
        const x = pending[offset]! + (pending[offset + 2]! - pending[offset]!) * fraction;
        const y = pending[offset + 1]! + (pending[offset + 3]! - pending[offset + 1]!) * fraction;
        const position = mercatorPosition(x, y);
        const expected = map.project(position);
        terrainElevation ??= map.queryTerrainElevation(position);
        for (let accepted = 0; accepted < hitCount; accepted += 1) {
          const error = Math.hypot(hitXs[accepted]! - expected.x, hitYs[accepted]! - expected.y);
          alignmentErrorPx = alignmentErrorPx === null ? error : Math.min(alignmentErrorPx, error);
        }
      }
    }
    return { count: Number(layer.count ?? 0), hitCount, statusCodes: Array.from((layer.legacyStatusCodes as ArrayLike<number> | undefined) ?? []),
      motion, routes, hit, dpr: window.devicePixelRatio || 1, terrainElevation, alignmentErrorPx };
  }, GUEST_GPU_BYTES_PER_GUEST / Float32Array.BYTES_PER_ELEMENT);
}

function representativeSamples(entries: WorkerProbeEntry[], ids: readonly string[]) {
  const wanted = new Set(ids), latest = new Map<string, NonNullable<WorkerProbeEntry['publications']>[number]['representatives'][number]>();
  for (const publication of entries.flatMap((entry) => entry.publications ?? [])) {
    for (const guest of publication.representatives ?? []) if (guest.id && wanted.has(guest.id)) latest.set(guest.id, guest);
  }
  return [...latest.values()];
}

export async function findGuestClickTarget(page: Page, guestIds: readonly string[]): Promise<{
  guestId: string; clientX: number; clientY: number;
}> {
  for (const title of ['Toolbox', 'Curved Return']) {
    const close = page.getByRole('button', { name: `Close ${title}`, exact: true });
    if (await close.isVisible()) await close.click();
  }
  const pause = page.getByRole('button', { name: 'Pause game clock', exact: true });
  if (await pause.isVisible()) await pause.click();
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible({ timeout: 5_000 });
  return page.evaluate(async (wanted) => {
    type Candidate = { x: number; y: number; hitId: string | null; publicId: string | null; domTarget: string };
    const appMap = (window as unknown as { appMap?: {
      getCanvas(): HTMLCanvasElement;
      getLayer(id: string): { implementation?: Record<string, unknown> } | undefined;
      getCenter(): { lng: number; lat: number }; getZoom(): number; getPitch(): number; getBearing(): number;
      on(event: string, listener: () => void): void; off?(event: string, listener: () => void): void;
      triggerRepaint?(): void;
      queryRenderedFeatures(point: { x: number; y: number }, options: { layers: string[] }): Array<{
        id?: string | number; properties?: { id?: string | number };
      }>;
    } }).appMap;
    const layer = appMap?.getLayer('guest-simulation-dots')?.implementation;
    const canvas = appMap?.getCanvas();
    if (!appMap || !layer || !canvas) throw new Error('Guest GPU layer or map canvas is unavailable');
    const hitTest = (layer.hitTest as ((point: { x: number; y: number }) => { id: string } | null)).bind(layer);
    const camera = () => {
      const center = appMap.getCenter();
      return [center.lng, center.lat, appMap.getZoom(), appMap.getPitch(), appMap.getBearing()];
    };
    const deadline = performance.now() + 7_500;
    const waitForRender = () => new Promise<void>((resolve, reject) => {
      const remaining = deadline - performance.now();
      if (remaining <= 0) { reject(new Error('Guest render did not complete before the acquisition deadline')); return; }
      let settled = false;
      const listener = () => { if (settled) return; settled = true; window.clearTimeout(timeout); appMap.off?.('render', listener); resolve(); };
      const timeout = window.setTimeout(() => {
        if (settled) return; settled = true; appMap.off?.('render', listener); reject(new Error('Guest render did not complete'));
      }, remaining);
      appMap.on('render', listener);
      appMap.triggerRepaint?.();
    });
    const readCandidates = (): Candidate[] => {
      const hitCount = Number(layer.hitCount ?? 0);
      const hitXs = layer.hitXs as ArrayLike<number> | undefined;
      const hitYs = layer.hitYs as ArrayLike<number> | undefined;
      if (!hitXs || !hitYs || hitCount <= 0) return [];
      return Array.from({ length: hitCount }, (_, index) => {
        const x = Number(hitXs[index]), y = Number(hitYs[index]);
        const hit = hitTest({ x, y });
        const features = appMap.queryRenderedFeatures({ x, y }, { layers: ['guest-simulation-hit'] });
        const feature = features[0];
        const publicId = feature?.properties?.id ?? feature?.id;
        const element = document.elementFromPoint(canvas.getBoundingClientRect().left + x, canvas.getBoundingClientRect().top + y);
        return { x, y, hitId: hit?.id ?? null, publicId: publicId === undefined ? null : String(publicId),
          domTarget: element ? `${element.tagName.toLowerCase()}#${element.id}.${String(element.className ?? '').replace(/\s+/g, '.')}` : 'null' };
      });
    };
    const rect = canvas.getBoundingClientRect();
    const stableTarget = (first: Candidate[], second: Candidate[]) => first.flatMap((candidate) => {
      const matching = second.find((next) => candidate.hitId !== null && next.hitId === candidate.hitId
        && Math.abs(next.x - candidate.x) <= 0.01 && Math.abs(next.y - candidate.y) <= 0.01);
      if (!matching || !wanted.includes(candidate.hitId ?? '') || candidate.publicId !== candidate.hitId
        || matching.publicId !== candidate.hitId) return [];
      const clientX = Math.round(rect.left + candidate.x), clientY = Math.round(rect.top + candidate.y);
      const queryPoint = { x: clientX - rect.left, y: clientY - rect.top };
      const roundedHit = hitTest(queryPoint);
      const roundedFeatures = appMap.queryRenderedFeatures(queryPoint, { layers: ['guest-simulation-hit'] });
      const roundedFeature = roundedFeatures[0];
      const roundedPublicId = roundedFeature?.properties?.id ?? roundedFeature?.id;
      const element = document.elementFromPoint(clientX, clientY);
      if (!roundedHit || roundedHit.id !== candidate.hitId || String(roundedPublicId ?? '') !== candidate.hitId
        || !(element === canvas || canvas.contains(element))) return [];
      return [{ guestId: candidate.hitId, clientX, clientY, rawX: candidate.x, rawY: candidate.y,
        mapX: queryPoint.x, mapY: queryPoint.y, canvasRect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height },
        domTarget: element ? `${element.tagName.toLowerCase()}#${element.id}.${String(element.className ?? '').replace(/\s+/g, '.')}` : 'null' }];
    });
    let previous: { camera: number[]; candidates: Candidate[] } | null = null;
    let latest: Record<string, unknown> = { wanted, canvasRect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height } };
    while (performance.now() < deadline) {
      await waitForRender();
      const current = { camera: camera(), candidates: readCandidates() };
      if (previous) {
        const stable = stableTarget(previous.candidates, current.candidates);
        latest = { wanted, firstCamera: previous.camera, secondCamera: current.camera,
          first: previous.candidates, second: current.candidates, stable,
          canvasRect: { left: rect.left, top: rect.top, width: rect.width, height: rect.height } };
        (window as unknown as { guestClickDiagnostics?: Record<string, unknown> }).guestClickDiagnostics = latest;
        const cameraStable = previous.camera.every((value, index) => value === current.camera[index]);
        if (cameraStable && stable.length > 0) {
          return { guestId: stable[0]!.guestId, clientX: stable[0]!.clientX, clientY: stable[0]!.clientY };
        }
      }
      previous = current;
    }
    (window as unknown as { guestClickDiagnostics?: Record<string, unknown> }).guestClickDiagnostics = latest;
    throw new Error(`No stable guest hit target matched canonical IDs before the acquisition deadline: ${wanted.join(', ')}`);
  }, guestIds);
}

test('curved trail guests cross the worker, route decoder and GPU with FIFO movement', async ({ page }) => {
  test.setTimeout(90_000);
  await installWorkerProbe(page);
  const fixture = curvedGuestFixture();
  await seedPreparedResort(page, { lifts: [fixture.lift], trails: [fixture.trail] });
  await putDualCheckpoint(page, fixture.checkpoint, fixture.weather);
  await page.reload({ waitUntil: 'load' });
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 20_000 });
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible({ timeout: 20_000 });
  const queueIds = fixture.checkpoint.guests.slice(0, 3).map((guest) => guest.id);
  await page.evaluate((center) => {
    const map = (window as unknown as { appMap: { jumpTo(options: unknown): void } }).appMap;
    map.jumpTo({ center, zoom: 16.5 });
  }, fixture.centerline[1]);
  await expect.poll(async () => {
    const probe = await gpuProbe(page);
    return probe.motion.filter((point) => queueIds.includes(point.id)).length;
  }, { timeout: 20_000 }).toBe(3);
  const waiting = await gpuProbe(page);
  const waitingMotion = waiting.motion.filter((point) => queueIds.includes(point.id));
  expect(waitingMotion).toHaveLength(3);
  // Waiting publications carry the authoritative entrance position and status;
  // route metadata is optional until a usable route dictionary is published.
  const waitingWorker = await workerEntries(page, 'dualClock.worker');
  const waitingGuests = representativeSamples(waitingWorker, queueIds);
  expect(waitingGuests).toHaveLength(3);
  expect(waitingGuests.every((guest) => guest.status === 'trail-queue')).toBe(true);
  const entrance = fixture.trail.parts[0]!.centerline[0]!;
  expect(waitingMotion.every((point) => Math.hypot(
    (point.lng - entrance[0]) * 70_000, (point.lat - entrance[1]) * 111_320,
  ) < 1)).toBe(true);
  const waitingSnapshot = waitingMotion.map((point) => [point.id, point.lng, point.lat]);
  await page.waitForTimeout(300);
  const stillWaiting = await gpuProbe(page);
  expect(stillWaiting.motion.filter((point) => queueIds.includes(point.id))
    .map((point) => [point.id, point.lng, point.lat])).toEqual(waitingSnapshot);
  expect(waitingWorker.some((entry) => entry.movementStatuses?.some((counts) => (counts['trail-queue'] ?? 0) === 3))).toBe(true);

  const playButton = page.getByRole('button', { name: 'Play game clock', exact: true });
  await expect(playButton).toBeEnabled({ timeout: 20_000 });
  await playButton.click();
  await expect(page.getByRole('button', { name: 'Pause game clock', exact: true })).toBeVisible({ timeout: 5_000 });
  await expect.poll(async () => {
    const probe = await gpuProbe(page);
    const motion = probe.motion.filter((point) => queueIds.includes(point.id));
    return motion.length === queueIds.length && motion.every((point) => point.status === 'skiing');
  }, { timeout: 20_000 }).toBe(true);
  const workerAfterRelease = await workerEntries(page, 'dualClock.worker');
  const released = representativeSamples(workerAfterRelease, queueIds).sort((left, right) => left.id!.localeCompare(right.id!));
  expect(released.map((guest) => guest.started)).toEqual([0, 2, 4]);
  expect(new Set(released.map((guest) => Number((guest.due! - guest.started!).toFixed(6)))).size).toBeGreaterThan(1);
  let pickTarget: { guestId: string; clientX: number; clientY: number };
  try {
    pickTarget = await findGuestClickTarget(page, queueIds);
  } catch (error) {
    const diagnostics = await page.evaluate((message) => ({
      ...(window as unknown as { guestClickDiagnostics?: Record<string, unknown> }).guestClickDiagnostics,
      error: message,
    }), error instanceof Error ? error.message : String(error));
    mkdirSync('test-results/guest-movement', { recursive: true });
    writeFileSync('test-results/guest-movement/curved-guest-pick-failure.json', JSON.stringify(diagnostics, null, 2));
    throw error;
  }
  let selectedId: string | null = null;
  let clickError: unknown = null;
  await page.evaluate(() => {
    const map = (window as unknown as { appMap?: { on(event: string, listener: (event: unknown) => void): void;
      off?(event: string, listener: (event: unknown) => void): void } }).appMap;
    if (!map) return;
    const diagnostics = (window as unknown as { guestClickDiagnostics?: Record<string, unknown> }).guestClickDiagnostics ?? {};
    const listener = (event: unknown) => {
      const point = (event as { point?: { x?: number; y?: number } }).point;
      diagnostics.actualMapClickEvent = { point: { x: point?.x ?? null, y: point?.y ?? null } };
    };
    map.on('click', listener);
    (window as unknown as { guestClickDiagnostics?: Record<string, unknown>; guestClickCleanup?: () => void }).guestClickDiagnostics = diagnostics;
    (window as unknown as { guestClickCleanup?: () => void }).guestClickCleanup = () => map.off?.('click', listener);
  });
  const selectionBaseline = (await workerEntries(page, 'dualClock.worker'))
    .flatMap((entry) => entry.publications ?? [])
    .reduce((latest, publication) => publication.generation > latest.generation
      || (publication.generation === latest.generation && publication.id > latest.id)
      ? { generation: publication.generation, id: publication.id } : latest,
    { generation: -1, id: -1 });
  try {
    await page.mouse.click(pickTarget.clientX, pickTarget.clientY);
    await expect(page.getByRole('tabpanel', { name: 'Guests' })).toBeVisible({ timeout: 5_000 });
    await expect.poll(async () => {
      const entries = await workerEntries(page, 'dualClock.worker');
      const newer = entries.flatMap((entry) => entry.publications ?? [])
        .filter((publication) => publication.generation > selectionBaseline.generation
          || (publication.generation === selectionBaseline.generation && publication.id > selectionBaseline.id));
      selectedId = newer.at(-1)?.selectedId ?? null;
      return selectedId;
    }, { timeout: 5_000 }).toBe(pickTarget.guestId);
  } catch (error) {
    clickError = error;
    throw error;
  } finally {
    const diagnostics = await page.evaluate((payload: { actualSelectedId: string | null; selectionBaseline: { generation: number; id: number } }) => {
      const state = (window as unknown as { guestClickDiagnostics?: Record<string, unknown> }).guestClickDiagnostics ?? {};
      (window as unknown as { guestClickCleanup?: () => void }).guestClickCleanup?.();
      return { ...state, selectionBaseline: payload.selectionBaseline, selectedId: payload.actualSelectedId };
    }, { actualSelectedId: selectedId, selectionBaseline });
    if (clickError) {
      mkdirSync('test-results/guest-movement', { recursive: true });
      writeFileSync('test-results/guest-movement/curved-guest-pick-failure.json', JSON.stringify(diagnostics, null, 2));
    }
  }
  await expect.poll(async () => (await gpuProbe(page)).hitCount, { timeout: 5_000 }).toBeGreaterThan(0);
  const moving = await gpuProbe(page);
  const movingMotion = moving.motion.filter((point) => queueIds.includes(point.id));
  const trailRoute = moving.routes.find((route) => route.id.includes(fixture.trailEdge.id));
  expect(trailRoute).toBeDefined();
  expect(trailRoute!.lanes).toHaveLength(1);
  expect(trailRoute!.lanes[0]).toEqual(fixture.trailEdge.path);
  const route: PreparedRoute = { lanes: trailRoute!.lanes,
    distances: Float64Array.from(trailRoute!.distances), length: trailRoute!.length };
  for (const point of movingMotion) {
    expect(point.motion).toBeDefined();
    const expected = routeLanePosition(route, point.motion!.progress, point.motion!.lane);
    expect(Math.hypot((point.lng - expected[0]) * 70_000, (point.lat - expected[1]) * 111_320)).toBeLessThan(0.05);
  }
  expect(moving.hitCount).toBeGreaterThan(0);
  expect(moving.hit && queueIds.includes(moving.hit.id)).toBe(true);
  expect(moving.dpr).toBeGreaterThan(0);
  mkdirSync('test-results/guest-movement', { recursive: true });
  await page.locator('.maplibregl-canvas').screenshot({ path: 'test-results/guest-movement/curved-centerline-9px-dots.png' });
});

test('guest dots and hit targets follow terrain through camera and fast-forward transitions', async ({ page }) => {
  test.setTimeout(180_000);
  const fixture = curvedGuestFixture();
  await seedPreparedResort(page, { lifts: [fixture.lift], trails: [fixture.trail] });
  await putDualCheckpoint(page, fixture.checkpoint, fixture.weather);
  await page.reload({ waitUntil: 'load' });
  await page.getByRole('button', { name: /^Continue / }).click();
  await expect(page.locator('.resort-loading')).toHaveCount(0, { timeout: 20_000 });
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible({ timeout: 20_000 });

  await page.evaluate(async (center) => {
    const canvas = document.createElement('canvas');
    canvas.width = 256; canvas.height = 256;
    const context = canvas.getContext('2d')!;
    // Terrarium RGB(131,232,0) encodes a constant 1,000 metre elevation.
    context.fillStyle = 'rgb(131, 232, 0)'; context.fillRect(0, 0, 256, 256);
    const map = (window as unknown as { appMap: {
      addSource(id: string, source: unknown): void; setTerrain(value: unknown): void;
      jumpTo(options: unknown): void; once(event: string, listener: () => void): void;
    } }).appMap;
    map.addSource('guest-test-dem', { type: 'raster-dem', tiles: [canvas.toDataURL()],
      encoding: 'terrarium', tileSize: 256, maxzoom: 14 });
    map.setTerrain({ source: 'guest-test-dem', exaggeration: 1 });
    map.jumpTo({ center, zoom: 16.5, pitch: 0, bearing: 0 });
    await new Promise<void>(resolve => map.once('idle', resolve));
  }, fixture.centerline[1]);
  const expectAligned = async () => {
    await expect.poll(async () => (await gpuProbe(page)).terrainElevation ?? 0,
      { timeout: 20_000 }).toBeGreaterThan(100);
    await expect.poll(async () => (await gpuProbe(page)).alignmentErrorPx ?? 1_000,
      { timeout: 20_000 }).toBeLessThanOrEqual(3);
  };

  await expect.poll(async () => (await gpuProbe(page)).hitCount, { timeout: 20_000 }).toBeGreaterThan(0);
  await expectAligned();
  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expect.poll(async () => (await gpuProbe(page)).motion.some(point => point.status === 'skiing'),
    { timeout: 20_000 }).toBe(true);
  await page.getByRole('button', { name: 'Pause game clock', exact: true }).click();
  await expect(page.getByRole('button', { name: 'Play game clock', exact: true })).toBeVisible();
  await expect.poll(async () => (await gpuProbe(page)).hitCount, { timeout: 20_000 }).toBeGreaterThan(0);
  await expectAligned();

  for (const camera of [{ pitch: 60, bearing: 25, zoom: 16 }, { pitch: 0, bearing: 0, zoom: 17 }]) {
    await page.evaluate(({ center, view }) => {
      (window as unknown as { appMap: { jumpTo(options: unknown): void } }).appMap
        .jumpTo({ center, ...view });
    }, { center: fixture.centerline[1], view: camera });
    await expectAligned();
  }

  await page.getByRole('button', { name: '4× simulation speed', exact: true }).click();
  await expect(page.getByRole('button', { name: '4× simulation speed', exact: true }))
    .toHaveAttribute('aria-pressed', 'true');
  await expectAligned();
  for (const speed of [8, 16, 64] as const) {
    await page.getByRole('button', { name: `${speed}× simulation speed`, exact: true }).click();
    await expect(page.getByRole('button', { name: `${speed}× simulation speed`, exact: true }))
      .toHaveAttribute('aria-pressed', 'true');
    await expect.poll(async () => (await gpuProbe(page)).count, { timeout: 15_000 }).toBe(0);
  }

  await page.getByRole('button', { name: '1× simulation speed', exact: true }).click();
  await expect.poll(async () => (await gpuProbe(page)).hitCount, { timeout: 20_000 }).toBeGreaterThan(0);
  await expectAligned();

  await page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      getStyle(): unknown; setStyle(style: unknown, options: unknown): void;
      getSource(id: string): unknown; removeSource(id: string): void; setTerrain(value: unknown): void;
    } }).appMap;
    // Do not carry a data-URL raster DEM through MapLibre's full style clone;
    // remove and remount the deterministic terrain around the reload instead.
    map.setTerrain(null);
    if (map.getSource('guest-test-dem')) map.removeSource('guest-test-dem');
    map.setStyle(map.getStyle(), { diff: false });
  });
  await expect.poll(() => page.evaluate(() => (window as unknown as {
    appMap: { isStyleLoaded(): boolean }
  }).appMap.isStyleLoaded()), { timeout: 20_000 }).toBe(true);
  await page.evaluate(() => {
    const map = (window as unknown as { appMap: {
      addSource(id: string, source: unknown): void; setTerrain(value: unknown): void;
    } }).appMap;
    const canvas = document.createElement('canvas');
    canvas.width = 256; canvas.height = 256;
    const context = canvas.getContext('2d')!;
    context.fillStyle = 'rgb(131, 232, 0)'; context.fillRect(0, 0, 256, 256);
    map.addSource('guest-test-dem', { type: 'raster-dem', tiles: [canvas.toDataURL()],
      encoding: 'terrarium', tileSize: 256, maxzoom: 14 });
    map.setTerrain({ source: 'guest-test-dem', exaggeration: 1 });
  });
  await expect.poll(async () => (await gpuProbe(page)).hitCount, { timeout: 20_000 }).toBeGreaterThan(0);
  await expectAligned();
  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expectAligned();
});
