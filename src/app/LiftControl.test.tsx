import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { SavedLift } from '../types/lifts';
import { LiftControl } from './LiftControl';
import type { LiftTool } from './liftControllerModel';

const BASE: [number, number] = [-121.5, 46.9];
const TOP: [number, number] = [-121.49, 46.91];

const callbacks = {
  onArm: vi.fn(),
  onCancel: vi.fn(),
  onDraftChange: vi.fn(),
  onConfirm: vi.fn(),
  onSelect: vi.fn(),
  onEditPatch: vi.fn(),
  onCloseEdit: vi.fn(),
  onDelete: vi.fn(),
  onRetryElevation: vi.fn(),
};

function render(tool: LiftTool, lifts: SavedLift[] = [], selectedId: string | null = null): string {
  return renderToStaticMarkup(
    <LiftControl
      tool={tool}
      lifts={lifts}
      selectedId={selectedId}
      units="metric"
      {...callbacks}
    />,
  );
}

function expectLabeledInput(html: string, label: string, value: string): void {
  const labels = html.match(/<label\b[^>]*>[\s\S]*?<\/label>/g) ?? [];
  const field = labels.find((candidate) => candidate.includes(`>${label}<`));
  expect(field, `expected an input labeled ${label}`).toBeDefined();
  expect(field).toContain('<input');
  expect(field).toContain(`value="${value}"`);
}

describe('LiftControl naming fields', () => {
  it('labels the identifier and actual-name inputs while reviewing a new lift', () => {
    const tool: LiftTool = {
      phase: 'review',
      draft: {
        points: [BASE, TOP],
        elev: [1000, 1200],
        elevStatus: 'ok',
        chairSize: 4,
        status: 'planning',
        identifier: 'A',
        name: 'Summit Express',
      },
    };

    const html = render(tool);

    expectLabeledInput(html, 'Letter / number', 'A');
    expectLabeledInput(html, 'Name', 'Summit Express');
  });

  it('labels and populates both inputs while editing an existing lift', () => {
    const existing: SavedLift = {
      id: 'lift-12',
      identifier: '12',
      name: 'Ridge Double',
      liftClass: 'fixed-grip',
      points: [BASE, TOP],
      endpointElevM: [1000, 1200],
      lengthM: 1500,
      verticalM: 200,
      chairSize: 2,
      status: 'complete',
      createdAt: '2026-01-01T00:00:00.000Z',
    };

    const html = render({ phase: 'idle' }, [existing], existing.id);

    expectLabeledInput(html, 'Letter / number', '12');
    expectLabeledInput(html, 'Name', 'Ridge Double');
  });
});
