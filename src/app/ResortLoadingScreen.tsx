import { useEffect, useRef, useState } from 'react';
import { bootBand, bootCaption, bootPercent } from './resortBoot';
import type { BootProgress } from './resortBoot';

/** How long a load may run before we offer the player a way past it. */
const STALL_MS = 20000;

export type ResortLoadingState = 'loading' | 'failed';

export interface ResortLoadingScreenProps {
  /** Resort name — known from the save summary before the save itself is read. */
  title: string;
  progress: BootProgress;
  /** The resort's own NAIP orthophoto, once the package has been decoded. */
  imageryUrl: string | null;
  /** Resume previews match the final camera and should not be heavily blurred. */
  imageryKind?: 'resume' | 'aerial';
  state: ResortLoadingState;
  message?: string;
  /** True once the resort is fully drawn; fades the screen out over the scene. */
  done?: boolean;
  onBack: () => void;
  onEnterAnyway?: () => void;
  onRepair?: () => void;
}

/**
 * The single loading surface for resuming a saved resort, from the click in the
 * menu to a fully-drawn resort. Speaks the main menu's language (scrim, logo
 * mark, letterspaced caps) over a blurred aerial of the player's own mountain,
 * and reuses the alpine `.package-progress` bar so it matches the New Game
 * preparation gate.
 *
 * The bar is driven imperatively from a rAF loop rather than from React state:
 * during a load the main thread is saturated rasterizing tiles, and a re-render
 * per frame would compete with the very work the bar is reporting on.
 */
export function ResortLoadingScreen({
  title,
  progress,
  imageryUrl,
  imageryKind = 'aerial',
  state,
  message,
  done = false,
  onBack,
  onEnterAnyway,
  onRepair,
}: ResortLoadingScreenProps) {
  const barRef = useRef<HTMLSpanElement>(null);
  const pctRef = useRef<HTMLSpanElement>(null);
  const progressRef = useRef(progress);
  const stateRef = useRef(state);
  const displayedRef = useRef(0);
  const [photoReady, setPhotoReady] = useState(false);
  const [stalled, setStalled] = useState(false);

  progressRef.current = progress;
  stateRef.current = state;

  // Offer an escape hatch rather than ever auto-entering a half-drawn resort.
  useEffect(() => {
    if (state !== 'loading') return;
    const t = window.setTimeout(() => setStalled(true), STALL_MS);
    return () => window.clearTimeout(t);
  }, [state]);

  // Ease the bar toward the current stage's target, monotonically. A stage that
  // can report real counts (the tile preload) drives an exact target; an opaque
  // stage creeps asymptotically toward just short of its band end, so the bar
  // always moves without inventing progress it does not have.
  useEffect(() => {
    let raf = 0;
    const tick = () => {
      // A failed load leaves the bar frozen where it stopped — creeping on
      // would suggest work is still happening.
      if (stateRef.current === 'failed') {
        raf = requestAnimationFrame(tick);
        return;
      }
      const p = progressRef.current;
      const [, end] = bootBand(p.stage);
      const exact = bootPercent(p);
      const target = exact ?? end - 1;
      const next = displayedRef.current + (target - displayedRef.current) * 0.06;
      displayedRef.current = Math.max(displayedRef.current, Math.min(next, 100));
      const shown = Math.round(displayedRef.current);
      if (barRef.current) barRef.current.style.width = `${displayedRef.current.toFixed(1)}%`;
      if (pctRef.current) pctRef.current.textContent = `${shown}%`;
      raf = requestAnimationFrame(tick);
    };
    raf = requestAnimationFrame(tick);
    return () => cancelAnimationFrame(raf);
  }, []);

  // Run the bar to a full 100% as the screen fades, so it never dies mid-track.
  useEffect(() => {
    if (!done) return;
    displayedRef.current = 100;
    if (barRef.current) barRef.current.style.width = '100%';
    if (pctRef.current) pctRef.current.textContent = '100%';
  }, [done]);

  const failed = state === 'failed';
  const showStall = stalled && !failed && !done;

  const caption = failed
    ? message ?? 'This resort could not be loaded.'
    : showStall
    ? 'Taking longer than usual on this resort.'
    : progress.stage === 'warm' && progress.total
    ? `${bootCaption(progress)} · ${progress.completed ?? 0} / ${progress.total} tiles`
    : bootCaption(progress);

  return (
    <div className={`resort-loading${done ? ' resort-loading-done' : ''}`} role="status" aria-live="polite">
      <svg className="resort-loading-topo" viewBox="0 0 120 120" preserveAspectRatio="xMidYMid slice" aria-hidden>
        <defs>
          <path
            id="bootTopoRing"
            d="M60 42 C73 42 80 50 80 60 C80 72 71 80 60 80 C49 80 40 71 40 60 C40 49 47 42 60 42 Z"
          />
        </defs>
        <g fill="none" stroke="currentColor" strokeWidth="0.7">
          {[0.5, 0.95, 1.45, 2.0, 2.6, 3.25, 3.95].map((scale, i) => (
            <use
              key={scale}
              href="#bootTopoRing"
              transform={`translate(60 60) scale(${scale}) translate(-60 -60)`}
              style={{ opacity: 0.9 - i * 0.09 }}
            />
          ))}
        </g>
      </svg>

      {imageryUrl && (
        <img
          className={`resort-loading-photo is-${imageryKind}${photoReady ? ' is-shown' : ''}`}
          src={imageryUrl}
          alt=""
          aria-hidden
          onLoad={() => setPhotoReady(true)}
        />
      )}

      <div className="resort-loading-scrim" aria-hidden />

      <div className="resort-loading-content">
        <svg className="menu-logo-mark" viewBox="0 0 100 80" aria-hidden>
          <path d="M10,70 L50,20 L90,70 Z" fill="none" strokeWidth="3" strokeLinejoin="round" />
          <path d="M35,70 L60,40 L85,70 Z" fill="none" strokeWidth="2" strokeLinejoin="round" />
          <line x1="5" y1="70" x2="95" y2="70" strokeWidth="3" />
        </svg>

        <h2 className="resort-loading-title">{title}</h2>

        <div className="resort-loading-bar-wrap">
          <div className={`package-progress${failed ? ' is-failed' : ''}`}>
            <span ref={barRef} style={{ width: '0%' }} />
          </div>
          <div className="resort-loading-status">
            <span className="resort-loading-caption">{caption}</span>
            <span className="resort-loading-pct" ref={pctRef}>
              0%
            </span>
          </div>
        </div>

        <div className="resort-loading-actions">
          <button className="resort-loading-btn" onClick={onBack}>
            ← Back to menu
          </button>
          {failed && onRepair && (
            <button className="resort-loading-btn resort-loading-btn-primary" onClick={onRepair}>
              Prepare Resort Data
            </button>
          )}
          {showStall && onEnterAnyway && (
            <button className="resort-loading-btn resort-loading-btn-primary" onClick={onEnterAnyway}>
              Enter anyway
            </button>
          )}
        </div>
      </div>
    </div>
  );
}
