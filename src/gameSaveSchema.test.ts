import { describe, expect, expectTypeOf, it } from 'vitest';
import { sanitizeDams } from './damAnalysis';
import { CURRENT_GAME_SAVE_SCHEMA_VERSION } from './gameSaveSchema';
import { sanitizeLifts } from './lifts';
import { sanitizePonds } from './pondAnalysis';
import { sanitizeRoads } from './roads';
import { sanitizeNodes, sanitizePaths } from './skiNodes';
import { sanitizeSnowmakingNodes } from './snowmakingNodes';
import { sanitizeJunctions } from './topology';
import { sanitizeTrails } from './trails';
import type { GameSave, SavedSiteBox } from './types/gameSave';
import type { SavedLift } from './types/lifts';
import type { SavedRoad } from './types/roads';
import type { SavedDam, SavedPond, SavedSnowmakingNode } from './types/snowmaking';
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
    point: [-121.5, 46.9] as [number, number],
    elevM: null,
    createdAt: '2026-08-06T00:00:00.000Z',
  }],
} satisfies GameSave;

function hydrateDesignFixture(fixture: GameSave) {
  const parsed = JSON.parse(JSON.stringify(fixture)) as GameSave;
  return {
    lifts: sanitizeLifts(parsed.lifts),
    trails: sanitizeTrails(parsed.trails),
    roads: sanitizeRoads(parsed.roads ?? []),
    dams: sanitizeDams(parsed.dams ?? []),
    ponds: sanitizePonds(parsed.ponds ?? []),
    nodes: sanitizeNodes(parsed.nodes ?? []),
    paths: sanitizePaths(parsed.paths ?? []),
    junctions: sanitizeJunctions(parsed.junctions ?? []),
    snowmakingNodes: sanitizeSnowmakingNodes(parsed.snowmakingNodes ?? []),
  };
}

describe('GameSave compatibility boundary', () => {
  it('keeps the exact field types and optionality of the approved schema', () => {
    expectTypeOf<GameSave>().toEqualTypeOf<ExpectedGameSave>();
  });

  it('hydrates a representative schema-v1 fixture with later collections absent', () => {
    expect(hydrateDesignFixture(legacyFixture)).toEqual({
      lifts: [], trails: [], roads: [], dams: [], ponds: [], nodes: [], paths: [],
      junctions: [], snowmakingNodes: [],
    });
  });

  it('hydrates a representative schema-v11 fixture with current collections', () => {
    expect(hydrateDesignFixture(currentFixture).snowmakingNodes).toEqual(currentFixture.snowmakingNodes);
  });

  it('keeps newly written saves on schema version 11', () => {
    expect(CURRENT_GAME_SAVE_SCHEMA_VERSION).toBe(11);
    expectTypeOf(CURRENT_GAME_SAVE_SCHEMA_VERSION).toEqualTypeOf<11>();
  });
});
