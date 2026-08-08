import { describe, expect, expectTypeOf, it } from 'vitest';
import { sanitizeDams } from './damAnalysis';
import { CURRENT_GAME_SAVE_SCHEMA_VERSION } from './gameSaveSchema';
import { sanitizeLifts } from './lifts';
import { sanitizePonds } from './pondAnalysis';
import { sanitizeRoads } from './roads';
import { sanitizeNodes, sanitizePaths } from './skiNodes';
import { hydrateSnowmakingNetwork } from './snowmakingNetwork';
import { sanitizeJunctions } from './topology';
import { sanitizeTrails } from './trails';
import type { GameSave, SavedSiteBox } from './types/gameSave';
import type { SavedLift } from './types/lifts';
import type { SavedRoad } from './types/roads';
import type { SavedDam, SavedPond, SavedSnowgun, SavedSnowmakingNode, SavedSnowmakingPipe,
  SnowmakingNodeNextNumbers } from './types/snowmaking';
import type { SavedJunction, SavedNode, SavedPath } from './types/topology';
import type { SavedTrail } from './types/trails';

interface ExpectedGameSave {
  schemaVersion: 1 | 2 | 3 | 4 | 5 | 6 | 7 | 8 | 9 | 10 | 11;
  key: string;
  name: string;
  mountainId?: string;
  terrainKey?: string;
  center: [number, number];
  zoom: number;
  bearing: number;
  pitch: number;
  is3D: boolean;
  site: SavedSiteBox | null;
  lifts: SavedLift[];
  trails: SavedTrail[];
  roads?: SavedRoad[];
  dams?: SavedDam[];
  ponds?: SavedPond[];
  nodes?: SavedNode[];
  paths?: SavedPath[];
  junctions?: SavedJunction[];
  snowmakingNodes?: SavedSnowmakingNode[];
  snowmakingPipes?: SavedSnowmakingPipe[];
  snowguns?: SavedSnowgun[];
  snowmakingNodeNextNumbers?: SnowmakingNodeNextNumbers;
  lakeDepthOverrides?: Record<string, number>;
  lakeNameOverrides?: Record<string, string>;
  snowmakingLakeIds?: string[];
  streamWidthOverrides?: Record<string, number>;
  createdAt: string;
  updatedAt: string;
  lastPlayedAt?: string;
}

const legacyFixture = {
  schemaVersion: 1,
  key: 'legacy-v1',
  name: 'Legacy Resort',
  center: [-121.5, 46.9] as [number, number],
  zoom: 12,
  bearing: 0,
  pitch: 0,
  is3D: false,
  site: null,
  lifts: [],
  trails: [],
  createdAt: '2025-01-01T00:00:00.000Z',
  updatedAt: '2025-01-01T00:00:00.000Z',
} satisfies GameSave;

const currentFixture = {
  ...legacyFixture,
  schemaVersion: 11,
  key: 'current-v11',
  roads: [],
  dams: [],
  ponds: [],
  nodes: [],
  paths: [],
  junctions: [],
  snowmakingNodes: [{
    id: 'hydrant-1',
    name: 'Hydrant 1',
    kind: 'hydrant',
    labelNumber: 1,
    point: [-121.5, 46.9] as [number, number],
    elevM: null,
    createdAt: '2026-08-06T00:00:00.000Z',
  }],
  snowmakingPipes: [{
    id: 'pipe-1', name: 'Main', diameterIn: 8,
    vertices: [
      { point: [-121.5, 46.9], elevM: 1000, nodeId: 'hydrant-1' },
      { point: [-121.499, 46.901], elevM: 1010, nodeId: null },
    ],
    lengthM: 1, verticalM: 1, createdAt: '2026-08-06T00:00:00.000Z',
  }],
  snowmakingNodeNextNumbers: { hydrant: 2, junction: 1, pump: 1 },
  snowguns: [{ id: 'gun-1', variantId: 'HKD_ImpulseR5_10s', point: [-121.5, 46.9],
    elevM: null, hydrantId: 'hydrant-1', createdAt: '2026-08-06T00:00:00.000Z' }],
} satisfies GameSave;

function hydrateDesignFixture(fixture: GameSave) {
  const parsed = JSON.parse(JSON.stringify(fixture)) as GameSave;
  const snowmaking = hydrateSnowmakingNetwork(parsed.snowmakingNodes ?? [],
    parsed.snowmakingPipes ?? [], parsed.snowmakingNodeNextNumbers, parsed.snowguns ?? []);
  return {
    lifts: sanitizeLifts(parsed.lifts),
    trails: sanitizeTrails(parsed.trails),
    roads: sanitizeRoads(parsed.roads ?? []),
    dams: sanitizeDams(parsed.dams ?? []),
    ponds: sanitizePonds(parsed.ponds ?? []),
    nodes: sanitizeNodes(parsed.nodes ?? []),
    paths: sanitizePaths(parsed.paths ?? []),
    junctions: sanitizeJunctions(parsed.junctions ?? []),
    snowmakingNodes: snowmaking.nodes,
    snowmakingPipes: snowmaking.pipes,
    snowguns: snowmaking.guns,
    snowmakingNodeNextNumbers: snowmaking.nextNumbers,
  };
}

describe('GameSave compatibility boundary', () => {
  it('keeps the exact field types and optionality of the approved schema', () => {
    expectTypeOf<GameSave>().toEqualTypeOf<ExpectedGameSave>();
  });

  it('hydrates a representative schema-v1 fixture with later collections absent', () => {
    expect(hydrateDesignFixture(legacyFixture)).toEqual({
      lifts: [], trails: [], roads: [], dams: [], ponds: [], nodes: [], paths: [],
      junctions: [], snowmakingNodes: [], snowmakingPipes: [], snowguns: [],
      snowmakingNodeNextNumbers: { hydrant: 1, junction: 1, pump: 1 },
    });
  });

  it('hydrates a representative schema-v11 fixture with current collections', () => {
    const hydrated = hydrateDesignFixture(currentFixture);
    expect(hydrated.snowmakingNodes).toEqual(currentFixture.snowmakingNodes);
    expect(hydrated.snowmakingPipes).toHaveLength(1);
    expect(hydrated.snowmakingPipes[0]).toMatchObject({ id: 'pipe-1', diameterIn: 8 });
    expect(hydrated.snowmakingPipes[0]?.lengthM).not.toBe(1);
    expect(hydrated.snowmakingPipes[0]?.verticalM).toBe(10);
    expect(hydrated.snowguns).toEqual(currentFixture.snowguns);
    expect(hydrated.snowmakingNodeNextNumbers).toEqual(currentFixture.snowmakingNodeNextNumbers);
  });

  it('keeps newly written saves on schema version 11', () => {
    expect(CURRENT_GAME_SAVE_SCHEMA_VERSION).toBe(11);
    expectTypeOf(CURRENT_GAME_SAVE_SCHEMA_VERSION).toEqualTypeOf<11>();
  });
});
