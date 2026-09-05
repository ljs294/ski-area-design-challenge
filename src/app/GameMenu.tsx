import { Icon } from './ui';
import { useEffect, useRef, useState } from 'react';

/**
 * Single top-right app menu for the in-game view: Save / Load / Settings /
 * Credits / Main Menu. Replaces the old bottom HUD cluster. Save appears only
 * once the resort is saved (`canSave`). Closes on outside-click or Escape.
 * Key item classNames (`hud-save`, `hud-settings`, `hud-quit`) are preserved for
 * the E2E harnesses — they open this menu, then click the item.
 */
export function GameMenu({
  canSave,
  saving,
  unsaved,
  onSave,
  onLoad,
  onSettings,
  onCredits,
  onRebuildCover,
  onQuit,
}: {
  canSave: boolean;
  saving: boolean;
  /** Live design or terrain edits that have not reached the disk. */
  unsaved: boolean;
  onSave: () => void;
  onLoad: () => void;
  onSettings: () => void;
  onCredits: () => void;
  onRebuildCover?: () => void;
  onQuit: () => void;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    rootRef.current?.querySelector<HTMLElement>('[role=menuitem]:not(:disabled)')?.focus();
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { setOpen(false); rootRef.current?.querySelector<HTMLButtonElement>('.game-menu-btn')?.focus(); }
      if (['ArrowDown', 'ArrowUp', 'Home', 'End'].includes(e.key)) {
        const items = Array.from(rootRef.current?.querySelectorAll<HTMLButtonElement>('[role=menuitem]:not(:disabled)') ?? []);
        const index = items.indexOf(document.activeElement as HTMLButtonElement);
        const next = e.key === 'Home' ? 0 : e.key === 'End' ? items.length - 1
          : (index + (e.key === 'ArrowDown' ? 1 : -1) + items.length) % items.length;
        e.preventDefault(); items[next]?.focus();
      }
    };
    window.addEventListener('mousedown', onDown);
    window.addEventListener('keydown', onKey);
    return () => {
      window.removeEventListener('mousedown', onDown);
      window.removeEventListener('keydown', onKey);
    };
  }, [open]);

  // Run an item's action and close the menu.
  const pick = (fn: () => void) => () => {
    setOpen(false);
    rootRef.current?.querySelector<HTMLButtonElement>('.game-menu-btn')?.focus();
    fn();
  };

  return (
    <div className="game-menu" ref={rootRef}>
      <button
        className="game-menu-btn"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-haspopup="menu"
        title={unsaved ? 'Menu — unsaved changes' : 'Menu'}
      >
        <Icon name="menu" />
        <span className="game-menu-label">Menu</span>
        {unsaved && <span className="game-menu-dot" aria-label="Unsaved changes" role="status" />}
      </button>

      {open && (
        <div className="game-menu-pop" role="menu">
          <div className="game-menu-head">
            <span className="game-menu-head-title">Menu</span>
            <button
              className="settings-close-x"
              aria-label="Close menu"
              onClick={() => setOpen(false)}
            >
              ✕
            </button>
          </div>
          {canSave && (
            <button
              className="game-menu-item hud-save"
              role="menuitem"
              onClick={pick(onSave)}
              disabled={saving}
            >
              {saving ? 'Saving…' : unsaved ? 'Save •' : 'Save'}
            </button>
          )}
          <button className="game-menu-item" role="menuitem" onClick={pick(onLoad)}>
            My Resorts
          </button>
          <button
            className="game-menu-item hud-settings"
            role="menuitem"
            onClick={pick(onSettings)}
          >
            Settings
          </button>
          <button className="game-menu-item" role="menuitem" onClick={pick(onCredits)}>
            Credits
          </button>
          {onRebuildCover && (
            <button className="game-menu-item" role="menuitem" onClick={pick(onRebuildCover)}>
              Rebuild Detailed Tree Cover
            </button>
          )}
          <div className="game-menu-divider" role="separator" />
          <button className="game-menu-item hud-quit" role="menuitem" onClick={pick(onQuit)}>
            Main Menu
          </button>
        </div>
      )}
    </div>
  );
}
