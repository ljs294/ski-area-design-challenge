import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react';
import type { GameSessionControls } from './MapView';
import { MainMenu } from './MainMenu';
import { Settings } from './Settings';
import { LoadGameModal } from './LoadGameModal';
import { ResortLoadingScreen } from './ResortLoadingScreen';
import { SettingsProvider } from './SettingsContext';
import { listGames, loadGame, loadGamePreview, mostRecentGame } from '../gameSaveClient';
import { desktop } from '../desktopBridge';
import type { BootControls, BootEvent, BootProgress } from './resortBoot';
import type { GameSave } from '../types';

const loadMapView = () => import('./MapView');
const MapView = lazy(() => loadMapView().then((module) => ({ default: module.MapView })));
const MapManagement = lazy(() => import('./MapManagement').then((module) => ({ default: module.MapManagement })));
const GraphicsLab = lazy(() => import('./GraphicsLab').then((module) => ({ default: module.GraphicsLab })));

// 'loadingGame' mounts nothing: it exists so the menu (and its live backdrop
// map) is torn down before the save/package lookup begins. The loading screen
// itself is an overlay driven by `boot`, independent of the screen machine, so
// it survives the switch to 'game' and stays up until the resort is drawn.
type Screen = 'menu' | 'newGame' | 'game' | 'loadingGame' | 'mapMgmt' | 'graphicsLab';

/** Everything the resort loading overlay needs; null when no load is running. */
interface BootState {
  title: string;
  progress: BootProgress;
  imageryUrl: string | null;
  imageryKind: 'resume' | 'aerial' | null;
  failure: { message: string; repair: () => void } | null;
  /** True once the resort is ready; the screen fades out before unmounting. */
  done: boolean;
}

const BOOT_FADE_MS = 600;

/**
 * Boot straight into a screen from a deep link, bypassing the menu. The
 * Graphics Lab dev tool opens via `#graphics-lab` (or `?lab`) so it can be
 * launched directly (see electron/main.ts GRAPHICS_LAB env).
 */
function initialScreen(): Screen {
  if (typeof window === 'undefined') return 'menu';
  const hash = window.location.hash.toLowerCase();
  const params = new URLSearchParams(window.location.search);
  if (hash.includes('graphics-lab') || params.has('lab')) return 'graphicsLab';
  return 'menu';
}

function AppInner() {
  const [screen, setScreen] = useState<Screen>(initialScreen);
  const [currentSave, setCurrentSave] = useState<GameSave | null>(null);
  const [hasSaves, setHasSaves] = useState(false);
  const [showSettings, setShowSettings] = useState(false);
  const [showLoad, setShowLoad] = useState(false);
  const [boot, setBoot] = useState<BootState | null>(null);
  // Populated by MapView while a resort boots, so the loading screen's
  // "Enter anyway" / "Back to menu" can drive the warm-up it can't see.
  const bootControlsRef = useRef<BootControls | null>(null);
  const sessionControlsRef = useRef<GameSessionControls | null>(null);
  const bootFadeRef = useRef<number | null>(null);

  useEffect(() => () => {
    if (bootFadeRef.current !== null) window.clearTimeout(bootFadeRef.current);
  }, []);

  useEffect(() => {
    const bridge = desktop;
    if (!bridge) return;
    return bridge.lifecycle.onCloseCheckpointRequested(() => {
      void (async () => {
        try {
          await sessionControlsRef.current?.checkpointForExit(false);
        } finally {
          bridge.lifecycle.completeCloseCheckpoint();
        }
      })();
    });
  }, []);

  const refreshHasSaves = useCallback(() => {
    void listGames().then((l) => setHasSaves(l.length > 0));
  }, []);

  // Keep the Continue lock state fresh whenever we land back on the menu.
  useEffect(() => {
    if (screen === 'menu') refreshHasSaves();
  }, [screen, refreshHasSaves]);

  // Dev shortcut: Ctrl/Cmd+Shift+G toggles the Graphics Lab from anywhere.
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.shiftKey && (e.key === 'g' || e.key === 'G')) {
        e.preventDefault();
        setScreen((s) => (s === 'graphicsLab' ? 'menu' : 'graphicsLab'));
      }
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, []);

  const openSave = useCallback((save: GameSave) => {
    setCurrentSave(save);
    setScreen('game');
  }, []);

  const dismissBoot = useCallback(() => {
    if (bootFadeRef.current !== null) {
      window.clearTimeout(bootFadeRef.current);
      bootFadeRef.current = null;
    }
    bootControlsRef.current = null;
    setBoot(null);
  }, []);

  const toMenu = useCallback(() => {
    setCurrentSave(null);
    setScreen('menu');
  }, []);

  const checkpointToMenu = useCallback(async () => {
    // Unsaved terrain grading and cleared cover die with the session now, so
    // ask before leaving; a cancelled prompt keeps the resort open.
    if ((await sessionControlsRef.current?.confirmExit()) === false) return;
    const result = await sessionControlsRef.current?.checkpointForExit(true);
    if (result && !result.ok) return;
    toMenu();
  }, [toMenu]);

  /** Raise the loading screen, then tear down the menu (and its live backdrop
   *  map) so it cannot issue Terrarium requests during the resume transition. */
  const beginBoot = useCallback(
    async (key: string, title: string) => {
      if (bootFadeRef.current !== null) {
        window.clearTimeout(bootFadeRef.current);
        bootFadeRef.current = null;
      }
      bootControlsRef.current = null;
      setBoot({
        title,
        progress: { stage: 'save' },
        imageryUrl: null,
        imageryKind: null,
        failure: null,
        done: false,
      });
      setScreen('loadingGame');
      const [save, previewUrl] = await Promise.all([loadGame(key), loadGamePreview(key)]);
      if (previewUrl) {
        setBoot((previous) => previous
          ? { ...previous, imageryUrl: previewUrl, imageryKind: 'resume' }
          : previous);
      }
      // From here MapView mounts underneath the still-visible loading screen and
      // takes over reporting via onBoot, all the way to a fully-drawn resort.
      if (save) openSave(save);
      else {
        dismissBoot();
        setScreen('menu');
      }
    },
    [dismissBoot, openSave]
  );

  const handleContinue = useCallback(async () => {
    // Resolve the summary first — an index.json read, no map work — so the
    // loading screen can title itself with the resort name from frame one.
    const recent = await mostRecentGame();
    if (!recent) return;
    await beginBoot(recent.key, recent.name);
  }, [beginBoot]);

  const handleLoadPick = useCallback(
    async (key: string, name: string) => {
      setShowLoad(false);
      if ((await sessionControlsRef.current?.confirmExit()) === false) return;
      const checkpoint = await sessionControlsRef.current?.checkpointForExit(true);
      if (checkpoint && !checkpoint.ok) return;
      await beginBoot(key, name);
    },
    [beginBoot]
  );

  /** MapView's boot reports, folded into the loading screen's state. */
  const handleBoot = useCallback(
    (e: BootEvent) => {
      setBoot((prev) => {
        if (!prev) return prev;
        switch (e.type) {
          case 'progress':
            return { ...prev, progress: e.progress };
          case 'backdrop':
            return prev.imageryKind === 'resume'
              ? prev
              : { ...prev, imageryUrl: e.imageryUrl, imageryKind: 'aerial' };
          case 'failed':
            return { ...prev, failure: { message: e.message, repair: e.repair } };
          case 'ready':
            return { ...prev, done: true };
          case 'handoff':
            // Preparation started; its own package gate owns the screen now.
            return null;
        }
      });
      if (e.type === 'handoff') bootControlsRef.current = null;
      if (e.type === 'ready') {
        bootFadeRef.current = window.setTimeout(() => {
          bootFadeRef.current = null;
          dismissBoot();
        }, BOOT_FADE_MS);
      }
    },
    [dismissBoot]
  );

  const handleBootBack = useCallback(() => {
    bootControlsRef.current?.abort();
    // Drop the overlay before MapView unmounts — it revokes the imagery blob
    // URL the backdrop is showing.
    dismissBoot();
    toMenu();
  }, [dismissBoot, toMenu]);

  return (
    <>
      {screen === 'menu' && (
        <MainMenu
          hasSaves={hasSaves}
          onContinue={() => void handleContinue()}
          onNewGame={() => {
            setCurrentSave(null);
            setScreen('newGame');
          }}
          onLoadGame={() => setShowLoad(true)}
          onMapManagement={() => setScreen('mapMgmt')}
          onSettings={() => setShowSettings(true)}
          onExit={() => desktop?.exit()}
          onPreloadGame={() => { void loadMapView(); }}
        />
      )}

      <Suspense fallback={<div className="route-loading" role="status">Loading…</div>}>
      {screen === 'newGame' && (
        <MapView
          mode="picking"
          onQuit={() => void checkpointToMenu()}
          onOpenSettings={() => setShowSettings(true)}
          onLoadGame={() => setShowLoad(true)}
          sessionControlsRef={sessionControlsRef}
          controlsSuspended={showSettings || showLoad}
        />
      )}

      {screen === 'game' && (
        <MapView
          // Remount when the loaded save changes so Load/Continue reinitialize cleanly.
          key={currentSave?.key ?? 'game'}
          mode="playing"
          initialSave={currentSave}
          onQuit={() => void checkpointToMenu()}
          onOpenSettings={() => setShowSettings(true)}
          onLoadGame={() => setShowLoad(true)}
          onBoot={handleBoot}
          bootControlsRef={bootControlsRef}
          sessionControlsRef={sessionControlsRef}
          controlsSuspended={showSettings || showLoad}
        />
      )}

      {screen === 'mapMgmt' && <MapManagement onBack={toMenu} />}

      {screen === 'graphicsLab' && <GraphicsLab onExit={toMenu} />}
      </Suspense>

      {boot && (
        <ResortLoadingScreen
          title={boot.title}
          progress={boot.progress}
          imageryUrl={boot.imageryUrl}
          imageryKind={boot.imageryKind ?? 'aerial'}
          state={boot.failure ? 'failed' : 'loading'}
          message={boot.failure?.message}
          done={boot.done}
          onBack={handleBootBack}
          // Only offer a forced entry once MapView has handed over a reveal
          // handle — i.e. from the tile preload onward.
          onEnterAnyway={
            boot.progress.stage === 'warm' || boot.progress.stage === 'settle'
              ? () => bootControlsRef.current?.reveal()
              : undefined
          }
          onRepair={boot.failure?.repair}
        />
      )}

      {showLoad && <LoadGameModal onClose={() => setShowLoad(false)} onPick={(k, n) => void handleLoadPick(k, n)} />}
      {showSettings && <Settings onClose={() => setShowSettings(false)}
        resortSettings={sessionControlsRef.current?.resortSettings} />}
    </>
  );
}

export function App() {
  return (
    <SettingsProvider>
      <AppInner />
    </SettingsProvider>
  );
}
