import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it } from 'vitest';
import { ConstructionStatusBug, type ConstructionActivity } from './ConstructionStatusBug';

describe('ConstructionStatusBug', () => {
  it.each([
    ['lift', 'Building lift…'],
    ['trail', 'Building run…'],
    ['road', 'Building road…'],
  ] as [ConstructionActivity, string][])('labels %s construction', (activity, label) => {
    const html = renderToStaticMarkup(<ConstructionStatusBug activity={activity} />);
    expect(html).toContain('role="status"');
    expect(html).toContain('site-btn-spinner');
    expect(html).toContain(label);
  });
});
