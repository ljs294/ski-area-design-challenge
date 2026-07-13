import { useSettings } from './SettingsContext';
import type { Theme, Units } from './SettingsContext';
import type { WindowMode } from '../ipcContract';
import { isDesktop } from '../desktopBridge';

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
  const { settings, setTheme, setUnits, setWindowMode, setReducedMotion } = useSettings();

  const themeOpts: { value: Theme; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
    { value: 'system', label: 'System' },
  ];
  const unitOpts: { value: Units; label: string }[] = [
    { value: 'imperial', label: 'Feet' },
    { value: 'metric', label: 'Meters' },
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
        </div>

        <button className="settings-done-btn" onClick={onClose}>
          Done
        </button>
      </div>
    </div>
  );
}
