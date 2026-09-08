import { lazy, Suspense, useEffect, useLayoutEffect, useRef, useState } from 'react';
import { isDesktop } from '../desktopBridge';
import { mostRecentGame } from '../gameSaveClient';
import type { GameSaveSummary } from '../types';
import { useSettings } from './SettingsContext';
import { renderProfileFor } from './renderProfile';
import { Icon } from './ui';
import './mainMenu.css';

const MenuBackdrop = lazy(() => import('./MenuBackdrop').then((module) => ({ default: module.MenuBackdrop })));
function TrailRating({ kind }: { kind: 'green' | 'blue' | 'black' }) {
  return <svg className="trail-rating" viewBox="0 0 32 32" aria-hidden="true" data-rating={kind}>
    <rect width="32" height="32" rx="2" fill="#fff9e9" />
    {kind === 'green' ? <circle cx="16" cy="16" r="10" fill="#247343" />
      : kind === 'blue' ? <path d="M6 6h20v20H6z" fill="#235f99" />
      : <path d="M16 3 29 16 16 29 3 16z" fill="#151b18" />}
  </svg>;
}
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
  const rootRef = useRef<HTMLElement>(null);
  useLayoutEffect(() => {
    const root = rootRef.current!;
    const sign = root.querySelector<HTMLElement>('.trail-signpost')!;
    const update = () => {
      const rect = sign.getBoundingClientRect(), parent = root.getBoundingClientRect();
      const scale = Number(getComputedStyle(root).getPropertyValue('--ui-scale')) || 1;
      root.style.setProperty('--post-top', `${Math.max(0, rect.top - parent.top + 10 * scale)}px`);
      root.style.setProperty('--post-left', `${rect.left - parent.left + 38 * scale}px`);
      root.style.setProperty('--post-width', `${24 * scale}px`);
      root.style.setProperty('--post-second-left', `${rect.right - parent.left - 62 * scale}px`);
    };
    update();
    const observer = new ResizeObserver(update); observer.observe(sign); observer.observe(root);
    root.addEventListener('scroll', update, true);
    return () => { observer.disconnect(); root.removeEventListener('scroll', update, true); };
  }, []);
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
  return <main ref={rootRef} className="main-menu alpine-home">
    {renderProfileFor(settings.renderQuality).menu === 'css'
      ? <div className="menu-backdrop menu-backdrop-css" />
      : <Suspense fallback={<div className="menu-backdrop menu-backdrop-css" />}><MenuBackdrop /></Suspense>}
    <div className="trail-timber-post" aria-hidden="true" />
    <div className="trail-timber-post trail-timber-post-second" aria-hidden="true" />
    <div className="trail-menu-foreground">
    <section className="trail-signpost">
      <header className="trail-nameplate"><Icon name="resort" /><h1>Ski Area Design Challenge</h1></header>
      <nav className="trail-menu-actions" aria-label="Main menu">
        {props.hasSaves && <button className="trail-sign trail-sign-continue" aria-label={`Continue ${recent?.name ?? 'your resort'}`} onClick={props.onContinue}
          onMouseEnter={props.onPreloadGame} onFocus={props.onPreloadGame}>
          <TrailRating kind="green" /><span><strong>Continue</strong><small>{recent?.name ?? 'Your resort'}</small></span><Icon name="arrow" />
        </button>}
        <button className="trail-sign trail-sign-new" onClick={props.onNewGame}
          onMouseEnter={props.onPreloadGame} onFocus={props.onPreloadGame}><TrailRating kind="blue" /><span>New Resort</span><Icon name="arrow" /></button>
        <button className="trail-sign trail-sign-library" onClick={props.onLoadGame}><TrailRating kind="black" /><span>My Resorts</span><Icon name="arrow" /></button>
      </nav>
    </section>
    <footer className="trail-menu-footer"><nav aria-label="Application">
      <button onClick={props.onSettings}>Settings</button><button onClick={props.onCredits}>Credits</button>
      {isDesktop && <button onClick={props.onExit}>Quit</button>}
    </nav></footer>
    </div>
  </main>;
}
