import { lazy, Suspense, useEffect, useState } from 'react';
import { isDesktop } from '../desktopBridge';
import { mostRecentGame } from '../gameSaveClient';
import type { GameSaveSummary } from '../types';
import { useSettings } from './SettingsContext';
import { renderProfileFor } from './renderProfile';
import { Icon } from './ui';
import './mainMenu.css';

const MenuBackdrop = lazy(() => import('./MenuBackdrop').then((module) => ({ default: module.MenuBackdrop })));
export interface MainMenuProps {
  hasSaves: boolean;
  libraryRevision?: number;
  onContinue(): void;
  onNewGame(): void;
  onLoadGame(): void;
  onSettings(): void;
  onCredits?(): void;
  onExit(): void;
  onPreloadGame?(): void;
}
export function MainMenu(props: MainMenuProps) {
  const { settings } = useSettings();
  const [recent, setRecent] = useState<GameSaveSummary | null>(null);
  useEffect(() => {
    let alive = true;
    void mostRecentGame().then((save) => {
      if (!alive) return;
      setRecent(save);
    }).catch(() => { /* Storage errors are exposed by the resort library. */ });
    return () => { alive = false; };
  }, [props.hasSaves, props.libraryRevision]);
  return <main className="main-menu alpine-home">
    {renderProfileFor(settings.renderQuality).menu === 'css'
      ? <div className="menu-backdrop menu-backdrop-css" />
      : <Suspense fallback={<div className="menu-backdrop menu-backdrop-css" />}><MenuBackdrop /></Suspense>}
    <div className="trail-menu-foreground">
    <section className="trail-signpost">
      <header className="trail-nameplate"><Icon name="resort" /><h1>Mountain Planner</h1></header>
      <nav className="trail-menu-actions" aria-label="Main menu">
        {props.hasSaves && <button className="trail-sign trail-sign-continue" aria-label={`Continue ${recent?.name ?? 'your resort'}`} onClick={props.onContinue}
          onMouseEnter={props.onPreloadGame} onFocus={props.onPreloadGame}>
          <span><strong>Continue</strong><small>{recent?.name ?? 'Your resort'}</small></span><Icon name="arrow" />
        </button>}
        <button className="trail-sign trail-sign-new" onClick={props.onNewGame}
          onMouseEnter={props.onPreloadGame} onFocus={props.onPreloadGame}>New Resort <Icon name="arrow" /></button>
        <button className="trail-sign trail-sign-library" onClick={props.onLoadGame}>My Resorts <Icon name="arrow" /></button>
      </nav>
    </section>
    <footer className="trail-menu-footer"><nav aria-label="Application">
      <button onClick={props.onSettings}>Settings</button><button onClick={props.onCredits}>Credits</button>
      {isDesktop && <button onClick={props.onExit}>Quit</button>}
    </nav></footer>
    </div>
  </main>;
}
