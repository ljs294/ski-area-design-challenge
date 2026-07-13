import { useEffect, useState } from 'react';
import { listTerrains, deleteTerrain } from '../terrainStorageClient';
import type { TerrainSummary } from '../types';

function fmtDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

/** Full-screen manager for downloaded terrain records (the offline map cache). */
export function MapManagement({ onBack }: { onBack: () => void }) {
  const [terrains, setTerrains] = useState<TerrainSummary[] | null>(null);

  const refresh = () =>
    listTerrains().then((l) => setTerrains([...l].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))));

  useEffect(() => {
    void refresh();
  }, []);

  async function handleDelete(key: string) {
    await deleteTerrain(key);
    void refresh();
  }

  return (
    <div className="screen-view">
      <div className="screen-panel">
        <div className="screen-header">
          <div>
            <h2 className="screen-title">Map Management</h2>
            <p className="screen-subtitle">Terrain downloaded and stored on this machine</p>
          </div>
          <button className="ghost-btn" onClick={onBack}>
            ← Back to Menu
          </button>
        </div>

        <div className="list-body">
          {terrains === null ? (
            <p className="list-empty">Loading…</p>
          ) : terrains.length === 0 ? (
            <p className="list-empty">No terrain downloaded yet.</p>
          ) : (
            terrains.map((t) => (
              <div key={t.key} className="list-row list-row-static">
                <span className="list-row-main">
                  <span className="list-row-title">{t.mountainName}</span>
                  <span className="list-row-sub">
                    {(t.areaSizeMeters / 1000).toFixed(0)} km · {t.sourceType} · {fmtDate(t.updatedAt)}
                  </span>
                </span>
                <button className="danger-btn" onClick={() => handleDelete(t.key)}>
                  Delete
                </button>
              </div>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
