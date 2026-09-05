import { lazy, Suspense, useEffect, useState } from 'react';
import { isDesktop } from '../desktopBridge';
import { loadGamePreview, mostRecentGame } from '../gameSaveClient';
import type { GameSaveSummary } from '../types';
import { useSettings } from './SettingsContext';
import { renderProfileFor } from './renderProfile';
import { Icon } from './ui';

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
  const [preview, setPreview] = useState<string | null>(null);
  useEffect(() => {
    let alive = true;
    void mostRecentGame().then(async (save) => {
      if (!alive) return;
      setRecent(save); setPreview(null);
      if (save) { const image = await loadGamePreview(save.key); if (alive) setPreview(image); }
    }).catch(() => { /* Storage errors are exposed by the resort library. */ });
    return () => { alive = false; };
  }, [props.hasSaves, props.libraryRevision]);
  return <main className="main-menu alpine-home">
    {renderProfileFor(settings.renderQuality).menu === 'css'
      ? <div className="menu-backdrop menu-backdrop-css" />
      : <Suspense fallback={<div className="menu-backdrop menu-backdrop-css" />}><MenuBackdrop /></Suspense>}
    <div className="home-scrim" />
    <header className="home-brand"><Icon name="resort" /><span>Mountain Planner</span><span className="home-edition">Ski Area Design Challenge</span></header>
    <section className="home-content">
      <p className="ui-eyebrow">A mountain of possibilities</p>
      <h1>Your mountain.<br />Your design.</h1>
      <p className="home-intro">Shape the slopes. Connect the summit.<br />Watch your mountain come to life.</p>
      <nav className="home-actions" aria-label="Main menu">
        {props.hasSaves && <button className="home-resume" aria-label={`Continue ${recent?.name ?? 'your resort'}`} onClick={props.onContinue}
          onMouseEnter={props.onPreloadGame} onFocus={props.onPreloadGame}>
          {preview && <img src={preview} alt="" />}
          <span><small>Continue</small><strong>{recent?.name ?? 'Your resort'}</strong></span><Icon name="arrow" />
        </button>}
        <button className="ui-button ui-button-primary" onClick={props.onNewGame}
          onMouseEnter={props.onPreloadGame} onFocus={props.onPreloadGame}>New Resort <Icon name="arrow" /></button>
        <button className="ui-button" onClick={props.onLoadGame}>My Resorts</button>
      </nav>
    </section>
    <footer className="home-footer"><span>Explore · Build · Refine</span><nav aria-label="Application">
      <button onClick={props.onSettings}>Settings</button><button onClick={props.onCredits}>Credits</button>
      {isDesktop && <button onClick={props.onExit}>Quit</button>}
    </nav></footer>
  </main>;
}