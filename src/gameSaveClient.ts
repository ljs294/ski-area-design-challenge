// Renderer-side wrapper over game-save storage. Uses the Electron desktop bridge
// when present, else localStorage (web-demo / dev fallback). Mirrors
// terrainStorageClient.ts.
import { desktop } from './desktopBridge';
import type {
  GameSaveSaveResponse,
  GameSaveLoadResponse,
  GameSaveListResponse,
  GameSaveDeleteResponse,
} from './ipcContract';
import type { GameSave, GameSaveSummary } from './types';

const PREFIX = 'gamesave:';
const INDEX_KEY = 'gamesave-index';

function localList(): GameSaveSummary[] {
  try {
    return JSON.parse(localStorage.getItem(INDEX_KEY) || '[]');
  } catch {
    return [];
  }
}

function localWriteIndex(summaries: GameSaveSummary[]): void {
  localStorage.setItem(INDEX_KEY, JSON.stringify(summaries));
}

function toSummary(save: GameSave): GameSaveSummary {
  return {
    key: save.key,
    name: save.name,
    mountainId: save.mountainId,
    terrainKey: save.terrainKey,
    createdAt: save.createdAt,
    updatedAt: save.updatedAt,
  };
}

export async function saveGame(save: GameSave): Promise<GameSaveSaveResponse> {
  if (desktop) return desktop.games.save(save);
  try {
    localStorage.setItem(PREFIX + save.key, JSON.stringify(save));
    localWriteIndex([...localList().filter((s) => s.key !== save.key), toSummary(save)]);
    return { ok: true, key: save.key };
  } catch (e) {
    return { ok: false, error: e instanceof Error ? e.message : 'Unknown error saving game' };
  }
}

export async function loadGame(key: string): Promise<GameSaveLoadResponse> {
  if (desktop) return desktop.games.load(key);
  const raw = localStorage.getItem(PREFIX + key);
  return raw ? JSON.parse(raw) : null;
}

export async function listGames(): Promise<GameSaveListResponse> {
  if (desktop) return desktop.games.list();
  return localList();
}

export async function deleteGame(key: string): Promise<GameSaveDeleteResponse> {
  if (desktop) return desktop.games.delete(key);
  localStorage.removeItem(PREFIX + key);
  localWriteIndex(localList().filter((s) => s.key !== key));
  return { ok: true };
}

/** Newest save by updatedAt, for the "Continue Game" shortcut. Null if none. */
export async function mostRecentGame(): Promise<GameSaveSummary | null> {
  const list = await listGames();
  if (list.length === 0) return null;
  return list.reduce((a, b) => (a.updatedAt >= b.updatedAt ? a : b));
}
