import { useCallback, useEffect, useMemo, useRef, useState } from 'react';
import type { DesignCameraState, DesignSaveBundle } from '../../src/types/designSave';
import { saveDesign } from '../../src/designSaveClient';
import { DesignSession } from '../../src/app/session/designSession';
import { designSaveDraft } from '../../src/app/session/designSaveSnapshot';
import { GameToolbar } from '../../src/app/GameToolbar';
import { GameMenu } from '../../src/app/GameMenu';
import { GameWindow, GameWindows } from '../../src/app/GameWindow';
import { View3DControl } from '../../src/app/View3DControl';
import { Settings } from '../../src/app/Settings';
import { CreditsPanel } from '../../src/app/CreditsPanel';
import { useSettings } from '../../src/app/SettingsContext';
import { Dialog, Icon } from '../../src/app/ui';
import { ThreeViewport, type ThreeAnalysisOverlay, type ThreeViewportHandle } from './ThreeViewport';
import type { ThreeFeatureSelection } from './terrainScene';
import './threeGameplay.css';

interface Props {
  bundle: DesignSaveBundle;
  onLoad(): void;
  onQuit(): void;
  onSaved(key: string): void;
}

export function DesignGameplayView({ bundle, onLoad, onQuit, onSaved }: Props) {
  const { settings } = useSettings();
  const viewport = useRef<ThreeViewportHandle>(null);
  const [camera, setCamera] = useState<DesignCameraState>(bundle.save.camera);
  const [selected, setSelected] = useState<ThreeFeatureSelection | null>(null);
  const [overlay, setOverlay] = useState<ThreeAnalysisOverlay>('none');
  const [toolboxOpen, setToolboxOpen] = useState(true);
  const [layersOpen, setLayersOpen] = useState(false);
  const [statsOpen, setStatsOpen] = useState(false);
  const [settingsOpen, setSettingsOpen] = useState(false);
  const [creditsOpen, setCreditsOpen] = useState(false);
  const [saving, setSaving] = useState(false);
  const [message, setMessage] = useState<string | null>(null);
  const [quitPrompt, setQuitPrompt] = useState(false);
  const [ready, setReady] = useState(false);
  const cameraDirty = JSON.stringify(camera) !== JSON.stringify(bundle.save.camera);
  const session = useMemo(() => new DesignSession({
    identity: { id: bundle.save.key, terrainKey: bundle.terrain.key },
    terrain: bundle.terrain,
    lifts: bundle.save.lifts,
    topology: { trails: bundle.save.trails, nodes: bundle.save.nodes,
      paths: bundle.save.paths, junctions: bundle.save.junctions },
    ports: { terrain: {
      cacheDisplayAssets: () => {}, activateProtocols: () => {}, publishState: () => {},
      refreshSources: () => {}, publishPersisted: () => {}, publishConstruction: () => {},
    } },
  }), [bundle]);
  useEffect(() => () => session.dispose(), [session]);
  useEffect(() => {
    const beforeUnload = (event: BeforeUnloadEvent) => {
      if (!cameraDirty) return;
      event.preventDefault(); event.returnValue = '';
    };
    window.addEventListener('beforeunload', beforeUnload);
    return () => window.removeEventListener('beforeunload', beforeUnload);
  }, [cameraDirty]);

  const select = useCallback((value: ThreeFeatureSelection | null) => setSelected(value), []);
  const cameraChanged = useCallback((value: DesignCameraState) => setCamera(value), []);
  const rendererReady = useCallback(() => setReady(true), []);
  const save = async (reload = true): Promise<boolean> => {
    if (saving) return false;
    setSaving(true); setMessage(null);
    try {
      const result = await saveDesign(designSaveDraft(session.read.persistenceSnapshot(), {
        name: bundle.save.name, createdAt: bundle.save.createdAt,
        updatedAt: new Date().toISOString(), site: bundle.save.site, camera,
      }));
      if (!result.ok) throw new Error(result.error);
      if (reload) onSaved(result.receipt.key);
      return true;
    } catch (error) {
      setMessage(error instanceof Error ? error.message : 'The design could not be saved.');
      return false;
    } finally { setSaving(false); }
  };
  const feature = selected?.kind === 'lift' ? bundle.save.lifts.find((item) => item.id === selected.id)
    : selected?.kind === 'trail' ? bundle.save.trails.find((item) => item.id === selected.id) : null;

  return <GameWindows><main className="three-game-root">
    <ThreeViewport ref={viewport} bundle={bundle} overlay={overlay} selected={selected}
      onSelect={select} onCameraChange={cameraChanged} onReady={rendererReady} />
    {!ready && <div className="three-loading">Building the Three.js mountain…</div>}
    <div className="workspace-shell game-window-shell three-game-chrome">
      <GameToolbar resortName={bundle.save.name} onOpenStats={() => setStatsOpen(true)}
        units={settings.units} terrain={bundle.terrain} unsaved={cameraDirty}
        navigation={<div className="three-toolbar-nav">
          <GameMenu canSave saving={saving} unsaved={cameraDirty} onSave={() => { void save(); }}
            onLoad={onLoad} onSettings={() => setSettingsOpen(true)} onCredits={() => setCreditsOpen(true)}
            onQuit={() => cameraDirty ? setQuitPrompt(true) : onQuit()} />
          <button className="tb-nav-btn" onClick={() => setToolboxOpen((open) => !open)}><Icon name="trails" /> Build</button>
          <button className="tb-nav-btn" onClick={() => setLayersOpen((open) => !open)}><Icon name="layers" /> Layers</button>
        </div>} />
      <div className="top-right-stack"><View3DControl is3D={camera.is3D}
        onToggle={() => viewport.current?.toggleOverhead()} /></div>
      {toolboxOpen && <GameWindow id="three-build" title="Mountain design" onClose={() => setToolboxOpen(false)}>
        <p className="three-capability-note">This Phase 2A workspace displays the saved design. Construction tools arrive in Phase 3.</p>
        <h3>Lifts ({bundle.save.lifts.length})</h3>
        <div className="three-feature-list">{bundle.save.lifts.map((lift) => <button key={lift.id}
          onClick={() => setSelected({ kind: 'lift', id: lift.id })}>{lift.identifier ? `${lift.identifier} · ` : ''}{lift.name}</button>)}</div>
        <h3>Trails ({bundle.save.trails.length})</h3>
        <div className="three-feature-list">{bundle.save.trails.map((trail) => <button key={trail.id}
          onClick={() => setSelected({ kind: 'trail', id: trail.id })}>{trail.name}</button>)}</div>
        <button className="primary-btn" disabled>Start construction (Phase 3)</button>
      </GameWindow>}
      {layersOpen && <GameWindow id="three-layers" title="Layers" onClose={() => setLayersOpen(false)}>
        <div className="three-layer-list">{(['none', 'contours', 'imagery'] as const).map((item) => <label key={item}>
          <input type="radio" name="three-overlay" checked={overlay === item} onChange={() => setOverlay(item)} />
          {item === 'none' ? 'Terrain and cover' : item[0].toUpperCase() + item.slice(1)}
        </label>)}</div>
      </GameWindow>}
      {selected && <GameWindow id="three-inspector" title="Selection" onClose={() => setSelected(null)}>
        <dl className="three-inspector"><dt>Type</dt><dd>{selected.kind}</dd><dt>Name</dt><dd>{feature?.name ?? selected.id}</dd>
          {'lengthM' in (feature ?? {}) && <><dt>Length</dt><dd>{Math.round((feature as { lengthM: number }).lengthM)} m</dd></>}
        </dl><p className="three-capability-note">Editing this feature becomes available with the Phase 3 construction tools.</p>
      </GameWindow>}
      {statsOpen && <GameWindow id="three-stats" title="Ski area details" onClose={() => setStatsOpen(false)}>
        <dl className="three-inspector"><dt>Lifts</dt><dd>{bundle.save.lifts.length}</dd><dt>Trails</dt><dd>{bundle.save.trails.length}</dd>
          <dt>Terrain cells</dt><dd>{bundle.terrain.sampleGridSize} × {bundle.terrain.sampleGridSize}</dd><dt>Mode</dt><dd>Design only</dd></dl>
      </GameWindow>}
      {message && <div className="three-status" role="alert">{message}<button onClick={() => setMessage(null)}>Dismiss</button></div>}
    </div>
    {settingsOpen && <Settings onClose={() => setSettingsOpen(false)} />}
    {creditsOpen && <CreditsPanel onClose={() => setCreditsOpen(false)} />}
    {quitPrompt && <Dialog title="Leave this design?" onClose={() => setQuitPrompt(false)}>
      <p>Your changed camera position has not been saved.</p><div className="game-time-confirm-actions">
        <button className="ghost-btn" onClick={() => setQuitPrompt(false)}>Keep designing</button>
        <button className="ghost-btn" onClick={onQuit}>Discard change</button>
        <button className="primary-btn" disabled={saving} onClick={() => { void save(false).then((ok) => { if (ok) onQuit(); }); }}>Save and leave</button>
      </div>
    </Dialog>}
  </main></GameWindows>;
}
