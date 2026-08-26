import { describe, expect, it, vi } from 'vitest';
import type { SavedLift } from '../types/lifts';
import { formatLiftLabel } from '../lifts';
import { addLiftLayers, LIFT_DRAFT_SOURCE, liftsToGeoJSON } from './liftLayers';

const BASE: [number, number] = [-121.5, 46.9];
const TOP: [number, number] = [-121.49, 46.91];

function lift(overrides: Partial<SavedLift> = {}): SavedLift {
  return {
    id: 'lift-1',
    identifier: 'A',
    name: 'Summit Express',
    liftTypeId: 'fixed-grip-quad',
    points: [BASE, TOP],
    endpointElevM: [1000, 1200],
    lengthM: 1500,
    verticalM: 200,
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
    ...overrides,
  };
}

describe('lift map labels', () => {
  it('hyphenates the identifier and actual name without replacing the name property', () => {
    const value = lift();
    const data = liftsToGeoJSON([value], null);
    const line = data.features.find((feature) =>
      feature.geometry.type === 'LineString' && feature.properties?.draft === false);

    expect(formatLiftLabel(value)).toBe('A - Summit Express');
    expect(line?.properties).toMatchObject({
      name: 'Summit Express',
      identifier: 'A',
      label: 'A - Summit Express',
    });
  });

  it('keeps the legacy name as the label when no identifier was persisted', () => {
    const legacy = lift({ identifier: undefined, name: 'Legacy Double' });
    const data = liftsToGeoJSON([legacy], null);
    const line = data.features.find((feature) =>
      feature.geometry.type === 'LineString' && feature.properties?.draft === false);

    expect(formatLiftLabel(legacy)).toBe('Legacy Double');
    expect(line?.properties).toMatchObject({
      name: 'Legacy Double',
      label: 'Legacy Double',
    });
  });
});

describe('lift layer styling', () => {
  it('uses the compact line widths and reads the composed label property', () => {
    const layers: Array<Record<string, unknown>> = [];
    const map = {
      getSource: vi.fn(() => undefined),
      addSource: vi.fn(),
      addLayer: vi.fn((layer: Record<string, unknown>) => { layers.push(layer); }),
    } as unknown as Parameters<typeof addLiftLayers>[0];

    addLiftLayers(map);

    const layer = (id: string) => {
      const found = layers.find((candidate) => candidate.id === id);
      expect(found, `expected layer ${id}`).toBeDefined();
      return found as {
        paint?: Record<string, unknown>;
        layout?: Record<string, unknown>;
        source?: string;
      };
    };

    expect(layer('lift-line-casing').paint?.['line-width']).toBe(3);
    expect(layer('lift-line-hit').paint).toMatchObject({
      'line-color': 'rgba(0,0,0,0)',
      'line-width': 8,
    });
    expect(layer('lift-line-complete').paint?.['line-width']).toBe(1.5);
    expect(layer('lift-line-planning').paint?.['line-width']).toBe(1.5);
    expect(layer('lift-line-draft').paint?.['line-width']).toBe(1.25);
    expect(layer('lift-line-draft').source).toBe(LIFT_DRAFT_SOURCE);
    expect(layer('lift-draft-terminals').source).toBe(LIFT_DRAFT_SOURCE);
    expect(layer('lift-labels').layout?.['text-field']).toEqual(['get', 'label']);
  });
});
