import { MenuBackdrop } from './MenuBackdrop';
import { isDesktop } from '../desktopBridge';

// Ski-trail difficulty ratings, reused as the visual "difficulty" of each menu
// action. Rendered as the standard trail markers.
export type Rating = 'green' | 'blue' | 'black' | 'double-black';

function RatingChip({ rating }: { rating: Rating }) {
  if (rating === 'green') return <span className="chip chip-circle chip-green" aria-hidden />;
  if (rating === 'blue') return <span className="chip chip-square chip-blue" aria-hidden />;
  if (rating === 'black') return <span className="chip chip-diamond chip-black" aria-hidden />;
  return (
    <span className="chip chip-double" aria-hidden>
      <span className="chip-diamond chip-black" />
      <span className="chip-diamond chip-black" />
    </span>
  );
}

interface MenuItem {
  key: string;
  label: string;
  rating: Rating;
  onClick: () => void;
  disabled?: boolean;
  hidden?: boolean;
}

export interface MainMenuProps {
  hasSaves: boolean;
  onContinue: () => void;
  onNewGame: () => void;
  onLoadGame: () => void;
  onMapManagement: () => void;
  onSettings: () => void;
  onExit: () => void;
}

export function MainMenu({
  hasSaves,
  onContinue,
  onNewGame,
  onLoadGame,
  onMapManagement,
  onSettings,
  onExit,
}: MainMenuProps) {
  const items: MenuItem[] = [
    { key: 'continue', label: 'Continue Game', rating: 'green', onClick: onContinue, disabled: !hasSaves },
    { key: 'new', label: 'New Game', rating: 'green', onClick: onNewGame },
    { key: 'load', label: 'Load Game', rating: 'blue', onClick: onLoadGame },
    { key: 'maps', label: 'Map Management', rating: 'black', onClick: onMapManagement },
    { key: 'settings', label: 'Settings', rating: 'black', onClick: onSettings },
    // Exit can only quit a real desktop window; hidden in the web demo.
    { key: 'exit', label: 'Exit', rating: 'double-black', onClick: onExit, hidden: !isDesktop },
  ];

  return (
    <div className="main-menu">
      <MenuBackdrop />

      <div className="menu-content">
        <div className="menu-logo">
          <svg className="menu-logo-mark" viewBox="0 0 100 80" aria-hidden>
            <path d="M10,70 L50,20 L90,70 Z" fill="none" strokeWidth="3" strokeLinejoin="round" />
            <path d="M35,70 L60,40 L85,70 Z" fill="none" strokeWidth="2" strokeLinejoin="round" />
            <line x1="5" y1="70" x2="95" y2="70" strokeWidth="3" />
          </svg>
          <h1 className="menu-title">Ski Area Design Challenge</h1>
        </div>

        <nav className="trail-sign" aria-label="Main menu">
          <div className="trail-sign-header">Resort Directory</div>
          {items
            .filter((it) => !it.hidden)
            .map((it) => (
              <button
                key={it.key}
                className={`trail-slat${it.disabled ? ' trail-slat-disabled' : ''}`}
                onClick={it.onClick}
                disabled={it.disabled}
              >
                <span className="slat-chip">
                  <RatingChip rating={it.rating} />
                </span>
                <span className="slat-label">{it.label}</span>
                <span className="slat-arrow" aria-hidden>
                  {it.disabled ? '🔒' : '➔'}
                </span>
              </button>
            ))}
        </nav>

        <footer className="menu-footer">v1.0.0 · Luke Small © 2026</footer>
      </div>
    </div>
  );
}
