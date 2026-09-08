import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { advanceSummerToSeptember, createClock } from '../../time-engine/src/timeEngine';
import { DeveloperConsole } from './DeveloperConsole';

describe('DeveloperConsole', () => {
  it('exposes a development-only in-game console affordance', () => {
    const clock = advanceSummerToSeptember(createClock()).clock;
    const html = renderToStaticMarkup(<DeveloperConsole clock={clock} skip={vi.fn()} />);
    expect(html).toContain('&gt;_ DEV');
    expect(html).toContain('Open developer console (` or F10)');
  });
});
