import { useId } from 'react';
import type { WeatherEventType, WeatherEventV1 } from '../../weather-engine/src/index.ts';
import { filterEventsByDate, PRECIPITATION_PHASES } from './weatherLabViewModel.ts';
import type { EventComparisonSeries } from './weatherLabViewModel.ts';

export interface WeatherEventTimelineProps {
  series: EventComparisonSeries;
  startDate?: string;
  endDate?: string;
  title?: string;
}

const SOURCES = Object.freeze([
  { key: 'observed', label: 'Observed', color: '#8aa4bd' },
  { key: 'baseline', label: 'Baseline', color: '#d6ad55' },
  { key: 'candidate', label: 'Simulation', color: '#ff7448' },
] as const);
const EVENT_TYPES = Object.freeze(['storm', 'cold-snap', 'warm-up', 'dry-spell'] satisfies readonly WeatherEventType[]);
const WIDTH = 900;
const LABEL_WIDTH = 154;
const ROW_HEIGHT = 20;

function words(value: string): string {
  return value.split('-').map((part) => `${part.slice(0, 1).toUpperCase()}${part.slice(1)}`).join(' ');
}
function sourceLabel(source: typeof SOURCES[number]): string { return source.label; }

function startOfDate(date: string): number {
  return Date.parse(`${date}T00:00:00Z`);
}

function endOfDate(date: string): number {
  return startOfDate(date) + 86_400_000;
}

function eventStart(event: WeatherEventV1): number {
  const parsed = Date.parse(event.startsAt);
  return Number.isFinite(parsed) ? parsed : startOfDate(event.localStartDate);
}

function eventEnd(event: WeatherEventV1): number {
  const parsed = Date.parse(event.endsAt);
  return Number.isFinite(parsed) ? parsed : endOfDate(event.localEndDate);
}

function eventDetail(event: WeatherEventV1): string {
  const base = `${words(event.severity)} · ${event.durationHours.toFixed(0)} h · percentile ${event.intensityPercentile.toFixed(0)}`;
  if (event.type === 'storm') {
    const phases = PRECIPITATION_PHASES.flatMap((phase) => event.precipitationByPhaseMm[phase] > 0
      ? [`${words(phase)} ${event.precipitationByPhaseMm[phase].toFixed(1)} mm`] : []).join(', ');
    const style = event.stormStyle == null ? 'Unclassified style' : `${words(event.stormStyle)} · ${event.styleConfidence ?? 'unknown'} confidence`;
    return `${base} · ${event.totalPrecipitationMm.toFixed(1)} mm total · ${event.peakPrecipitationMm.toFixed(1)} mm/h peak · ${event.snowfallCm.toFixed(1)} cm snow · ${phases || 'no phase total'} · ${style}`;
  }
  if (event.type === 'cold-snap' || event.type === 'warm-up') return `${base} · ${event.temperatureChangeC.toFixed(1)} °C change`;
  return base;
}

export function WeatherEventTimeline({ series, startDate, endDate, title = 'Weather events' }: WeatherEventTimelineProps) {
  const titleId = useId();
  const activeSources = series.baseline ? SOURCES : SOURCES.filter((source) => source.key !== 'baseline');
  const filtered = Object.fromEntries(activeSources.map((source) => [source.key, filterEventsByDate(series[source.key] ?? [], startDate, endDate)])) as Record<typeof SOURCES[number]['key'], readonly WeatherEventV1[]>;
  const all = activeSources.flatMap((source) => filtered[source.key]);
  const rangeStart = startDate == null ? Math.min(...all.map(eventStart)) : startOfDate(startDate);
  const rangeEnd = endDate == null ? Math.max(...all.map(eventEnd)) : endOfDate(endDate);
  const validRange = Number.isFinite(rangeStart) && Number.isFinite(rangeEnd) && rangeEnd > rangeStart;
  const height = 34 + activeSources.length * EVENT_TYPES.length * ROW_HEIGHT + 24;
  const x = (instant: number) => LABEL_WIDTH + Math.max(0, Math.min(1, (instant - rangeStart) / (rangeEnd - rangeStart))) * (WIDTH - LABEL_WIDTH - 12);

  return <section className="weather-event-timeline" aria-labelledby={titleId}>
    <h2 id={titleId}>{title}</h2>
    {validRange ? <svg viewBox={`0 0 ${WIDTH} ${height}`} role="img" aria-label={`${title} span timeline for observed, Simulation${series.baseline ? ', and pinned Baseline' : ''} series`}>
      <line x1={LABEL_WIDTH} y1="20" x2={WIDTH - 12} y2="20" stroke="currentColor" opacity="0.35"/>
      <text x={LABEL_WIDTH} y="12" fontSize="10">{startDate ?? all.map((event) => event.localStartDate).sort()[0]}</text>
      <text x={WIDTH - 12} y="12" textAnchor="end" fontSize="10">{endDate ?? all.map((event) => event.localEndDate).sort().at(-1)}</text>
      {activeSources.flatMap((source, sourceIndex) => EVENT_TYPES.map((type, typeIndex) => {
        const row = sourceIndex * EVENT_TYPES.length + typeIndex;
        const y = 32 + row * ROW_HEIGHT;
        return <g key={`${source.key}-${type}`}>
          <text x={LABEL_WIDTH - 8} y={y + 11} textAnchor="end" fontSize="10">{sourceLabel(source)} · {words(type)}</text>
          <line x1={LABEL_WIDTH} x2={WIDTH - 12} y1={y + 7} y2={y + 7} stroke="currentColor" opacity="0.08"/>
          {filtered[source.key].filter((event) => event.type === type).map((event) => {
            const left = x(eventStart(event));
            const right = x(eventEnd(event));
            return <rect key={event.id} x={left} y={y + 2} width={Math.max(2, right - left)} height="10" rx="2"
              fill={source.color} opacity={event.severity === 'major' ? 1 : event.severity === 'notable' ? 0.75 : 0.5}>
              <title>{sourceLabel(source)} {words(event.type)}: {event.localStartDate} through {event.localEndDate}; {eventDetail(event)}</title>
            </rect>;
          })}
        </g>;
      }))}
    </svg> : <p role="status">No events are available for this period.</p>}
    <div className="weather-table-scroll">
      <table>
        <caption>{title} details</caption>
        <thead><tr><th scope="col">Series</th><th scope="col">Event</th><th scope="col">Start</th><th scope="col">End</th><th scope="col">Measurements</th></tr></thead>
        <tbody>{all.length === 0
          ? <tr><td colSpan={5}>No detected events.</td></tr>
          : activeSources.flatMap((source) => filtered[source.key].map((event) => <tr key={`${source.key}-${event.id}`}>
            <th scope="row">{sourceLabel(source)}</th>
            <td><span className={`badge ${event.severity}`}>{words(event.type)} · {words(event.severity)}</span></td>
            <td><time dateTime={event.startsAt}>{event.localStartDate}</time></td>
            <td><time dateTime={event.endsAt}>{event.localEndDate}</time></td>
            <td>{eventDetail(event)}{event.styleEvidence.length > 0 && <small>Evidence: {event.styleEvidence.join('; ')}</small>}</td>
          </tr>))}</tbody>
      </table>
    </div>
  </section>;
}
