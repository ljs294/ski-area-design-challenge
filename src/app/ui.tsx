import { useEffect, useRef, type ReactNode } from 'react';

export type IconName = 'resort' | 'lifts' | 'trails' | 'snowmaking' | 'infrastructure' | 'guests' | 'layers' | 'menu' | 'close' | 'expand' | 'arrow' | 'pin' | 'save' | 'palette' | 'edit';
const paths: Record<IconName, string> = {
  resort: 'M2 20 10 5l5 9 3-5 4 11H2ZM7 11l3 3 3-3',
  lifts: 'M2 6 22 3M11 5v7H7v6h10v-6h-6M8 21v-3m8 3v-3',
  trails: 'M3 20 12 4l9 16H3Zm7-10 4 3-4 3 4 4',
  snowmaking: 'M12 2v20M3.3 7l17.4 10M3.3 17 20.7 7M9 4l3 3 3-3M9 20l3-3 3 3',
  infrastructure: 'M4 22 8 2m12 20L16 2M12 3v3m0 4v4m0 4v3',
  guests: 'M8 12a4 4 0 1 0 0-8 4 4 0 0 0 0 8Zm-6 9v-2a6 6 0 0 1 12 0v2m3-17a4 4 0 0 1 0 8m1 3a5 5 0 0 1 4 5v1',
  layers: 'm12 3 10 5-10 5L2 8l10-5ZM2 12l10 5 10-5M2 16l10 5 10-5',
  menu: 'M4 6h16M4 12h16M4 18h16',
  close: 'm6 6 12 12M6 18 18 6',
  expand: 'M8 3H3v5m13-5h5v5M3 16v5h5m13-5v5h-5',
  arrow: 'M4 12h16m-6-6 6 6-6 6',
  pin: 'M12 17v4M5 12h14l-4-4V3H9v5l-4 4Z',
  save: 'M4 3h13l3 3v15H4V3Zm3 0v6h8V3M8 17h8',
  palette: 'M12 3a9 9 0 1 0 0 18h1.3a1.7 1.7 0 0 0 0-3.4h-.8a1.5 1.5 0 0 1 0-3H15a3 3 0 0 0 0-6H12ZM7.5 11.5h.01M9 7.5h.01M14.5 7.5h.01M17 11.5h.01',
  edit: 'm4 20 4.2-1 10.4-10.4a2.1 2.1 0 0 0-3-3L5.2 16 4 20ZM13.8 7.2l3 3',
};
export function Icon({ name }: { name: IconName }) {
  return <svg className="ui-icon" viewBox="0 0 24 24" width="22" height="22" fill="none"
    stroke="currentColor" strokeWidth="1.6" strokeLinecap="round" strokeLinejoin="round" aria-hidden="true">
    <path d={paths[name]} />
  </svg>;
}

/** Trap focus without taking Escape away from the owning feature. */
export function useDialogFocus() {
  const ref = useRef<HTMLDivElement>(null);
  useEffect(() => {
    const root = ref.current;
    if (!root) return;
    const previous = document.activeElement;
    const focusable = () => Array.from(root.querySelectorAll<HTMLElement>(
      'button:not(:disabled), input:not(:disabled), select:not(:disabled), textarea:not(:disabled), a[href], [tabindex="0"]',
    )).filter((element) => element.getClientRects().length > 0);
    (focusable()[0] ?? root).focus();
    const onKey = (event: KeyboardEvent) => {
      if (event.key !== 'Tab') return;
      const items = focusable(), first = items[0], last = items.at(-1);
      if (!first) { event.preventDefault(); root.focus(); }
      else if (event.shiftKey && (document.activeElement === first || document.activeElement === root)) {
        event.preventDefault(); last?.focus();
      } else if (!event.shiftKey && document.activeElement === last) {
        event.preventDefault(); first.focus();
      }
    };
    const observer = new MutationObserver(() => {
      if (document.activeElement === document.body) (focusable()[0] ?? root).focus();
    });
    observer.observe(root, { childList: true, subtree: true });
    root.addEventListener('keydown', onKey);
    return () => {
      observer.disconnect();
      root.removeEventListener('keydown', onKey);
      if (previous instanceof HTMLElement && previous.isConnected) previous.focus();
    };
  }, []);
  return ref;
}
export function Dialog({ title, onClose, children, className = '' }: {
  title: string; onClose(): void; children: ReactNode; className?: string;
}) {
  const ref = useDialogFocus();
  useEffect(() => {
    const onKey = (event: KeyboardEvent) => { if (event.key === 'Escape') { event.stopPropagation(); onClose(); } };
    const root = ref.current;
    root?.addEventListener('keydown', onKey);
    return () => root?.removeEventListener('keydown', onKey);
  }, [onClose, ref]);
  return <div className="modal-overlay" onClick={onClose}>
    <div ref={ref} className={`ui-dialog ${className}`} role="dialog" aria-modal="true" aria-label={title}
      tabIndex={-1} onKeyDown={(event) => event.stopPropagation()} onClick={(event) => event.stopPropagation()}>
      <header className="ui-dialog-header"><h2>{title}</h2>
        <button className="ui-icon-button" aria-label={`Close ${title}`} onClick={onClose}><Icon name="close" /></button>
      </header>{children}
    </div>
  </div>;
}
