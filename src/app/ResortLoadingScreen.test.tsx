import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ResortLoadingScreen } from './ResortLoadingScreen';

describe('ResortLoadingScreen backdrop', () => {
  it('marks an exit snapshot as an unblurred resume image', () => {
    const html = renderToStaticMarkup(
      <ResortLoadingScreen
        title="Alpine"
        progress={{ stage: 'save' }}
        imageryUrl="data:image/jpeg;base64,preview"
        imageryKind="resume"
        state="loading"
        onBack={() => {}}
      />
    );
    expect(html).toContain('resort-loading-photo is-resume');
    expect(html).toContain('data:image/jpeg;base64,preview');
  });

  it('keeps terrain imagery on the aerial treatment by default', () => {
    const html = renderToStaticMarkup(
      <ResortLoadingScreen
        title="Alpine"
        progress={{ stage: 'save' }}
        imageryUrl="blob:aerial"
        state="loading"
        onBack={() => {}}
      />
    );
    expect(html).toContain('resort-loading-photo is-aerial');
  });
});
