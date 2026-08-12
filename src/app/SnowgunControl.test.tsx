import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { SavedSnowgun, SavedSnowmakingNode } from '../types/snowmaking';
import { SnowgunInspector, SnowgunToolPanel } from './SnowgunControl';
import type { SnowgunPlanPreview } from './useSnowgunController';

const emptyPreview: SnowgunPlanPreview = {
  items: [], candidate: null, totalUsd: 0, connectedCount: 0, disconnectedCount: 0,
};
const callbacks = {
  setVariant: vi.fn(), removeDraft: vi.fn(), review: vi.fn(), back: vi.fn(),
  confirm: vi.fn(), cancel: vi.fn(), confirmMove: vi.fn(),
};

describe('SnowgunControl', () => {
  it('offers all four catalog variants and their quote prices', () => {
    const html = renderToStaticMarkup(<SnowgunToolPanel
      tool={{ phase: 'placing', variantId: 'HKD_ImpulseR5_10s', items: [], cursor: null, error: null }}
      preview={emptyPreview} units="imperial" {...callbacks} />);
    expect(html).toContain('HKD Impulse R5 10 ft Sled');
    expect(html).toContain('HKD Impulse R5 10 ft Tower');
    expect(html).toContain('HKD Impulse R5 20 ft Tower');
    expect(html).toContain('HKD Impulse R5 30 ft Tower');
    expect(html).toContain('$9,000');
  });

  it('keeps the mixed review cart visible with totals and textual disconnection warnings', () => {
    const preview: SnowgunPlanPreview = { items: [
      { draftId: 'a', variantId: 'HKD_ImpulseR5_10s', point: [0, 0], elevM: 1,
        hydrantId: 'h1', hydrantLabel: 'Hydrant 1', hoseDistanceM: 2 },
      { draftId: 'b', variantId: 'HKD_ImpulseR5_20t', point: [1, 1], elevM: null,
        hydrantId: null, hydrantLabel: null, hoseDistanceM: null },
    ], candidate: null, totalUsd: 15_000, connectedCount: 1, disconnectedCount: 1 };
    const html = renderToStaticMarkup(<SnowgunToolPanel
      tool={{ phase: 'review', variantId: 'HKD_ImpulseR5_20t', items: preview.items,
        revision: 2, error: null }}
      preview={preview} units="metric" {...callbacks} />);
    expect(html).toContain('R5 10S');
    expect(html).toContain('R5 20 ft Tower');
    expect(html).toContain('$15,000');
    expect(html).toContain('Disconnected guns will be built');
    expect(html).toContain('Remove HKD Impulse R5 20 ft Tower from plan');
  });

  it('allows moving sleds while explaining that towers are fixed', () => {
    const nodes: SavedSnowmakingNode[] = [];
    const base: Omit<SavedSnowgun, 'variantId'> = { id: 'gun', point: [0, 0], elevM: null,
      hydrantId: null, createdAt: 'now' };
    const sled = renderToStaticMarkup(<SnowgunInspector gun={{ ...base,
      variantId: 'HKD_ImpulseR5_10s' }} nodes={nodes} units="metric"
      close={vi.fn()} move={vi.fn()} remove={vi.fn()} />);
    const tower = renderToStaticMarkup(<SnowgunInspector gun={{ ...base,
      variantId: 'HKD_ImpulseR5_30t' }} nodes={nodes} units="metric"
      close={vi.fn()} move={vi.fn()} remove={vi.fn()} />);
    expect(sled).toContain('Move sled');
    expect(tower).not.toContain('Move sled');
    expect(tower).toContain('Tower guns are permanent');
  });
});
