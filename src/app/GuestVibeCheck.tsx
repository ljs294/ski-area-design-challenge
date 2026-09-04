import type { CSSProperties } from 'react';

/** The three labels used by the presentation layer for guest sentiment. */
export type GuestVibeSentiment = 'positive' | 'neutral' | 'negative';

/** One already-aggregated reason row. Keep aggregation out of this component. */
export interface GuestVibeReasonAggregate {
  readonly reasonCode: string;
  readonly label: string;
  readonly count: number;
  readonly sentiment: GuestVibeSentiment;
}

/** A representative thought selected by the caller from its aggregate. */
export interface GuestVibeTopThought {
  readonly text: string;
  readonly reasonCode: string;
  readonly sentiment: GuestVibeSentiment;
  readonly count?: number;
}

/** The small amount of per-guest information needed by the bounded picker. */
export interface GuestVibeGuest {
  readonly id: string;
  readonly label?: string;
  readonly status: string;
  readonly satisfaction?: number;
  readonly sentiment?: GuestVibeSentiment;
  readonly latestThought?: string;
}

export interface GuestVibeSummary {
  readonly guestCount: number;
  readonly activeGuestCount: number;
  readonly positiveThoughtCount: number;
  readonly neutralThoughtCount: number;
  readonly negativeThoughtCount: number;
  readonly activeIncidentCount?: number;
  readonly resolvedIncidentCount?: number;
  readonly patrolQueueCount?: number;
  readonly safetyRate?: number;
}

export interface GuestVibeCheckProps {
  readonly summary: GuestVibeSummary;
  readonly reasonAggregates: readonly GuestVibeReasonAggregate[];
  readonly topThoughts: readonly GuestVibeTopThought[];
  readonly guests: readonly GuestVibeGuest[];
  /** Controlled selection; the parent owns map focus and selected-guest state. */
  readonly selectedGuestId?: string | null;
  readonly onSelectGuest: (guestId: string) => void;
  readonly onClearSelectedGuest?: () => void;
  /** Maximum number of guest buttons shown at once. Values are clamped to 1..12. */
  readonly maxGuests?: number;
  readonly title?: string;
  readonly description?: string;
}

const DEFAULT_MAX_GUESTS = 8;
const MAX_REASON_ROWS = 6;
const MAX_TOP_THOUGHTS = 4;

const SENTIMENT_TOKENS: Record<GuestVibeSentiment, { color: string; background: string }> = {
  positive: { color: 'var(--accent)', background: 'color-mix(in srgb, var(--accent) 14%, transparent)' },
  neutral: { color: 'var(--text-muted)', background: 'var(--seg-bg)' },
  negative: { color: 'var(--danger)', background: 'color-mix(in srgb, var(--danger) 14%, transparent)' },
};

const columnStyle: CSSProperties = {
  display: 'flex',
  flexDirection: 'column',
  gap: 8,
  minWidth: 0,
};

const sectionTitleStyle: CSSProperties = {
  margin: 0,
  color: 'var(--text)',
  fontSize: 11,
  fontWeight: 800,
  letterSpacing: '0.06em',
  textTransform: 'uppercase',
};

const mutedStyle: CSSProperties = {
  margin: 0,
  color: 'var(--text-muted)',
  fontSize: 12,
  lineHeight: 1.35,
};

const cardStyle: CSSProperties = {
  border: '1px solid var(--border)',
  borderRadius: 8,
  background: 'var(--surface-solid)',
};

function count(value: number): number {
  return Number.isFinite(value) ? Math.max(0, Math.round(value)) : 0;
}

function formatCount(value: number): string {
  return count(value).toLocaleString();
}

function clampGuestLimit(value: number | undefined): number {
  if (!Number.isFinite(value)) return DEFAULT_MAX_GUESTS;
  return Math.min(12, Math.max(1, Math.trunc(value as number)));
}

function reasonLabel(reasonCode: string): string {
  return reasonCode.replace(/[-_]+/g, ' ').replace(/\b\w/g, (letter) => letter.toUpperCase());
}

function sentimentLabel(sentiment: GuestVibeSentiment): string {
  return sentiment.charAt(0).toUpperCase() + sentiment.slice(1);
}

function SentimentPill({ sentiment }: { sentiment: GuestVibeSentiment }) {
  const token = SENTIMENT_TOKENS[sentiment];
  const style: CSSProperties = {
    display: 'inline-flex',
    alignItems: 'center',
    minHeight: 20,
    boxSizing: 'border-box',
    padding: '2px 7px',
    borderRadius: 999,
    color: token.color,
    background: token.background,
    fontSize: 10,
    fontWeight: 800,
    letterSpacing: '0.04em',
    textTransform: 'uppercase',
    whiteSpace: 'nowrap',
  };
  return <span style={style}>{sentimentLabel(sentiment)}</span>;
}

function Metric({ label, value }: { label: string; value: string }) {
  return <div style={{ ...cardStyle, display: 'flex', flexDirection: 'column', gap: 2, padding: '8px 9px' }}>
    <span style={{ color: 'var(--text-muted)', fontSize: 10, fontWeight: 700, letterSpacing: '0.04em',
      textTransform: 'uppercase' }}>{label}</span>
    <strong style={{ color: 'var(--text)', fontSize: 18, lineHeight: 1.1, fontVariantNumeric: 'tabular-nums' }}>{value}</strong>
  </div>;
}

function ThoughtReasonRow({ reason, maxCount }: { reason: GuestVibeReasonAggregate; maxCount: number }) {
  const width = maxCount > 0 ? Math.min(100, (count(reason.count) / maxCount) * 100) : 0;
  return <li style={{ ...cardStyle, display: 'flex', flexDirection: 'column', gap: 6, padding: 8 }}>
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
      <div style={{ display: 'flex', alignItems: 'baseline', gap: 6, minWidth: 0 }}>
        <strong style={{ overflow: 'hidden', color: 'var(--text)', fontSize: 12, textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>
          {reason.label || reasonLabel(reason.reasonCode)}
        </strong>
        <span style={{ color: 'var(--text-faint)', fontSize: 10, whiteSpace: 'nowrap' }}>{reason.reasonCode}</span>
      </div>
      <span style={{ color: 'var(--text)', fontSize: 12, fontWeight: 800, fontVariantNumeric: 'tabular-nums', whiteSpace: 'nowrap' }}>
        {formatCount(reason.count)}
      </span>
    </div>
    <div style={{ display: 'flex', alignItems: 'center', gap: 8 }}>
      <div aria-hidden="true" style={{ flex: 1, height: 5, overflow: 'hidden', borderRadius: 999, background: 'var(--seg-bg)' }}>
        <div style={{ width: `${width}%`, height: '100%', borderRadius: 'inherit', background: SENTIMENT_TOKENS[reason.sentiment].color }} />
      </div>
      <SentimentPill sentiment={reason.sentiment} />
    </div>
  </li>;
}

function TopThoughtCard({ thought }: { thought: GuestVibeTopThought }) {
  return <li style={{ ...cardStyle, display: 'flex', flexDirection: 'column', gap: 6, padding: 8 }}>
    <p style={{ margin: 0, color: 'var(--text)', fontSize: 12, lineHeight: 1.4 }}>
      <span aria-hidden="true" style={{ color: 'var(--accent)', fontSize: 17, fontWeight: 800, lineHeight: 0 }}>“</span>{thought.text}
    </p>
    <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
      <span style={{ color: 'var(--text-muted)', fontSize: 10 }}>{reasonLabel(thought.reasonCode)}
        {thought.count === undefined ? '' : ` · ${formatCount(thought.count)} mentions`}</span>
      <SentimentPill sentiment={thought.sentiment} />
    </div>
  </li>;
}

function GuestButton({ guest, selected, onSelect }: {
  guest: GuestVibeGuest;
  selected: boolean;
  onSelect: (id: string) => void;
}) {
  const buttonStyle: CSSProperties = {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'stretch',
    gap: 4,
    width: '100%',
    boxSizing: 'border-box',
    padding: '7px 8px',
    border: `1px solid ${selected ? 'var(--accent)' : 'var(--border)'}`,
    borderRadius: 7,
    background: selected ? 'color-mix(in srgb, var(--accent) 10%, var(--surface-solid))' : 'var(--surface-solid)',
    color: 'var(--text)',
    font: 'inherit',
    textAlign: 'left',
    cursor: 'pointer',
  };
  const guestLabel = guest.label || guest.id;
  const satisfaction = guest.satisfaction === undefined ? null : Math.round(Math.min(1, Math.max(0, guest.satisfaction)) * 100);
  return <li>
    <button type="button" style={buttonStyle} aria-pressed={selected}
      aria-label={`${guestLabel}, ${guest.status}${satisfaction === null ? '' : `, ${satisfaction}% satisfied`}`}
      onClick={() => onSelect(guest.id)}>
      <span style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 6 }}>
        <strong style={{ overflow: 'hidden', fontSize: 12, textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{guestLabel}</strong>
        <span style={{ color: 'var(--text-muted)', fontSize: 10, textTransform: 'capitalize', whiteSpace: 'nowrap' }}>{guest.status}</span>
      </span>
      {guest.latestThought && <span style={{ overflow: 'hidden', color: 'var(--text-muted)', fontSize: 11, lineHeight: 1.25,
        textOverflow: 'ellipsis', whiteSpace: 'nowrap' }}>{guest.latestThought}</span>}
    </button>
  </li>;
}

/**
 * Compact RCT2-style guest mood readout for an existing dock/panel surface.
 * It deliberately owns no simulation state and never derives aggregates from
 * raw events; the caller supplies a snapshot-consistent presentation model.
 */
export function GuestVibeCheck({ summary, reasonAggregates, topThoughts, guests, selectedGuestId,
  onSelectGuest, onClearSelectedGuest, maxGuests, title = 'Guest vibe check',
  description = 'A quick read on what visitors are thinking right now.' }: GuestVibeCheckProps) {
  const headingId = 'guest-vibe-check-heading';
  const guestLimit = clampGuestLimit(maxGuests);
  const visibleGuests = guests.slice(0, guestLimit);
  const hiddenGuestCount = Math.max(0, guests.length - visibleGuests.length);
  const visibleReasons = reasonAggregates.slice(0, MAX_REASON_ROWS);
  const visibleTopThoughts = topThoughts.slice(0, MAX_TOP_THOUGHTS);
  const maxReasonCount = visibleReasons.reduce((maximum, reason) => Math.max(maximum, count(reason.count)), 0);
  const selectedGuest = selectedGuestId ? guests.find((guest) => guest.id === selectedGuestId) : undefined;
  const sentimentCounts: readonly [GuestVibeSentiment, number][] = [
    ['positive', summary.positiveThoughtCount],
    ['neutral', summary.neutralThoughtCount],
    ['negative', summary.negativeThoughtCount],
  ];

  return <section className="lift-overview guest-vibe-check" aria-labelledby={headingId} style={columnStyle}>
    <div className="dock-head" style={{ alignItems: 'flex-start', gap: 10 }}>
      <div style={{ minWidth: 0 }}>
        <h2 id={headingId} style={{ margin: 0, color: 'var(--text)', fontSize: 14, lineHeight: 1.2 }}>{title}</h2>
        <p style={{ ...mutedStyle, marginTop: 3 }}>{description}</p>
      </div>
      <span role="status" aria-live="polite" style={{ color: 'var(--accent)', fontSize: 11, fontWeight: 800, whiteSpace: 'nowrap' }}>
        {formatCount(summary.guestCount)} guests
      </span>
    </div>

    <div aria-label="Guest vibe summary" style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 6 }}>
      <Metric label="Guests" value={formatCount(summary.guestCount)} />
      <Metric label="Active" value={formatCount(summary.activeGuestCount)} />
      <Metric label="Positive" value={formatCount(summary.positiveThoughtCount)} />
    </div>

    <div aria-label="Guest safety summary" style={{ display: 'grid', gridTemplateColumns: 'repeat(3, minmax(0, 1fr))', gap: 6 }}>
      <Metric label="Incidents" value={formatCount(summary.activeIncidentCount ?? 0)} />
      <Metric label="Patrol queue" value={formatCount(summary.patrolQueueCount ?? 0)} />
      <Metric label="Safe runs" value={`${Math.round(Math.min(1, Math.max(0, summary.safetyRate ?? 1)) * 100)}%`} />
    </div>

    <div style={columnStyle}>
      <h3 style={sectionTitleStyle}>Sentiment</h3>
      <div style={{ display: 'flex', flexWrap: 'wrap', gap: 5 }} aria-label="Thought sentiment counts">
        {sentimentCounts.map(([sentiment, sentimentCount]) => <span key={sentiment}
          style={{ display: 'inline-flex', alignItems: 'center', gap: 5, padding: '4px 7px', borderRadius: 6,
            background: SENTIMENT_TOKENS[sentiment].background, color: SENTIMENT_TOKENS[sentiment].color, fontSize: 11, fontWeight: 700 }}>
          <span aria-hidden="true" style={{ width: 6, height: 6, borderRadius: '50%', background: 'currentColor' }} />
          {sentimentLabel(sentiment)} <strong style={{ fontVariantNumeric: 'tabular-nums' }}>{formatCount(sentimentCount)}</strong>
        </span>)}
      </div>
    </div>

    <div style={columnStyle}>
      <h3 style={sectionTitleStyle}>Why guests feel that way</h3>
      {visibleReasons.length === 0 ? <p style={mutedStyle}>No coded thoughts have arrived yet.</p> :
        <ol aria-label="Guest thought reasons" style={{ ...columnStyle, margin: 0, padding: 0, listStyle: 'none' }}>
          {visibleReasons.map((reason, index) => <ThoughtReasonRow key={`${reason.reasonCode}-${index}`} reason={reason} maxCount={maxReasonCount} />)}
        </ol>}
    </div>

    <div style={columnStyle}>
      <h3 style={sectionTitleStyle}>What visitors are saying</h3>
      {visibleTopThoughts.length === 0 ? <p style={mutedStyle}>No representative thoughts yet.</p> :
        <ol aria-label="Top guest thoughts" style={{ ...columnStyle, margin: 0, padding: 0, listStyle: 'none' }}>
          {visibleTopThoughts.map((thought, index) => <TopThoughtCard key={`${thought.reasonCode}-${index}`} thought={thought} />)}
        </ol>}
    </div>

    <div style={columnStyle}>
      <div style={{ display: 'flex', alignItems: 'baseline', justifyContent: 'space-between', gap: 8 }}>
        <h3 style={sectionTitleStyle}>Inspect a guest</h3>
        <span style={{ color: 'var(--text-faint)', fontSize: 10 }}>Select to focus</span>
      </div>
      {visibleGuests.length === 0 ? <p style={mutedStyle}>No guests are available in this snapshot.</p> :
        <ul aria-label="Guests available for inspection" style={{ ...columnStyle, margin: 0, padding: 0, listStyle: 'none' }}>
          {visibleGuests.map((guest) => <GuestButton key={guest.id} guest={guest}
            selected={guest.id === selectedGuestId} onSelect={onSelectGuest} />)}
        </ul>}
      {hiddenGuestCount > 0 && <p style={mutedStyle} aria-live="polite">Showing {formatCount(visibleGuests.length)} of {formatCount(guests.length)} guests.</p>}
      {selectedGuest && <div style={{ ...cardStyle, display: 'flex', flexDirection: 'column', gap: 6, padding: 8 }} aria-label={`Selected guest ${selectedGuest.label || selectedGuest.id}`}>
        <div style={{ display: 'flex', alignItems: 'center', justifyContent: 'space-between', gap: 8 }}>
          <strong style={{ color: 'var(--text)', fontSize: 12 }}>{selectedGuest.label || selectedGuest.id}</strong>
          {selectedGuest.sentiment && <SentimentPill sentiment={selectedGuest.sentiment} />}
        </div>
        <div style={{ display: 'flex', justifyContent: 'space-between', gap: 8, color: 'var(--text-muted)', fontSize: 11 }}>
          <span>Status</span><strong style={{ color: 'var(--text)', textTransform: 'capitalize' }}>{selectedGuest.status}</strong>
        </div>
        {selectedGuest.latestThought && <p style={mutedStyle}>{selectedGuest.latestThought}</p>}
        {onClearSelectedGuest && <button type="button" className="site-btn" onClick={onClearSelectedGuest}>Clear guest selection</button>}
      </div>}
    </div>
  </section>;
}
