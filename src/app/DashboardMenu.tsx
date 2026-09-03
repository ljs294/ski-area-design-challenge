import { useEffect, useRef, useState } from 'react';
import type { DashboardKind } from './dashboardMode';

export function DashboardMenu({ active, onChange }: {
  active: DashboardKind | null;
  onChange(kind: DashboardKind | null): void;
}) {
  const [open, setOpen] = useState(false);
  const rootRef = useRef<HTMLDivElement | null>(null);

  useEffect(() => {
    if (!open) return;
    const pointer = (event: PointerEvent) => {
      if (!rootRef.current?.contains(event.target as Node)) setOpen(false);
    };
    const key = (event: KeyboardEvent) => {
      if (event.key === 'Escape') { event.stopImmediatePropagation(); setOpen(false); }
    };
    window.addEventListener('pointerdown', pointer);
    window.addEventListener('keydown', key, true);
    return () => {
      window.removeEventListener('pointerdown', pointer);
      window.removeEventListener('keydown', key, true);
    };
  }, [open]);

  const choose = (kind: DashboardKind) => {
    onChange(active === kind ? null : kind);
    setOpen(false);
  };

  return <div className="dashboard-menu" ref={rootRef}>
    <button className={`dashboard-bubble${active ? ' is-active' : ''}`}
      type="button" aria-expanded={open} aria-haspopup="menu"
      onClick={() => setOpen((value) => !value)}>
      <svg className="dashboard-bubble-icon" viewBox="0 0 20 20" aria-hidden="true">
        <rect x="2.5" y="3" width="15" height="14" rx="2" />
        <path d="M6 7.5h3v5H6zM11 7.5h3v2h-3zM11 11h3v1.5h-3z" />
      </svg>
      <span>Dashboards</span>
    </button>
    {open && <div className="dashboard-popover" role="menu" aria-label="Dashboards">
      <button type="button" role="menuitemcheckbox" aria-checked={active === 'trails'}
        onClick={() => choose('trails')}>
        <span className="dashboard-menu-check" aria-hidden="true">
          {active === 'trails' ? '✓' : ''}
        </span><span>Trail Map</span>
      </button>
      <button type="button" role="menuitemcheckbox" aria-checked={active === 'snowmaking'}
        onClick={() => choose('snowmaking')}>
        <span className="dashboard-menu-check" aria-hidden="true">
          {active === 'snowmaking' ? '✓' : ''}
        </span><span>Snowmaking</span>
      </button>
      <button type="button" role="menuitemcheckbox" aria-checked={active === 'guests'}
        onClick={() => choose('guests')}>
        <span className="dashboard-menu-check" aria-hidden="true">
          {active === 'guests' ? '✓' : ''}
        </span><span>Guests</span>
      </button>
    </div>}
  </div>;
}
