import { useEffect, useRef, useState } from 'react';
import { MAP_COLOR_PRESETS } from './mapTheme';
import { useSettings } from './SettingsContext';
import { Icon } from './ui';

const THEME_OPTIONS = [
  { value: 'light', label: 'Light' },
  { value: 'dark', label: 'Dark' },
  { value: 'system', label: 'System' },
] as const;

const CUSTOM_FIELDS = [
  { key: 'paper', label: 'Land' },
  { key: 'water', label: 'Water' },
  { key: 'road', label: 'Roads' },
  { key: 'contour', label: 'Contours' },
  { key: 'text', label: 'Labels' },
] as const;

/** Compact, in-game cartography picker. Settings owns the persisted values. */
export function MapAppearanceMenu() {
  const { settings, setTheme, setMapColorPreset, setCustomMapColor } = useSettings();
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement>(null);
  const buttonRef = useRef<HTMLButtonElement>(null);

  useEffect(() => {
    if (!open) return;
    const onPointerDown = (event: MouseEvent) => {
      if (rootRef.current && !rootRef.current.contains(event.target as Node)) setOpen(false);
    };
    const onKeyDown = (event: KeyboardEvent) => {
      if (event.key !== 'Escape') return;
      event.preventDefault();
      setOpen(false);
      buttonRef.current?.focus();
    };
    window.addEventListener('mousedown', onPointerDown);
    window.addEventListener('keydown', onKeyDown);
    return () => {
      window.removeEventListener('mousedown', onPointerDown);
      window.removeEventListener('keydown', onKeyDown);
    };
  }, [open]);

  return <div className="map-appearance-menu" ref={rootRef}>
    <button
      ref={buttonRef}
      type="button"
      className="ui-icon-button map-appearance-toggle"
      aria-label="Theme and map colors"
      aria-haspopup="dialog"
      aria-expanded={open}
      aria-controls="map-appearance-popover"
      title="Theme and map colors"
      onClick={() => setOpen((value) => !value)}
    >
      <Icon name="palette" />
    </button>

    {open && <section id="map-appearance-popover" className="map-appearance-popover menu-container"
      role="dialog" aria-label="Theme and map colors">
      <section className="map-appearance-section" aria-labelledby="map-theme-title">
        <h2 id="map-theme-title">Theme</h2>
        <div className="map-theme-options" role="group" aria-label="Interface theme">
          {THEME_OPTIONS.map((option) => <button key={option.value} type="button"
            className={settings.theme === option.value ? 'is-selected' : ''}
            aria-pressed={settings.theme === option.value}
            onClick={() => setTheme(option.value)}>{option.label}</button>)}
        </div>
      </section>

      <section className="map-appearance-section" aria-labelledby="map-colors-title">
        <h2 id="map-colors-title">Colors</h2>
        <div className="map-preset-grid" role="group" aria-label="Map color presets">
          {MAP_COLOR_PRESETS.map((preset) => {
            const selected = settings.mapColorPreset === preset.id;
            const swatches = preset.id === 'custom' ? Object.values(settings.customMapColors) : preset.swatches;
            return <button key={preset.id} type="button"
              className={`map-preset${selected ? ' is-selected' : ''}${preset.id === 'custom' ? ' map-preset-custom' : ''}`}
              aria-pressed={selected} onClick={() => setMapColorPreset(preset.id)}>
              <span className="map-preset-label">{preset.id === 'custom' && <Icon name="edit" />}{preset.label}</span>
              <span className="map-preset-swatches" aria-hidden="true">
                {swatches.map((color, index) => <span key={`${color}:${index}`} style={{ backgroundColor: color }} />)}
              </span>
            </button>;
          })}
        </div>
        {settings.mapColorPreset === 'custom' && <div className="map-custom-editor" aria-label="Custom map colors">
          {CUSTOM_FIELDS.map((field) => <label key={field.key}>
            <span>{field.label}</span>
            <input type="color" value={settings.customMapColors[field.key]}
              onChange={(event) => setCustomMapColor(field.key, event.target.value)} />
          </label>)}
        </div>}
      </section>
    </section>}
  </div>;
}
