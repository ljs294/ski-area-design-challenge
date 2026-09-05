import { useEffect, useState } from 'react';
import { listGames, deleteGame, loadGamePreview } from '../gameSaveClient';
import type { GameSaveSummary } from '../types';
import { Dialog, Icon } from './ui';

const lastActivity = (save: GameSaveSummary) => save.lastPlayedAt && save.lastPlayedAt > save.updatedAt ? save.lastPlayedAt : save.updatedAt;

function ResortPreview({ save }: { save: GameSaveSummary }) {
  const [image, setImage] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    void loadGamePreview(save.key).then((value) => { if (alive) setImage(value); });
    return () => { alive = false; };
  }, [save.key]);
  return <div className="library-preview">{image ? <img src={image} alt="" /> : <Icon name="resort" />}</div>;
}
export function LoadGameModal({ onClose, onPick }: {
  onClose(): void; onPick(key: string, name: string): void;
}) {
  const [saves, setSaves] = useState<GameSaveSummary[] | null>(null);
  const [query, setQuery] = useState('');
  const [error, setError] = useState<string | null>(null);
  const [deleting, setDeleting] = useState<string | null>(null);
  const [busy, setBusy] = useState(false);
  useEffect(() => {
    let alive = true;
    void listGames().then((items) => { if (alive) setSaves(items.sort((a, b) =>
      lastActivity(b).localeCompare(lastActivity(a)))); })
      .catch(() => { if (alive) setError('Your resorts could not be loaded. Close this window and try again.'); });
    return () => { alive = false; };
  }, []);
  async function remove(key: string) {
    setBusy(true); setError(null);
    try {
      const result = await deleteGame(key);
      if (!result.ok) throw new Error('The resort could not be deleted. Please try again.');
      setSaves((items) => items?.filter((save) => save.key !== key) ?? []);
      setDeleting(null);
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'The resort could not be deleted.'); }
    finally { setBusy(false); }
  }
  const shown = saves?.filter((save) => save.name.toLowerCase().includes(query.toLowerCase()));
  return <Dialog title="My Resorts" onClose={onClose} className="resort-library">
    <div className="library-tools"><label>Find a resort<input className="ui-input" type="search" placeholder="Search by name"
      value={query} onChange={(event) => setQuery(event.target.value)} /></label>
      <span>{saves?.length ?? '—'} saved resorts</span></div>
    {error && <p className="ui-error" role="alert">{error}</p>}
    <div className="library-body">
      {!saves && !error && <p role="status">Loading your resorts…</p>}
      {saves?.length === 0 && <p>No resorts yet. Start a New Resort to choose your mountain.</p>}
      {saves && saves.length > 0 && shown?.length === 0 && <p>No resorts match “{query}”.</p>}
      {shown?.map((save) => <article className="library-resort" key={save.key}>
        <ResortPreview save={save} /><div className="library-resort-info"><h3>{save.name}</h3>
          <p>Last active {new Date(lastActivity(save)).toLocaleDateString(undefined, { month: 'short', day: 'numeric', year: 'numeric' })}</p>
          {deleting === save.key ? <div className="library-confirm"><p>Delete this resort and its preview? This cannot be undone.</p>
            <button className="ui-button" disabled={busy} onClick={() => setDeleting(null)}>Keep resort</button>
            <button className="ui-button ui-button-danger" disabled={busy} onClick={() => void remove(save.key)}>{busy ? 'Deleting…' : 'Delete resort'}</button>
          </div> : <div className="ui-actions"><button className="ui-button ui-button-primary" disabled={busy} onClick={() => onPick(save.key, save.name)}>Open resort</button>
            <button className="ui-button" disabled={busy} aria-label={`Delete ${save.name}`} onClick={() => setDeleting(save.key)}>Delete</button></div>}
        </div>
      </article>)}
    </div>
  </Dialog>;
}
