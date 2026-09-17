import { lazy, Suspense, useCallback, useEffect, useState } from 'react';
import { listDesigns, loadDesign } from '../../designSaveClient';
import type { DesignSaveBundle, DesignSaveSummary } from '../../types/designSave';
import { desktop } from '../../desktopBridge';
import { MainMenu } from '../MainMenu';
import { Settings } from '../Settings';
import { CreditsPanel } from '../CreditsPanel';
import { Dialog } from '../ui';
import { DesignGameplayView } from './DesignGameplayView';

const MapView = lazy(() => import('../MapView').then((module) => ({ default: module.MapView })));
type Screen = 'menu' | 'selecting' | 'loading' | 'game';

export function ThreeApp() {
  const [screen, setScreen] = useState<Screen>('menu');
  const [bundle, setBundle] = useState<DesignSaveBundle | null>(null);
  const [designs, setDesigns] = useState<DesignSaveSummary[]>([]);
  const [libraryOpen, setLibraryOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [creditsOpen, setCreditsOpen] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const refreshLibrary = useCallback(async () => {
    try { setDesigns(await listDesigns()); }
    catch (reason) { setError(reason instanceof Error ? reason.message : 'Could not read the design library.'); }
  }, []);
  const openDesign = useCallback(async (key: string) => {
    setScreen('loading'); setLibraryOpen(false); setError(null);
    try {
      const result = await loadDesign(key);
      if (!result?.ok) throw new Error(result?.error ?? 'That design is no longer available.');
      setBundle(result.bundle); setScreen('game');
      void refreshLibrary();
      const query = new URLSearchParams(location.search); query.set('three-save', key);
      history.replaceState(null, '', `${location.pathname}?${query}${location.hash}`);
    } catch (reason) {
      setError(reason instanceof Error ? reason.message : 'Could not load the design.'); setScreen('menu');
    }
  }, [refreshLibrary]);
  useEffect(() => {
    void refreshLibrary();
    const key = new URLSearchParams(location.search).get('three-save');
    if (key) void openDesign(key);
  }, [openDesign, refreshLibrary]);
  const newest = [...designs].sort((a, b) => b.updatedAt.localeCompare(a.updatedAt))[0];
  const menu = () => { setBundle(null); setScreen('menu'); history.replaceState(null, '', location.pathname); void refreshLibrary(); };

  return <>
    {screen === 'menu' && <MainMenu hasSaves={designs.length > 0} recentOverride={newest ?? null}
      onContinue={() => newest && void openDesign(newest.key)} onNewGame={() => setScreen('selecting')}
      onLoadGame={() => setLibraryOpen(true)} onSettings={() => setSettingsOpen(true)}
      onCredits={() => setCreditsOpen(true)} onExit={() => desktop?.exit()} />}
    {screen === 'selecting' && <Suspense fallback={<div className="three-loading">Opening terrain selection…</div>}>
      <MapView mode="picking" onQuit={menu} onOpenSettings={() => setSettingsOpen(true)}
        onLoadGame={() => setLibraryOpen(true)} controlsSuspended={settingsOpen || libraryOpen || creditsOpen}
        onForkCreated={(key) => { void openDesign(key); }} />
    </Suspense>}
    {screen === 'loading' && <div className="three-loading">Loading the Three.js design…</div>}
    {screen === 'game' && bundle && <DesignGameplayView bundle={bundle} onQuit={menu}
      onLoad={() => setLibraryOpen(true)} onSaved={(key) => { void openDesign(key); }} />}
    {libraryOpen && <Dialog title="Three.js design forks" onClose={() => setLibraryOpen(false)} className="three-library">
      <p>These design-only forks are separate from the original playable-game saves.</p>
      <div className="three-feature-list">{designs.map((design) => <button key={design.key}
        onClick={() => { void openDesign(design.key); }}><strong>{design.name}</strong><small>{new Date(design.updatedAt).toLocaleString()}</small></button>)}</div>
      {!designs.length && <p>No Three.js design forks yet. Choose New Resort to create one.</p>}
    </Dialog>}
    {settingsOpen && <Settings onClose={() => setSettingsOpen(false)} />}
    {creditsOpen && <CreditsPanel onClose={() => setCreditsOpen(false)} />}
    {error && <Dialog title="Three.js design" onClose={() => setError(null)}><p>{error}</p></Dialog>}
  </>;
}
