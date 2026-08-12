import { useLayoutEffect, useRef, useState } from 'react';
import { fmtDistance } from '../lifts';
import type { SnowmakingSegmentAnalysisResult } from '../snowmakingHydraulics';
import type { SnowmakingPipeStats } from '../snowmakingNetwork';
import type { SavedSnowmakingPipe } from '../types/snowmaking';
import type { Units } from './SettingsContext';

const NUMBER = new Intl.NumberFormat('en-US', { maximumFractionDigits: 1 });

export interface SnowmakingPipeHoverState {
  pipe: SavedSnowmakingPipe;
  segmentId: string;
  segmentIndex: number;
  segmentStats: SnowmakingPipeStats;
  point: { x: number; y: number };
  analysis: SnowmakingSegmentAnalysisResult | null;
  direction: { from: string; to: string } | null;
}

export function SnowmakingPipeHoverDetails({ hover, units, compact = false }: {
  hover: SnowmakingPipeHoverState;
  units: Units;
  compact?: boolean;
}) {
  const result = hover.analysis;
  return <section className={`snowmaking-pipe-hover-details${compact ? ' is-compact' : ''}`}
    aria-label="Hovered pipe properties">
    <div className="snowmaking-pipe-hover-title"><span>Pipe segment</span>
      <strong>{hover.pipe.name} · {hover.segmentIndex + 1}</strong></div>
    <div className="snowmaking-pipe-hover-grid">
      <div><span>Diameter</span><strong>{hover.pipe.diameterIn}&quot;</strong></div>
      <div><span>Length</span><strong>{fmtDistance(hover.segmentStats.lengthM, units)}</strong></div>
      <div><span>Vertical</span><strong>{hover.segmentStats.verticalM == null
        ? '—' : fmtDistance(hover.segmentStats.verticalM, units)}</strong></div>
      {result && <>
        {hover.direction && <div className="snowmaking-pipe-hover-direction"><span>Flow direction</span>
          <strong>{hover.direction.from} → {hover.direction.to}</strong></div>}
        <div><span>Flow</span><strong>{NUMBER.format(Math.abs(result.flowGpm))} GPM</strong></div>
        <div><span>Pressure</span><strong>{NUMBER.format(result.upstreamPressurePsi)} →{' '}
          {NUMBER.format(result.downstreamPressurePsi)} PSI</strong></div>
        <div><span>Friction</span><strong>{NUMBER.format(result.frictionHeadFt)} ft</strong></div>
      </>}
    </div>
  </section>;
}

export function SnowmakingPipeTooltip({ hover, units }: {
  hover: SnowmakingPipeHoverState;
  units: Units;
}) {
  const ref = useRef<HTMLDivElement | null>(null);
  const [position, setPosition] = useState({ left: hover.point.x + 14, top: hover.point.y + 14 });
  useLayoutEffect(() => {
    const rect = ref.current?.getBoundingClientRect();
    const width = rect?.width ?? 260;
    const height = rect?.height ?? 150;
    setPosition({
      left: Math.max(12, Math.min(hover.point.x + 14, window.innerWidth - width - 12)),
      top: Math.max(12, Math.min(hover.point.y + 14, window.innerHeight - height - 12)),
    });
  }, [hover]);
  return <div ref={ref} className="snowmaking-pipe-tooltip" role="tooltip"
    style={position} data-segment-id={hover.segmentId}>
    <SnowmakingPipeHoverDetails hover={hover} units={units} compact />
  </div>;
}
