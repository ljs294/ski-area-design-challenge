import { renderToStaticMarkup } from 'react-dom/server';
import { describe, expect, it, vi } from 'vitest';
import { SnowmakingControl, type DamTool, type PondTool } from './SnowmakingControl';

const callbacks = {
  onClose: vi.fn(),
  onArmDam: vi.fn(), onCancelDam: vi.fn(), onDamDraftChange: vi.fn(), onConfirmDam: vi.fn(),
  onSelectDam: vi.fn(), onDeleteDam: vi.fn(), onCloseDam: vi.fn(),
  onArmPond: vi.fn(), onCancelPond: vi.fn(), onUndoPond: vi.fn(), onFinishPond: vi.fn(),
  onPondDraftChange: vi.fn(), onPondElevationChange: vi.fn(), onPondExcavationChange: vi.fn(),
  onConfirmPond: vi.fn(),
  onSelectPond: vi.fn(), onDeletePond: vi.fn(), onClosePond: vi.fn(),
  onPondSnowmakingChange: vi.fn(),
};

function render(damTool: DamTool = { phase: 'idle' }, pondTool: PondTool = { phase: 'idle' },
  units: 'metric' | 'imperial' = 'metric') {
  return renderToStaticMarkup(<SnowmakingControl damTool={damTool} pondTool={pondTool}
    dams={[]} ponds={[]} selectedDam={null} selectedPond={null} units={units} {...callbacks} />);
}

describe('SnowmakingControl', () => {
  it('gathers dam, pond, and pipe construction under one dock', () => {
    const html = render();
    expect(html).toContain('Snowmaking · 0 dams · 0 ponds');
    expect(html).toContain('Build dam');
    expect(html).toContain('Build standalone pond');
    expect(html).toContain('Install snowmaking pipe');
  });

  it('keeps the pipe tool inert until there is a network to build', () => {
    // Placeholder: a disabled button reads as not-yet-ready, where a live one
    // would arm nothing and look broken.
    expect(render()).toMatch(/disabled=""[^>]*>＋ Install snowmaking pipe/);
  });

  it('reviews standalone pond elevation, volume, and lack of natural fill', () => {
    const html = render({ phase: 'idle' }, { phase: 'review', error: null, draft: {
      name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
      topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
      capacityM3: 2160 } });
    expect(html).toContain('Top of pond elevation');
    expect(html).toContain('Snowmaking pond');
    expect(html).toContain('type="checkbox"');
    expect(html).toContain('checked=""');
    expect(html).toContain('Pond volume');
    expect(html).toContain('2.2M L');
    expect(html).toContain('will not fill on its own');
    expect(html).toContain('Build pond');
  });

  it('bills the berm earthwork during standalone pond review', () => {
    const html = render({ phase: 'idle' }, { phase: 'review', error: null, draft: {
      name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
      topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
      capacityM3: 2160, excavationDepthM: 1.5, crestElevationM: 1001.6,
      maxBermHeightM: 4, bermLengthM: 120, maxCutDepthM: 2.5, disturbedAreaM2: 2400,
      earthwork: { cutM3: 1800, fillM3: 1200, balanceM3: 600 } } });
    expect(html).toContain('Excavation below full pool');
    expect(html).toContain('Berm crest elevation');
    expect(html).toContain('Berm length');
    expect(html).toContain('Max berm height');
    expect(html).toContain('Disturbed area');
    expect(html).toContain('1,800 m³'); // cut
    expect(html).toContain('1,200 m³'); // fill
    expect(html).toContain('Surplus cut: 600 m³');
  });

  it('warns when a pond is short of fill', () => {
    const html = render({ phase: 'idle' }, { phase: 'review', error: null, draft: {
      name: 'Pond 1', boundary: [[0, 0], [0, 0.001], [0.001, 0], [0, 0]],
      topElevationM: 1001, areaM2: 1000, averageDepthM: 2, maxDepthM: 3,
      capacityM3: 2160, earthwork: { cutM3: 100, fillM3: 900, balanceM3: -800 } } });
    expect(html).toContain('Short of material: 800 m³');
    expect(html).toContain('Deepen the excavation');
  });

  it('shows the full-pool capacity and gameplay fill time during dam review', () => {
    const html = render({ phase: 'review', error: null, draft: { name: 'Dam 1',
      points: [[0, 0], [0.001, 0]],
      crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
      inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
      areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
      maxDamHeightM: 4 } });
    expect(html).toContain('Gameplay inflow');
    expect(html).toContain('300 L/s');
    expect(html).toContain('Estimated fill time');
    expect(html).toContain('2.0 hr');
    expect(html).toContain('Pond volume');
    expect(html).toContain('2.2M L');
    expect(html).toContain('Dam length');
    expect(html).toContain('Average height');
    expect(html).toContain('2.5 m');
    expect(html).toContain('Build dam');
  });

  it('shows dam pond volume in K/M US gallons for imperial games', () => {
    const html = render({ phase: 'review', error: null, draft: { name: 'Dam 1',
      points: [[0, 0], [0.001, 0]],
      crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
      inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
      areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
      maxDamHeightM: 4 } }, { phase: 'idle' }, 'imperial');
    expect(html).toContain('570.6K gal');
  });

  it('bills the dam embankment as borrow fill and offers a ground preview', () => {
    const html = render({ phase: 'review', error: null, draft: { name: 'Dam 1',
      points: [[0, 0], [0.001, 0]],
      crestElevationM: 1000, damCrestElevationM: 1000.6, streamId: 'way/1', streamName: 'Creek',
      sourceWidthM: 3, inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
      areaM2: 1000, averageDepthM: 2, capacityM3: 2160, averageDamHeightM: 2.5,
      maxDamHeightM: 4, builtLengthM: 60, disturbedAreaM2: 900,
      earthwork: { cutM3: 0, fillM3: 1200, balanceM3: -1200 } } });
    expect(html).toContain('Dam crest elevation');
    expect(html).toContain('Embankment length');
    expect(html).toContain('Short of material: 1,200 m³');
    expect(html).toContain('Highlighted contours show the ground this dam will reshape.');
  });

  it('surfaces a failed dam build in the review panel', () => {
    const html = render({ phase: 'review', error: 'The terrain changed after this grading preview.',
      draft: { name: 'Dam 1', points: [[0, 0], [0.001, 0]],
        crestElevationM: 1000, streamId: 'way/1', streamName: 'Creek', sourceWidthM: 3,
        inflowM3s: 0.3, pondRings: [[[0, 0], [0, 0.001], [0.001, 0], [0, 0]]],
        areaM2: 1000, averageDepthM: 2, capacityM3: 2160, maxDamHeightM: 4 } });
    expect(html).toContain('The terrain changed after this grading preview.');
  });
});
