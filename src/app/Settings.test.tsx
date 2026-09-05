import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { GAME_ACTION_LABELS, GAME_ACTION_ORDER, DEFAULT_KEYBINDS } from '../keybinds';
import { Settings } from './Settings';
import type { ResortSettingsCapability } from './Settings';
import { SettingsProvider } from './SettingsContext';

// The component tests in this directory render without a browser DOM. These
// checks cover the complete tab structure and initial resort-data states;
// keyboard and asynchronous transitions are covered by the browser workflow.
function render(resortSettings?: ResortSettingsCapability) {
  return renderToStaticMarkup(
    <SettingsProvider>
      <Settings onClose={vi.fn()} resortSettings={resortSettings} />
    </SettingsProvider>,
  );
}

function expectLabel(html: string, label: string): void {
  expect(html).toContain(label.replace(/&/g, '&amp;'));
}

const missingMapContext: ResortSettingsCapability = {
  mapContextAvailable: false,
  downloadMapContext: vi.fn(async () => ({ ok: true as const })),
};

const availableMapContext: ResortSettingsCapability = {
  mapContextAvailable: true,
  downloadMapContext: vi.fn(async () => ({ ok: true as const })),
};

describe('Settings tabs', () => {
  it('renders an accessible tab list with General selected by default', () => {
    const html = render();

    expect(html).toContain('role="tablist"');
    expect(html).toContain('aria-label="Settings sections"');
    expect(html).toMatch(
      /id="settings-tab-general"[^>]*role="tab"[^>]*aria-selected="true"[^>]*aria-controls="settings-panel-general"[^>]*tabindex="0"/,
    );
    expect(html).toMatch(
      /id="settings-tab-controls"[^>]*role="tab"[^>]*aria-selected="false"[^>]*aria-controls="settings-panel-controls"[^>]*tabindex="-1"/,
    );
    expect(html).toMatch(
      /id="settings-panel-general"[^>]*role="tabpanel"[^>]*aria-labelledby="settings-tab-general"/,
    );
    expect(html).toMatch(
      /id="settings-panel-controls"[^>]*role="tabpanel"[^>]*aria-labelledby="settings-tab-controls"[^>]*hidden=""/,
    );
  });

  it('keeps general settings in the General panel', () => {
    const html = render();

    for (const label of ['Theme', 'Window', 'Units', 'Render quality', 'Reduced motion']) {
      expectLabel(html, label);
    }
    expectLabel(html, 'US Units');
    expectLabel(html, 'Metric');
  });

  it('keeps Data available for downloaded terrain without an active resort', () => {
    const html = render();

    expect(html).toContain('settings-tab-resort-data');
    expect(html).toContain('settings-panel-resort-data');
    expect(html).not.toContain('Resort Data');
  });
});

describe('Settings Controls tab', () => {
  it('renders one row per action with the correct default key displayed', () => {
    const html = render();
    for (const action of GAME_ACTION_ORDER) {
      expectLabel(html, GAME_ACTION_LABELS[action]);
      expect(html).toContain(`>${DEFAULT_KEYBINDS[action].toUpperCase()}<`);
    }
  });

  it('renders all action labels', () => {
    const html = render();
    expect(GAME_ACTION_ORDER.length).toBe(12);
    for (const action of GAME_ACTION_ORDER) {
      expectLabel(html, GAME_ACTION_LABELS[action]);
    }
  });

  it('renders the reset action', () => {
    expect(render()).toContain('Reset controls to defaults');
  });
});

describe('Settings Resort Data tab', () => {
  it('renders the conditional tab and missing-context recovery action', () => {
    const html = render(missingMapContext);

    expect(html).toMatch(
      /id="settings-tab-resort-data"[^>]*role="tab"[^>]*aria-selected="false"[^>]*aria-controls="settings-panel-resort-data"/,
    );
    expect(html).toMatch(
      /id="settings-panel-resort-data"[^>]*role="tabpanel"[^>]*aria-labelledby="settings-tab-resort-data"[^>]*aria-busy="false"[^>]*hidden=""/,
    );
    expect(html).toContain('Roads and water are not available for this resort.');
    expect(html).toContain('Download Map Context');
    expect(html).not.toContain('Map context available');
  });

  it('reports existing map context without offering a refresh action', () => {
    const html = render(availableMapContext);

    expect(html).toContain('Map context available');
    expect(html).not.toContain('Download Map Context');
    expect(html).not.toContain('Retry Map Context');
  });
});
