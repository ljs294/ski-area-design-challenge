import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { UnsavedChangesModal } from './UnsavedChangesModal';

// The suite renders to static markup (no jsdom), so this covers what the dialog
// offers and how it is labelled; the click/Escape wiring is exercised in the
// running app.
describe('UnsavedChangesModal', () => {
  it('offers save, discard, and cancel, and names terrain work as at risk', () => {
    const html = renderToStaticMarkup(<UnsavedChangesModal saving={false} onChoice={() => {}} />);
    expect(html).toContain('aria-modal="true"');
    expect(html).toContain('Save and leave');
    expect(html).toContain('Discard changes');
    expect(html).toContain('Keep editing');
    expect(html).toContain('graded');
    expect(html).toContain('ground cover');
  });

  it('locks every choice while the save it started is still running', () => {
    const html = renderToStaticMarkup(<UnsavedChangesModal saving onChoice={() => {}} />);
    expect(html).toContain('Saving…');
    expect(html.match(/disabled/g)).toHaveLength(3);
  });
});
