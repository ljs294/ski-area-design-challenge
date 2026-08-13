import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import type { SavedLift } from '../types/lifts';
import { LiftControl } from './LiftControl';
import type { LiftTool } from './liftControllerModel';

const BASE: [number, number] = [-121.5, 46.9];
const TOP: [number, number] = [-121.49, 46.91];

const callbacks = {
  onArm: vi.fn(),
  onStartPlacement: vi.fn(),
  onTypeChange: vi.fn(),
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
    <LiftControl tool={tool} lifts={lifts} selectedId={selectedId} units="metric" {...callbacks} />,
  );
}

function expectLabeledInput(html: string, label: string, value: string): void {
  const labels = html.match(/<label\b[^>]*>[\s\S]*?<\/label>/g) ?? [];
  const field = labels.find((candidate) => candidate.includes(`>${label}<`));
  expect(field, `expected an input labeled ${label}`).toBeDefined();
  expect(field).toContain('<input');
  expect(field).toContain(`value="${value}"`);
}

describe('LiftControl builder', () => {
  it('opens a fixed-footprint type tree with Double selected and an explicit Draw action', () => {
    const html = render({ phase: 'choosing', liftTypeId: 'fixed-grip-double' });
    expect(html).toContain('lift-builder-panel');
    expect(html).toContain('role="tree"');
    expect(html).toContain('aria-label="Lift type"');
    expect(html).toContain('aria-expanded="true"');
    expect(html).toContain('aria-selected="true"');
    expect(html).toContain('>Double<');
    expect(html).toContain('>Draw lift<');
  });

  it('shows ordered live estimates and a builder-only cost placeholder while anchored', () => {
    const html = render({
      phase: 'anchored',
      liftTypeId: 'detachable-six-pack',
      a: BASE,
      cursor: TOP,
      elev: [1000, 1200],
      anchorElevStatus: 'ok',
      cursorElevStatus: 'ok',
    });
    const labels = ['Length', 'Capacity', 'Vertical', 'Estimated Ride Time', 'Cost'];
    const positions = labels.map((label) => html.indexOf(`>${label}<`));
    expect(positions.every((position) => position >= 0)).toBe(true);
    expect(positions).toEqual([...positions].sort((a, b) => a - b));
    expect(html).toContain('3,000/hr');
    expect(html).toContain('>TBD<');
  });

  it('shows sampling and unavailable states without hiding horizontal estimates', () => {
    const pending = render({ phase: 'anchored', liftTypeId: 'rope-tow', a: BASE, cursor: TOP,
      elev: [1000, null], anchorElevStatus: 'ok', cursorElevStatus: 'pending' });
    expect(pending).toContain('Sampling…');
    expect(pending).toContain('Length');
    const failed = render({ phase: 'anchored', liftTypeId: 'rope-tow', a: BASE, cursor: TOP,
      elev: [1000, null], anchorElevStatus: 'ok', cursorElevStatus: 'error' });
    expect(failed).toContain('Unavailable');
  });

  it('keeps type and naming editable in review', () => {
    const html = render({ phase: 'review', draft: {
      points: [BASE, TOP], elev: [1000, 1200], elevStatus: 'ok',
      liftTypeId: 'gondola-10', status: 'planning', identifier: 'A', name: 'Summit Express',
    } });
    expect(html).toContain('Detachable 10-Person Gondola');
    expect(html).toContain('>Change<');
    expectLabeledInput(html, 'Letter / number', 'A');
    expectLabeledInput(html, 'Name', 'Summit Express');
    expect(html).toContain('>TBD<');
  });
});

describe('LiftControl committed editing', () => {
  const existing: SavedLift = {
    id: 'lift-12',
    identifier: '12',
    name: 'Ridge Gondola',
    liftTypeId: 'gondola-8',
    points: [BASE, TOP],
    endpointElevM: [1000, 1200],
    lengthM: 1500,
    verticalM: 200,
    status: 'complete',
    createdAt: '2026-01-01T00:00:00.000Z',
  };

  it('shows the committed type read-only and omits builder cost', () => {
    const html = render({ phase: 'idle' }, [existing], existing.id);
    expect(html).toContain('Detachable 8-Person Gondola');
    expect(html).not.toContain('>Change<');
    expect(html).not.toContain('>Cost<');
    expectLabeledInput(html, 'Letter / number', '12');
    expectLabeledInput(html, 'Name', 'Ridge Gondola');
  });
});
