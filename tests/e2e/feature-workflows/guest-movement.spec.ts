import { expect, test } from '../support/deterministicApp';
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
import { mkdirSync } from 'node:fs';

type MotionPoint = { id: string; lng: number; lat: number; status: string;
  motion?: { routeId: string; lane: number; progress: number; duration: number } };
type RouteProbe = { id: string; lanes: [number, number][][]; distances: number[]; length: number };
type GpuProbe = { count: number; hitCount: number; statusCodes: number[]; motion: MotionPoint[];
  routes: RouteProbe[]; hit: { id: string; x: number; y: number } | null; dpr: number };

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
  return page.evaluate(() => {
    const empty: GpuProbe = { count: 0, hitCount: 0, statusCodes: [], motion: [], routes: [], hit: null,
      dpr: window.devicePixelRatio || 1 };
    const map = (window as unknown as { appMap?: {
      getLayer(id: string): { implementation?: Record<string, unknown> } | undefined;
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
      const offset = index * 5;
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
    return { count: Number(layer.count ?? 0), hitCount, statusCodes: Array.from((layer.legacyStatusCodes as ArrayLike<number> | undefined) ?? []),
      motion, routes, hit, dpr: window.devicePixelRatio || 1 };
  });
}

function representativeSamples(entries: WorkerProbeEntry[], ids: readonly string[]) {
  const wanted = new Set(ids), latest = new Map<string, NonNullable<WorkerProbeEntry['publications']>[number]['representatives'][number]>();
  for (const publication of entries.flatMap((entry) => entry.publications ?? [])) {
    for (const guest of publication.representatives ?? []) if (guest.id && wanted.has(guest.id)) latest.set(guest.id, guest);
  }
  return [...latest.values()];
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

  await page.getByRole('button', { name: 'Play game clock', exact: true }).click();
  await expect.poll(async () => {
    const entries = await workerEntries(page, 'dualClock.worker');
    return representativeSamples(entries, queueIds).filter((guest) => guest.status === 'skiing').length;
  }, { timeout: 20_000 }).toBe(3);
  const workerAfterRelease = await workerEntries(page, 'dualClock.worker');
  const released = representativeSamples(workerAfterRelease, queueIds).sort((left, right) => left.id!.localeCompare(right.id!));
  expect(released.map((guest) => guest.started)).toEqual([0, 2, 4]);
  expect(new Set(released.map((guest) => Number((guest.due! - guest.started!).toFixed(6)))).size).toBeGreaterThan(1);
  expect(workerAfterRelease.some((entry) => entry.movementStatuses?.some((counts) => (counts.skiing ?? 0) === 3))).toBe(true);

  await expect.poll(async () => {
    const probe = await gpuProbe(page);
    const motion = probe.motion.filter((point) => queueIds.includes(point.id));
    return motion.length === queueIds.length && motion.every((point) => point.status === 'skiing');
  }, { timeout: 20_000 }).toBe(true);
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
