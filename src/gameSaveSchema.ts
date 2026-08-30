import type { GameSave } from './types/gameSave';

/** The schema written by every newly created or updated resort save. */
export const CURRENT_GAME_SAVE_SCHEMA_VERSION = 15 as const satisfies GameSave['schemaVersion'];
