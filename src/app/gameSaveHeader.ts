import type { GameSave } from '../types/gameSave';
import { CURRENT_GAME_SAVE_SCHEMA_VERSION, LEGACY_GAME_SAVE_SCHEMA_VERSION } from '../gameSaveSchema';

/** Select the writer explicitly; loading a legacy game never upgrades its simulation semantics. */
export function gameSaveHeader(options: {
  base: GameSave | null; name: string; terrainKey?: string; dual: boolean; is3D: boolean; createId(): string;
  map: { getCenter(): { lng: number; lat: number }; getZoom(): number; getBearing(): number; getPitch(): number };
  now?: string;
}): Pick<GameSave, 'schemaVersion' | 'key' | 'name' | 'mountainId' | 'terrainKey' | 'center' | 'zoom' | 'bearing' | 'pitch' | 'is3D' | 'createdAt' | 'updatedAt' | 'lastPlayedAt'> {
  const { base, map } = options, now = options.now ?? new Date().toISOString(), center = map.getCenter();
  return { schemaVersion: options.dual ? CURRENT_GAME_SAVE_SCHEMA_VERSION : LEGACY_GAME_SAVE_SCHEMA_VERSION,
    key: base?.key ?? options.createId(), name: base?.name ?? (options.name.trim() || 'Untitled Resort'), mountainId: base?.mountainId,
    terrainKey: options.terrainKey ?? base?.terrainKey, center: [center.lng, center.lat], zoom: map.getZoom(),
    bearing: map.getBearing(), pitch: map.getPitch(), is3D: options.is3D, createdAt: base?.createdAt ?? now,
    updatedAt: now, lastPlayedAt: base?.lastPlayedAt };
}
