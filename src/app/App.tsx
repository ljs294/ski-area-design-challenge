import { useCallback, useEffect, useState } from 'react';
import { MapView } from './MapView';
import { MainMenu } from './MainMenu';
import { MapManagement } from './MapManagement';
import { Settings } from './Settings';
import { LoadGameModal } from './LoadGameModal';
import { SettingsProvider } from './SettingsContext';
import { listGames, loadGame, mostRecentGame } from '../gameSaveClient';
import { desktop } from '../desktopBridge';
import type { GameSave } from '../types';

type Screen = 'menu' | 'newGame' | 'game' | 'mapMgmt';

function AppInner() {
  const [screen, setScreen] = useState<Screen>('menu');
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

  const openSave = useCallback((save: GameSave) => {
    setCurrentSave(save);
    setScreen('game');
  }, []);

  const handleContinue = useCallback(async () => {
    const recent = await mostRecentGame();
    if (!recent) return;
    const save = await loadGame(recent.key);
    if (save) openSave(save);
  }, [openSave]);

  const handleLoadPick = useCallback(
    async (key: string) => {
      const save = await loadGame(key);
      setShowLoad(false);
      if (save) openSave(save);
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
        <MapView mode="picking" onQuit={toMenu} onOpenSettings={() => setShowSettings(true)} />
      )}

      {screen === 'game' && (
        <MapView
          mode="playing"
          initialSave={currentSave}
          onQuit={toMenu}
          onOpenSettings={() => setShowSettings(true)}
        />
      )}

      {screen === 'mapMgmt' && <MapManagement onBack={toMenu} />}

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
