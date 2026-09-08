import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { MapViewChrome, type MapViewChromeProps } from './MapViewChrome';

function props(): MapViewChromeProps {
  return {
    checkpointError: null,
    dismissCheckpointError: vi.fn(),
    unsaved: null,
    packageGate: null,
    localBoot: null,
    menu: {
      canSave: false, saving: false, unsaved: false, onSave: vi.fn(), onLoad: vi.fn(),
      onSettings: vi.fn(), onCredits: vi.fn(), onQuit: vi.fn(),
    },
    searchResult: null,
    siteControl: null,
    view3D: null,
    buildingActivity: null,
    dashboard: null,
    readout: null,
    dock: null,
    nameEntry: null,
    stats: null,
    closeCredits: null,
    developerConsole: null,
  };
}

describe('map context preparation decision', () => {
  it('offers retry, explicit continuation, and cancellation', () => {
    const value = props();
    value.packageGate = {
      state: 'preparing', progress: null, error: null,
      mapContextError: 'Both providers failed.', cancel: vi.fn(), back: vi.fn(),
      prepare: vi.fn(), decideMapContext: vi.fn(),
    };
    const html = renderToStaticMarkup(<MapViewChrome {...value} />);
    expect(html).toContain('Map context unavailable');
    expect(html).toContain('Retry Map Context');
    expect(html).toContain('Continue Without Map Context');
    expect(html).toContain('Cancel');
  });
});
