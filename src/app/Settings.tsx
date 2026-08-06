import { useEffect, useState } from 'react';
import { useSettings } from './SettingsContext';
import type { Theme, Units, RenderQuality } from './SettingsContext';
import type { WindowMode } from '../ipcContract';
import { isDesktop } from '../desktopBridge';
import { GAME_ACTION_LABELS, GAME_ACTION_ORDER, actionForKey, normalizeKey } from '../keybinds';
import type { GameAction } from '../keybinds';

/** A segmented row of mutually-exclusive choices. */
function Segmented<T extends string>({
  label,
  value,
  options,
  onChange,
}: {
  label: string;
  value: T;
  options: { value: T; label: string }[];
  onChange: (v: T) => void;
}) {
  return (
    <div className="setting-row">
      <span className="setting-label">{label}</span>
      <div className="segmented">
        {options.map((o) => (
          <button
            key={o.value}
            className={`seg-btn${value === o.value ? ' seg-btn-active' : ''}`}
            aria-pressed={value === o.value}
            onClick={() => onChange(o.value)}
          >
            {o.label}
          </button>
        ))}
      </div>
    </div>
  );
}

export function Settings({ onClose }: { onClose: () => void }) {
  const {
    settings, setTheme, setUnits, setWindowMode, setReducedMotion, setRenderQuality,
    setKeybind, resetKeybinds,
  } = useSettings();

  // Which action (if any) is currently listening for its next keypress, and
  // a transient conflict message when the pressed key already belongs to a
  // different action. Both are local UI state — nothing here is persisted
  // until a non-conflicting key is actually assigned.
  const [listeningFor, setListeningFor] = useState<GameAction | null>(null);
  const [conflict, setConflict] = useState<{ action: GameAction; key: string } | null>(null);

  // Escape closes the whole Settings panel — but not while the Controls
  // rebind listener below owns the keydown (it treats Escape as "cancel
  // listening" and calls stopImmediatePropagation, so this bails out on the
  // same state it's guarded by rather than relying on effect registration
  // order between two independent listeners on the same `window` target).
  useEffect(() => {
    if (listeningFor !== null) return;
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [listeningFor, onClose]);

  // While rebinding, capture the very next keydown anywhere and stop it from
  // reaching anything else (MapView's global camera-control listener sits on
  // the same `window` target underneath this modal).
  useEffect(() => {
    if (listeningFor === null) return;
    const action = listeningFor;
    const onKey = (e: KeyboardEvent) => {
      e.preventDefault();
      e.stopImmediatePropagation();
      if (e.key === 'Escape') {
        setListeningFor(null);
        return;
      }
      const key = normalizeKey(e.key);
      const owner = actionForKey(settings.keybinds, key);
      if (owner !== null && owner !== action) {
        setConflict({ action: owner, key });
        return; // leave listeningFor as-is so the user can try another key
      }
      setKeybind(action, key);
      setListeningFor(null);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [listeningFor, settings.keybinds, setKeybind]);

  // Clear a conflict message shortly after it appears, or as soon as the
  // user starts listening for a different key.
  useEffect(() => {
    if (!conflict) return;
    const t = setTimeout(() => setConflict(null), 2000);
    return () => clearTimeout(t);
  }, [conflict]);
  useEffect(() => {
    setConflict(null);
  }, [listeningFor]);

  const themeOpts: { value: Theme; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
    { value: 'system', label: 'System' },
  ];
  const unitOpts: { value: Units; label: string }[] = [
    { value: 'imperial', label: 'Feet' },
    { value: 'metric', label: 'Meters' },
  ];
  const qualityOpts: { value: RenderQuality; label: string }[] = [
    { value: 'standard', label: 'Standard' },
    { value: 'high', label: 'High' },
    { value: 'ultra', label: 'Ultra' },
  ];
  const windowOpts: { value: WindowMode; label: string }[] = isDesktop
    ? [
        { value: 'windowed', label: 'Windowed' },
        { value: 'fullscreen', label: 'Fullscreen' },
        { value: 'borderless', label: 'Borderless' },
      ]
    : [
        { value: 'windowed', label: 'Windowed' },
        { value: 'fullscreen', label: 'Fullscreen' },
      ];

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="settings-panel" onClick={(e) => e.stopPropagation()}>
        <div className="settings-header">
          <h2 className="settings-title">Settings</h2>
          <button className="settings-close-x" aria-label="Close settings" onClick={onClose}>
            ✕
          </button>
        </div>

        <div className="settings-body">
          <Segmented label="Theme" value={settings.theme} options={themeOpts} onChange={setTheme} />
          <Segmented
            label="Window"
            value={settings.windowMode}
            options={windowOpts}
            onChange={setWindowMode}
          />
          <Segmented label="Units" value={settings.units} options={unitOpts} onChange={setUnits} />

          <Segmented
            label="Render quality"
            value={settings.renderQuality}
            options={qualityOpts}
            onChange={setRenderQuality}
          />
          <p className="setting-hint">Sharper map textures at higher GPU cost. Standard matches your display.</p>

          <div className="setting-row">
            <span className="setting-label">Reduced motion</span>
            <label className="switch">
              <input
                type="checkbox"
                checked={settings.reducedMotion}
                onChange={(e) => setReducedMotion(e.target.checked)}
              />
              <span className="switch-track" />
            </label>
          </div>

          <h3 className="settings-section-title">Controls</h3>
          {GAME_ACTION_ORDER.map((action) => (
            <div className="setting-row" key={action}>
              <span className="setting-label">{GAME_ACTION_LABELS[action]}</span>
              <button
                type="button"
                className={`keybind-btn${listeningFor === action ? ' keybind-btn-listening' : ''}`}
                onClick={() => setListeningFor(action)}
              >
                {listeningFor === action ? 'Press a key…' : settings.keybinds[action].toUpperCase()}
              </button>
            </div>
          ))}
          {conflict && (
            <p className="setting-hint">
              {conflict.key.toUpperCase()} is already used by {GAME_ACTION_LABELS[conflict.action]}
            </p>
          )}
          <button type="button" className="site-btn keybind-reset-btn" onClick={resetKeybinds}>
            Reset controls to defaults
          </button>
        </div>

        <button className="settings-done-btn" onClick={onClose}>
          Done
        </button>
      </div>
    </div>
  );
}
