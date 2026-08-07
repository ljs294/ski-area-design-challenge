import type { Page } from '@playwright/test';

const FIXED_TIME = '2026-01-01T00:00:00.000Z';
const TERRAIN_KEY = 'e2e-terrain';
const SAVE_KEY = 'e2e-save';
const bounds = { west: -121.5, south: 46.9, east: -121.49, north: 46.91 };

function checksumBytes(bytes: ArrayLike<number>): string {
  let hash = 0x811c9dc5;
  for (let index = 0; index < bytes.length; index += 1) {
    hash ^= bytes[index] & 0xff;
    hash = Math.imul(hash, 0x01000193) >>> 0;
  }
  return `fnv1a32-${hash.toString(16).padStart(8, '0')}`;
}

function floatMetadata(values: number[]) {
  const bytes = new Uint8Array(Float32Array.from(values).buffer);
  return { byteLength: bytes.byteLength, checksum: checksumBytes(bytes) };
}

/** Small, fully valid schema-v5 package. It exercises the real browser stores. */
function preparedTerrainFixture(includeMapContext = true) {
  const sampleHeights = [1000, 1010, 1020, 1030];
  const coverGrid = {
    bounds,
    width: 2,
    height: 2,
    cellSizeM: 10,
    data: [10, 10, 20, 30],
    complete: true,
    nodataCount: 0,
    source: 'esa-worldcover-2021-v200',
    vintage: '2021',
  };
  const coverMetadata = {
    ...coverGrid,
    data: undefined,
    byteLength: coverGrid.data.length,
    checksum: checksumBytes(coverGrid.data),
  };
  delete (coverMetadata as { data?: unknown }).data;

  const coverBoundarySegments = [0, 0, 1, 0, 10];
  const coverGeometryMetadata = {
    segmentCount: 1,
    ...floatMetadata(coverBoundarySegments),
  };
  const contourSegments = [0, 0, 1, 1, 1500];
  const contourMetadata = {
    intervalM: 6.096,
    segmentCount: 1,
    gridSize: 2,
    ...floatMetadata(contourSegments),
  };
  const coverDisplayGeometry = [10, 1, 4, 0, 0, 1, 0, 1, 1, 0, 0];
  const coverDisplayMetadata = {
    polygonCount: 1,
    ringCount: 1,
    vertexCount: 4,
    smoothingM: 24,
    simplifyM: 10,
    minFeatureM2: 600,
    ...floatMetadata(coverDisplayGeometry),
  };
  const elevation = floatMetadata(sampleHeights);

  const terrain = {
    schemaVersion: 5,
    key: TERRAIN_KEY,
    mountainName: 'Deterministic Peak',
    latitude: 46.905,
    longitude: -121.495,
    areaSizeMeters: 2000,
    bounds,
    sampleGridSize: 2,
    sampleHeights,
    coverGrid,
    coverMetadata,
    coverBoundarySegments,
    coverGeometryMetadata,
    coverDisplayGeometry,
    coverDisplayMetadata,
    contourSegments,
    contourMetadata,
    climate: { monthly: [] },
    vectorFeatures: {
      roads: [{ id: 'way/road', name: 'Context Road', roadClass: 'minor',
        points: [[-121.499, 46.901], [-121.491, 46.909]] }],
      waterLines: [{ id: 'way/stream', name: 'Context Creek', waterClass: 'stream',
        sourceWidthM: 3, points: [[-121.498, 46.909], [-121.492, 46.901]] }],
      waterPolygons: [{ id: 'way/lake', name: 'Context Lake', rings: [[
        [-121.497, 46.906], [-121.496, 46.906], [-121.496, 46.907],
        [-121.497, 46.906],
      ]] }],
      landCover: [],
      peaks: [],
    },
    sourceType: 'live',
    createdAt: FIXED_TIME,
    updatedAt: FIXED_TIME,
    packageManifest: {
      schemaVersion: 2,
      terrainKey: TERRAIN_KEY,
      complete: true,
      elevationByteLength: elevation.byteLength,
      elevationChecksum: elevation.checksum,
      cover: coverMetadata,
      coverGeometry: coverGeometryMetadata,
      coverDisplay: coverDisplayMetadata,
      contours: contourMetadata,
      assets: {
        elevation: `${TERRAIN_KEY}.heights.bin`,
        cover: `${TERRAIN_KEY}.cover.bin`,
        coverGeometry: `${TERRAIN_KEY}.cover-geometry.bin`,
        coverDisplay: `${TERRAIN_KEY}.cover-display.bin`,
        contours: `${TERRAIN_KEY}.contours.bin`,
      },
      preparedAt: FIXED_TIME,
    },
  };
  if (!includeMapContext) {
    delete (terrain as { vectorFeatures?: unknown }).vectorFeatures;
  }
  return terrain;
}

/** Structures merged into the fixture save, for tests that need built features. */
export interface PreparedStructures {
  lifts?: Record<string, unknown>[];
  trails?: Record<string, unknown>[];
  dams?: Record<string, unknown>[];
  ponds?: Record<string, unknown>[];
  snowmakingNodes?: Record<string, unknown>[];
  snowmakingLakeIds?: string[];
}

export interface PreparedResortOptions {
  /** Omit persisted OSM vectors to exercise Settings -> Resort Data recovery. */
  mapContext?: 'present' | 'missing';
}

function preparedSaveFixture(structures: PreparedStructures) {
  return {
    schemaVersion: 11,
    key: SAVE_KEY,
    name: 'Deterministic Peak',
    terrainKey: TERRAIN_KEY,
    center: [-121.495, 46.905],
    zoom: 13,
    bearing: 0,
    pitch: 0,
    is3D: false,
    site: {
      bounds: [[bounds.west, bounds.south], [bounds.east, bounds.north]],
      widthKm: 0.76,
      heightKm: 1.11,
      areaKm2: 0.84,
    },
    lifts: structures.lifts ?? [],
    trails: structures.trails ?? [],
    roads: [],
    dams: structures.dams ?? [],
    ponds: structures.ponds ?? [],
    nodes: [],
    paths: [],
    junctions: [],
    snowmakingNodes: structures.snowmakingNodes ?? [],
    lakeDepthOverrides: {},
    lakeNameOverrides: {},
    snowmakingLakeIds: structures.snowmakingLakeIds ?? [],
    streamWidthOverrides: {},
    createdAt: FIXED_TIME,
    updatedAt: FIXED_TIME,
  };
}

export async function seedPreparedResort(
  page: Page,
  structures: PreparedStructures = {},
  options: PreparedResortOptions = {},
): Promise<void> {
  await page.goto('/?flat', { waitUntil: 'load' });
  await page.evaluate(
    async ({ save, terrain }) => {
      localStorage.clear();
      localStorage.setItem(`gamesave:${save.key}`, JSON.stringify(save));
      localStorage.setItem('gamesave-index', JSON.stringify([{
        key: save.key,
        name: save.name,
        terrainKey: save.terrainKey,
        createdAt: save.createdAt,
        updatedAt: save.updatedAt,
      }]));

      await new Promise<void>((resolve, reject) => {
        const request = indexedDB.open('mountain-planner-terrain', 1);
        request.onupgradeneeded = () => {
          if (!request.result.objectStoreNames.contains('terrains')) {
            request.result.createObjectStore('terrains', { keyPath: 'key' });
          }
        };
        request.onerror = () => reject(request.error ?? new Error('Unable to seed terrain fixture'));
        request.onsuccess = () => {
          const database = request.result;
          const transaction = database.transaction('terrains', 'readwrite');
          transaction.objectStore('terrains').put(terrain);
          transaction.onerror = () => reject(transaction.error ?? new Error('Unable to write terrain fixture'));
          transaction.oncomplete = () => {
            database.close();
            resolve();
          };
        };
      });
    },
    { save: preparedSaveFixture(structures),
      terrain: preparedTerrainFixture(options.mapContext !== 'missing') },
  );
  await page.reload({ waitUntil: 'load' });
}
