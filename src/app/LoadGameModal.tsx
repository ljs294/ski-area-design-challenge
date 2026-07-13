import { useEffect, useState } from 'react';
import type { MouseEvent } from 'react';
import { listGames, deleteGame } from '../gameSaveClient';
import type { GameSaveSummary } from '../types';

function fmtDate(iso: string): string {
  const d = new Date(iso);
  return isNaN(d.getTime()) ? '' : d.toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' });
}

export function LoadGameModal({ onClose, onPick }: { onClose: () => void; onPick: (key: string) => void }) {
  const [saves, setSaves] = useState<GameSaveSummary[] | null>(null);

  const refresh = () =>
    listGames().then((l) => setSaves([...l].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))));

  useEffect(() => {
    void refresh();
  }, []);

  async function handleDelete(e: MouseEvent, key: string) {
    e.stopPropagation();
    await deleteGame(key);
    void refresh();
  }

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="list-panel" onClick={(e) => e.stopPropagation()}>
        <div className="settings-header">
          <h2 className="settings-title">Load Game</h2>
          <button className="settings-close-x" aria-label="Close" onClick={onClose}>
            ✕
          </button>
        </div>

        <div className="list-body">
          {saves === null ? (
            <p className="list-empty">Loading…</p>
          ) : saves.length === 0 ? (
            <p className="list-empty">No saved resorts yet. Start a New Game to create one.</p>
          ) : (
            saves.map((s) => (
              <button key={s.key} className="list-row" onClick={() => onPick(s.key)}>
                <span className="list-row-main">
                  <span className="list-row-title">{s.name}</span>
                  <span className="list-row-sub">Updated {fmtDate(s.updatedAt)}</span>
                </span>
                <span
                  className="list-row-delete"
                  role="button"
                  aria-label={`Delete ${s.name}`}
                  onClick={(e) => handleDelete(e, s.key)}
                >
                  🗑
                </span>
              </button>
            ))
          )}
        </div>
      </div>
    </div>
  );
}
