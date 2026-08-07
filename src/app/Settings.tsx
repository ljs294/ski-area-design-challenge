import {
  useEffect,
  useRef,
  useState,
} from 'react';
import type { KeyboardEvent as ReactKeyboardEvent } from 'react';
import { useSettings } from './SettingsContext';
import type { Theme, Units, RenderQuality } from './SettingsContext';
import type { WindowMode } from '../ipcContract';
import { isDesktop } from '../desktopBridge';
import { GAME_ACTION_LABELS, GAME_ACTION_ORDER, actionForKey, normalizeKey } from '../keybinds';
import type { GameAction } from '../keybinds';

export interface ResortSettingsCapability {
  mapContextAvailable: boolean;
  downloadMapContext(
    signal: AbortSignal,
  ): Promise<{ ok: true } | { ok: false; error: string }>;
}

export interface SettingsProps {
  onClose: () => void;
  resortSettings?: ResortSettingsCapability;
}

type SettingsTab = 'general' | 'controls' | 'resort-data';

interface SettingsTabDefinition {
  id: SettingsTab;
  label: string;
}

type MapContextDownloadState =
  | { status: 'idle' }
  | { status: 'downloading' }
  | { status: 'success' }
  | { status: 'error'; error: string };

const GENERAL_TAB: SettingsTabDefinition = { id: 'general', label: 'General' };
const CONTROLS_TAB: SettingsTabDefinition = { id: 'controls', label: 'Controls' };
const RESORT_DATA_TAB: SettingsTabDefinition = { id: 'resort-data', label: 'Resort Data' };

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
        {options.map((option) => (
          <button
            key={option.value}
            className={`seg-btn${value === option.value ? ' seg-btn-active' : ''}`}
            aria-pressed={value === option.value}
            onClick={() => onChange(option.value)}
          >
            {option.label}
          </button>
        ))}
      </div>
    </div>
  );
}

function tabId(tab: SettingsTab): string {
  return `settings-tab-${tab}`;
}

function panelId(tab: SettingsTab): string {
  return `settings-panel-${tab}`;
}

export function Settings({ onClose, resortSettings }: SettingsProps) {
  const {
    settings, setTheme, setUnits, setWindowMode, setReducedMotion, setRenderQuality,
    setKeybind, resetKeybinds,
  } = useSettings();
  const [activeTab, setActiveTab] = useState<SettingsTab>('general');
  const [listeningFor, setListeningFor] = useState<GameAction | null>(null);
  const [conflict, setConflict] = useState<{ action: GameAction; key: string } | null>(null);
  const [mapContextDownload, setMapContextDownload] = useState<MapContextDownloadState>({
    status: 'idle',
  });
  const tabRefs = useRef<Partial<Record<SettingsTab, HTMLButtonElement | null>>>({});
  const downloadController = useRef<AbortController | null>(null);

  const tabs: SettingsTabDefinition[] = resortSettings
    ? [GENERAL_TAB, CONTROLS_TAB, RESORT_DATA_TAB]
    : [GENERAL_TAB, CONTROLS_TAB];

  // Escape closes Settings unless a keybinding listener owns that key, in
  // which case Escape only cancels the in-progress binding.
  useEffect(() => {
    if (listeningFor !== null) return;
    const onKey = (event: KeyboardEvent) => {
      if (event.key === 'Escape') onClose();
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [listeningFor, onClose]);

  // While rebinding, capture the next keydown before MapView's global camera
  // controls can observe it.
  useEffect(() => {
    if (listeningFor === null) return;
    const action = listeningFor;
    const onKey = (event: KeyboardEvent) => {
      event.preventDefault();
      event.stopImmediatePropagation();
      if (event.key === 'Escape') {
        setListeningFor(null);
        return;
      }
      const key = normalizeKey(event.key);
      const owner = actionForKey(settings.keybinds, key);
      if (owner !== null && owner !== action) {
        setConflict({ action: owner, key });
        return;
      }
      setKeybind(action, key);
      setListeningFor(null);
    };
    window.addEventListener('keydown', onKey);
    return () => window.removeEventListener('keydown', onKey);
  }, [listeningFor, settings.keybinds, setKeybind]);

  useEffect(() => {
    if (!conflict) return;
    const timer = setTimeout(() => setConflict(null), 2000);
    return () => clearTimeout(timer);
  }, [conflict]);

  useEffect(() => {
    setConflict(null);
  }, [listeningFor]);

  // Closing Settings cancels provider work that has not reached its commit.
  useEffect(() => () => {
    downloadController.current?.abort();
  }, []);

  // App removes the transient capability when the active MapView goes away.
  // Abort here as well because Settings can briefly outlive that view during a
  // screen transition.
  useEffect(() => {
    if (!resortSettings) {
      downloadController.current?.abort();
      downloadController.current = null;
      setActiveTab('general');
      setMapContextDownload({ status: 'idle' });
    }
  }, [resortSettings]);

  const selectTab = (tab: SettingsTab) => {
    if (tab !== 'controls') {
      setListeningFor(null);
      setConflict(null);
    }
    setActiveTab(tab);
  };

  const onTabKeyDown = (event: ReactKeyboardEvent<HTMLButtonElement>, tab: SettingsTab) => {
    const currentIndex = tabs.findIndex((candidate) => candidate.id === tab);
    let nextIndex: number | null = null;
    if (event.key === 'ArrowRight') nextIndex = (currentIndex + 1) % tabs.length;
    if (event.key === 'ArrowLeft') nextIndex = (currentIndex - 1 + tabs.length) % tabs.length;
    if (event.key === 'Home') nextIndex = 0;
    if (event.key === 'End') nextIndex = tabs.length - 1;
    if (nextIndex === null) return;

    event.preventDefault();
    event.stopPropagation();
    const nextTab = tabs[nextIndex].id;
    selectTab(nextTab);
    tabRefs.current[nextTab]?.focus();
  };

  const downloadMapContext = async () => {
    if (!resortSettings || mapContextDownload.status === 'downloading') return;

    const controller = new AbortController();
    downloadController.current?.abort();
    downloadController.current = controller;
    setMapContextDownload({ status: 'downloading' });

    try {
      const result = await resortSettings.downloadMapContext(controller.signal);
      if (controller.signal.aborted) return;
      setMapContextDownload(result.ok
        ? { status: 'success' }
        : { status: 'error', error: result.error });
    } catch (error) {
      if (controller.signal.aborted) return;
      setMapContextDownload({
        status: 'error',
        error: error instanceof Error ? error.message : 'Map context could not be downloaded.',
      });
    } finally {
      if (downloadController.current === controller) downloadController.current = null;
    }
  };

  const themeOptions: { value: Theme; label: string }[] = [
    { value: 'light', label: 'Light' },
    { value: 'dark', label: 'Dark' },
    { value: 'system', label: 'System' },
  ];
  const unitOptions: { value: Units; label: string }[] = [
    { value: 'imperial', label: 'Feet' },
    { value: 'metric', label: 'Meters' },
  ];
  const qualityOptions: { value: RenderQuality; label: string }[] = [
    { value: 'standard', label: 'Standard' },
    { value: 'high', label: 'High' },
    { value: 'ultra', label: 'Ultra' },
  ];
  const windowOptions: { value: WindowMode; label: string }[] = isDesktop
    ? [
        { value: 'windowed', label: 'Windowed' },
        { value: 'fullscreen', label: 'Fullscreen' },
        { value: 'borderless', label: 'Borderless' },
      ]
    : [
        { value: 'windowed', label: 'Windowed' },
        { value: 'fullscreen', label: 'Fullscreen' },
      ];

  const mapContextAvailable = resortSettings?.mapContextAvailable === true
    || mapContextDownload.status === 'success';

  return (
    <div className="modal-overlay" onClick={onClose}>
      <div className="settings-panel settings-panel-tabbed" onClick={(event) => event.stopPropagation()}>
        <div className="settings-header">
          <h2 className="settings-title">Settings</h2>
          <button className="settings-close-x" aria-label="Close settings" onClick={onClose}>
            ×
          </button>
        </div>

        <div className="settings-tabs" role="tablist" aria-label="Settings sections">
          {tabs.map((tab) => (
            <button
              key={tab.id}
              ref={(element) => { tabRefs.current[tab.id] = element; }}
              id={tabId(tab.id)}
              type="button"
              className={`settings-tab${activeTab === tab.id ? ' settings-tab-active' : ''}`}
              role="tab"
              aria-selected={activeTab === tab.id}
              aria-controls={panelId(tab.id)}
              tabIndex={activeTab === tab.id ? 0 : -1}
              onClick={() => selectTab(tab.id)}
              onKeyDown={(event) => onTabKeyDown(event, tab.id)}
            >
              {tab.label}
            </button>
          ))}
        </div>

        <div
          id={panelId('general')}
          className="settings-body settings-tabpanel"
          role="tabpanel"
          aria-labelledby={tabId('general')}
          hidden={activeTab !== 'general'}
        >
          <Segmented
            label="Theme"
            value={settings.theme}
            options={themeOptions}
            onChange={setTheme}
          />
          <Segmented
            label="Window"
            value={settings.windowMode}
            options={windowOptions}
            onChange={setWindowMode}
          />
          <Segmented
            label="Units"
            value={settings.units}
            options={unitOptions}
            onChange={setUnits}
          />
          <Segmented
            label="Render quality"
            value={settings.renderQuality}
            options={qualityOptions}
            onChange={setRenderQuality}
          />
          <p className="setting-hint">
            Sharper map textures at higher GPU cost. Standard matches your display.
          </p>

          <div className="setting-row">
            <span className="setting-label">Reduced motion</span>
            <label className="switch">
              <input
                type="checkbox"
                checked={settings.reducedMotion}
                onChange={(event) => setReducedMotion(event.target.checked)}
              />
              <span className="switch-track" />
            </label>
          </div>
        </div>

        <div
          id={panelId('controls')}
          className="settings-body settings-tabpanel"
          role="tabpanel"
          aria-labelledby={tabId('controls')}
          hidden={activeTab !== 'controls'}
        >
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
            <p className="setting-hint" role="alert">
              {conflict.key.toUpperCase()} is already used by {GAME_ACTION_LABELS[conflict.action]}
            </p>
          )}
          <button type="button" className="site-btn keybind-reset-btn" onClick={resetKeybinds}>
            Reset controls to defaults
          </button>
        </div>

        {resortSettings && (
          <div
            id={panelId('resort-data')}
            className="settings-body settings-tabpanel resort-data-panel"
            role="tabpanel"
            aria-labelledby={tabId('resort-data')}
            aria-busy={mapContextDownload.status === 'downloading'}
            hidden={activeTab !== 'resort-data'}
          >
            <h3 className="settings-section-title">Map context</h3>
            {mapContextAvailable ? (
              <p className="resort-data-status" role="status">Map context available</p>
            ) : mapContextDownload.status === 'downloading' ? (
              <>
                <p className="resort-data-description" role="status" aria-live="polite">
                  Downloading roads and water for this resort…
                </p>
                <button type="button" className="site-btn resort-data-download-btn" disabled>
                  Downloading Map Context…
                </button>
              </>
            ) : mapContextDownload.status === 'error' ? (
              <>
                <p className="resort-data-error" role="alert">{mapContextDownload.error}</p>
                <button
                  type="button"
                  className="site-btn resort-data-download-btn"
                  onClick={() => void downloadMapContext()}
                >
                  Retry Map Context
                </button>
              </>
            ) : (
              <>
                <p className="resort-data-description">
                  Roads and water are not available for this resort. Download map context to add them.
                </p>
                <button
                  type="button"
                  className="site-btn resort-data-download-btn"
                  onClick={() => void downloadMapContext()}
                >
                  Download Map Context
                </button>
              </>
            )}
          </div>
        )}

        <button className="settings-done-btn" onClick={onClose}>
          Done
        </button>
      </div>
    </div>
  );
}
