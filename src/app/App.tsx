import { useCallback, useEffect, useState } from 'react';
import { MapView } from './MapView';
import { MainMenu } from './MainMenu';
import { MapManagement } from './MapManagement';
import { Settings } from './Settings';
import { LoadGameModal } from './LoadGameModal';
import { GraphicsLab } from './GraphicsLab';
import { SettingsProvider } from './SettingsContext';
import { listGames, loadGame, mostRecentGame } from '../gameSaveClient';
import { desktop } from '../desktopBridge';
import type { GameSave } from '../types';

type Screen = 'menu' | 'newGame' | 'game' | 'loadingGame' | 'mapMgmt' | 'graphicsLab';

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

  const handleContinue = useCallback(async () => {
    // Unmount the decorative menu map before asynchronous save/package lookup;
    // otherwise it can issue Terrarium requests during the resume transition.
    setScreen('loadingGame');
    const recent = await mostRecentGame();
    if (!recent) { setScreen('menu'); return; }
    const save = await loadGame(recent.key);
    if (save) openSave(save);
    else setScreen('menu');
  }, [openSave]);

  const handleLoadPick = useCallback(
    async (key: string) => {
      setShowLoad(false);
      setScreen('loadingGame');
      const save = await loadGame(key);
      if (save) openSave(save);
      else setScreen('menu');
    },
    [openSave]
  );

  const toMenu = useCallback(() => {
    setCurrentSave(null);
    setScreen('menu');
  }, []);

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
        />
      )}

      {screen === 'newGame' && (
        <MapView
          mode="picking"
          onQuit={toMenu}
          onOpenSettings={() => setShowSettings(true)}
          onLoadGame={() => setShowLoad(true)}
        />
      )}

      {screen === 'loadingGame' && <div className="menu-loading" aria-label="Loading resort" />}

      {screen === 'game' && (
        <MapView
          // Remount when the loaded save changes so Load/Continue reinitialize cleanly.
          key={currentSave?.key ?? 'game'}
          mode="playing"
          initialSave={currentSave}
          onQuit={toMenu}
          onOpenSettings={() => setShowSettings(true)}
          onLoadGame={() => setShowLoad(true)}
        />
      )}

      {screen === 'mapMgmt' && <MapManagement onBack={toMenu} />}

      {screen === 'graphicsLab' && <GraphicsLab onExit={toMenu} />}

      {showLoad && <LoadGameModal onClose={() => setShowLoad(false)} onPick={(k) => void handleLoadPick(k)} />}
      {showSettings && <Settings onClose={() => setShowSettings(false)} />}
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
