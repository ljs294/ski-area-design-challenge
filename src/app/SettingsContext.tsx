import { createContext, useContext, useEffect, useMemo, useState, useCallback } from 'react';
import type { ReactNode } from 'react';
import { desktop } from '../desktopBridge';
import type { WindowMode } from '../ipcContract';

export type Theme = 'light' | 'dark' | 'system';
export type Units = 'imperial' | 'metric';
export type RenderQuality = 'standard' | 'high' | 'ultra';

export interface Settings {
  theme: Theme;
  units: Units;
  windowMode: WindowMode;
  reducedMotion: boolean;
  renderQuality: RenderQuality;
}

/**
 * Map a quality tier to a MapLibre pixelRatio (canvas supersampling factor).
 * 'standard' matches the display's own DPR (what MapLibre does by default, so
 * Retina/4K displays never regress); higher tiers push above it for a crisper
 * map at the cost of fill-rate. Clamped to never fall below native DPR.
 */
export function pixelRatioFor(quality: RenderQuality): number {
  const dpr = (typeof window !== 'undefined' && window.devicePixelRatio) || 1;
  switch (quality) {
    case 'ultra':
      return Math.max(dpr, 3);
    case 'high':
      return Math.max(dpr, 2);
    default:
      return dpr;
  }
}

const STORAGE_KEY = 'skiapp:settings';

const DEFAULTS: Settings = {
  theme: 'system',
  units: 'imperial',
  windowMode: 'windowed',
  reducedMotion: false,
  renderQuality: 'standard',
};

function loadSettings(): Settings {
  try {
    const raw = localStorage.getItem(STORAGE_KEY);
    if (!raw) return DEFAULTS;
    return { ...DEFAULTS, ...(JSON.parse(raw) as Partial<Settings>) };
  } catch {
    return DEFAULTS;
  }
}

function systemPrefersDark(): boolean {
  return typeof window !== 'undefined' && window.matchMedia('(prefers-color-scheme: dark)').matches;
}

function resolveTheme(theme: Theme, systemDark: boolean): 'light' | 'dark' {
  if (theme === 'system') return systemDark ? 'dark' : 'light';
  return theme;
}

/** Stamp the resolved theme on <html> so all CSS + the map style react to it. */
function applyThemeAttr(resolved: 'light' | 'dark'): void {
  if (typeof document !== 'undefined') document.documentElement.dataset.theme = resolved;
}

// Apply the persisted theme at module load — before React's first paint — so
// there is no light-mode flash when the app starts in dark mode.
applyThemeAttr(resolveTheme(loadSettings().theme, systemPrefersDark()));

interface SettingsContextValue {
  settings: Settings;
  resolvedTheme: 'light' | 'dark';
  setTheme: (t: Theme) => void;
  setUnits: (u: Units) => void;
  setWindowMode: (m: WindowMode) => void;
  setReducedMotion: (v: boolean) => void;
  setRenderQuality: (q: RenderQuality) => void;
}

const SettingsContext = createContext<SettingsContextValue | null>(null);

/** Apply a window mode via the desktop bridge, or the browser Fullscreen API. */
function applyWindowMode(mode: WindowMode): void {
  if (desktop) {
    void desktop.window.setMode(mode);
    return;
  }
  // Web fallback: fullscreen/borderless both map to the Fullscreen API.
  const el = document.documentElement;
  if (mode === 'windowed') {
    if (document.fullscreenElement) void document.exitFullscreen().catch(() => {});
  } else if (!document.fullscreenElement) {
    void el.requestFullscreen?.().catch(() => {});
  }
}

export function SettingsProvider({ children }: { children: ReactNode }) {
  const [settings, setSettings] = useState<Settings>(loadSettings);
  const [systemDark, setSystemDark] = useState(systemPrefersDark);

  // Track OS light/dark so `theme: 'system'` stays live.
  useEffect(() => {
    const mq = window.matchMedia('(prefers-color-scheme: dark)');
    const onChange = () => setSystemDark(mq.matches);
    mq.addEventListener('change', onChange);
    return () => mq.removeEventListener('change', onChange);
  }, []);

  const resolvedTheme = resolveTheme(settings.theme, systemDark);

  // Persist + apply the theme attribute whenever the resolved theme changes.
  useEffect(() => {
    applyThemeAttr(resolvedTheme);
  }, [resolvedTheme]);

  useEffect(() => {
    localStorage.setItem(STORAGE_KEY, JSON.stringify(settings));
  }, [settings]);

  // Re-assert the persisted window mode once on mount (desktop only).
  useEffect(() => {
    if (desktop) applyWindowMode(settings.windowMode);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const setTheme = useCallback((theme: Theme) => setSettings((s) => ({ ...s, theme })), []);
  const setUnits = useCallback((units: Units) => setSettings((s) => ({ ...s, units })), []);
  const setReducedMotion = useCallback(
    (reducedMotion: boolean) => setSettings((s) => ({ ...s, reducedMotion })),
    []
  );
  const setRenderQuality = useCallback(
    (renderQuality: RenderQuality) => setSettings((s) => ({ ...s, renderQuality })),
    []
  );
  const setWindowMode = useCallback((windowMode: WindowMode) => {
    applyWindowMode(windowMode);
    setSettings((s) => ({ ...s, windowMode }));
  }, []);

  const value = useMemo<SettingsContextValue>(
    () => ({ settings, resolvedTheme, setTheme, setUnits, setWindowMode, setReducedMotion, setRenderQuality }),
    [settings, resolvedTheme, setTheme, setUnits, setWindowMode, setReducedMotion, setRenderQuality]
  );

  return <SettingsContext.Provider value={value}>{children}</SettingsContext.Provider>;
}

export function useSettings(): SettingsContextValue {
  const ctx = useContext(SettingsContext);
  if (!ctx) throw new Error('useSettings must be used within a SettingsProvider');
  return ctx;
}
