import type { GameSave } from './types/gameSave';

/** New dual-clock games. Legacy saves continue using the schema-16 writer. */
export const CURRENT_GAME_SAVE_SCHEMA_VERSION = 17 as const satisfies GameSave['schemaVersion'];
export const LEGACY_GAME_SAVE_SCHEMA_VERSION = 16 as const;
