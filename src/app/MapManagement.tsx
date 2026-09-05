import { useEffect, useState } from 'react';
import { listTerrains, deleteTerrain } from '../terrainStorageClient';
import { listGames } from '../gameSaveClient';
import type { TerrainSummary } from '../types';

/** Download management is embedded in Settings; it never deletes referenced terrain. */
export function TerrainLibrary() {
  const [terrains, setTerrains] = useState<TerrainSummary[] | null>(null);
  const [references, setReferences] = useState<Set<string>>(new Set());
  const [error, setError] = useState<string | null>(null);
  const [confirmKey, setConfirmKey] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let alive = true;
    void Promise.all([listTerrains(), listGames()]).then(([items, saves]) => {
      if (!alive) return;
      setTerrains(items.sort((a, b) => b.updatedAt.localeCompare(a.updatedAt)));
      setReferences(new Set(saves.flatMap((save) => save.terrainKey ? [save.terrainKey] : [])));
    }).catch(() => { if (alive) setError('Downloaded terrain could not be loaded.'); });
    return () => { alive = false; };
  }, []);
  async function remove(key: string) {
    setBusy(true); setError(null);
    try {
      const saves = await listGames();
      if (saves.some((save) => save.terrainKey === key)) throw new Error('This terrain is used by a saved resort.');
      const result = await deleteTerrain(key);
      if (!result.ok) throw new Error('The terrain could not be deleted. Please try again.');
      setTerrains((items) => items?.filter((item) => item.key !== key) ?? []);
      setConfirmKey(null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Terrain could not be deleted.'); }
    finally { setBusy(false); }
  }
  return <section className="terrain-library" aria-label="Downloaded terrain">
    <h3>Downloaded terrain</h3><p className="setting-hint">Local mountain data. Terrain used by a saved resort is kept with that resort.</p>
    {error && <p role="alert" className="ui-error">{error}</p>}
    {!terrains && !error && <p role="status">Loading terrain…</p>}
    {terrains?.length === 0 && <p>No terrain downloaded yet.</p>}
    {terrains?.map((terrain) => <article className="terrain-row" key={terrain.key}>
      <div><strong>{terrain.mountainName}</strong><p>{(terrain.areaSizeMeters / 1000).toFixed(0)} km · {new Date(terrain.updatedAt).toLocaleDateString()}</p></div>
      {references.has(terrain.key) ? <span className="ui-badge">In use</span> : confirmKey === terrain.key
        ? <div><p>Delete this downloaded terrain?</p><div className="ui-actions">
          <button className="ui-button" disabled={busy} onClick={() => setConfirmKey(null)}>Keep</button>
          <button className="ui-button ui-button-danger" disabled={busy} onClick={() => void remove(terrain.key)}>Delete terrain</button>
        </div></div>
        : <button className="ui-button" disabled={busy} onClick={() => setConfirmKey(terrain.key)}>Delete</button>}
    </article>)}
  </section>;
}
