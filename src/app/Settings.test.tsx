import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { GAME_ACTION_LABELS, GAME_ACTION_ORDER, DEFAULT_KEYBINDS } from '../keybinds';
import { Settings } from './Settings';
import { SettingsProvider } from './SettingsContext';

// Matches the DOM-free component-test pattern in SnowmakingDashboard.test.tsx /
// NetworkMap.test.tsx: render to a string and assert on markup, so no jsdom is
// needed. renderToStaticMarkup produces static HTML with no live event
// handling, so this is a structural smoke test only — no click/keydown
// simulation (that needs a real DOM, out of house convention here).

function render() {
  return renderToStaticMarkup(
    <SettingsProvider>
      <Settings onClose={vi.fn()} />
    </SettingsProvider>
  );
}

// renderToStaticMarkup HTML-escapes text content, so "Trails & lifts
// dashboard" comes back as "Trails &amp; lifts dashboard".
function expectLabel(html: string, label: string): void {
  expect(html).toContain(label.replace(/&/g, '&amp;'));
}

describe('Settings — Controls section', () => {
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

  it('renders a "Reset controls to defaults" button', () => {
    const html = render();
    expect(html).toContain('Reset controls to defaults');
  });

  it('renders the Controls section title', () => {
    const html = render();
    expect(html).toContain('Controls');
  });
});
