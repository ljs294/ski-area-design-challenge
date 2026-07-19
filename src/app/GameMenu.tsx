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
  onSave,
  onLoad,
  onSettings,
  onCredits,
  onQuit,
}: {
  canSave: boolean;
  saving: boolean;
  onSave: () => void;
  onLoad: () => void;
  onSettings: () => void;
  onCredits: () => void;
  onQuit: () => void;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    if (!open) return;
    const onDown = (e: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false);
    };
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false);
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
    fn();
  };

  return (
    <div className="game-menu" ref={rootRef}>
      <button
        className="game-menu-btn"
        onClick={() => setOpen((o) => !o)}
        aria-expanded={open}
        aria-haspopup="menu"
        title="Menu"
      >
        <span className="game-menu-icon" aria-hidden="true">☰</span>
        <span className="game-menu-label">Menu</span>
      </button>

      {open && (
        <div className="game-menu-pop" role="menu">
          {canSave && (
            <button
              className="game-menu-item hud-save"
              role="menuitem"
              onClick={pick(onSave)}
              disabled={saving}
            >
              {saving ? 'Saving…' : 'Save'}
            </button>
          )}
          <button className="game-menu-item" role="menuitem" onClick={pick(onLoad)}>
            Load
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
          <div className="game-menu-divider" role="separator" />
          <button className="game-menu-item hud-quit" role="menuitem" onClick={pick(onQuit)}>
            Main Menu
          </button>
        </div>
      )}
    </div>
  );
}
