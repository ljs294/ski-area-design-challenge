import { createContext, useContext, useLayoutEffect, useRef, useState, type ReactNode } from 'react';
import { useSettings } from './SettingsContext';
import { Icon } from './ui';
import { clampWindow, placeWindow, placeWindowBelowAnchor, type WindowRect } from './windowPlacement';

interface Arrangement {
  positions: Map<string, WindowRect>;
  elements: Map<string, HTMLElement>;
  front: number;
}
const WindowContext = createContext<Arrangement | null>(null);
export function GameWindows({ children }: { children: ReactNode }) {
  const [arrangement] = useState<Arrangement>(() => ({ positions: new Map(), elements: new Map(), front: 1 }));
  return <WindowContext.Provider value={arrangement}>{children}</WindowContext.Provider>;
}
export function GameWindow({ id, title, children, tabs, pinned = false, onPin, onClose,
  placement = 'auto', anchorRef, variant = 'default' }: {
  id: string; title: string; children: ReactNode; tabs?: ReactNode;
  pinned?: boolean; onPin?(): void; onClose(): void;
  placement?: 'auto' | 'below-anchor'; anchorRef?: { readonly current: HTMLElement | null };
  variant?: 'default' | 'menu';
}) {
  const arrangement = useContext(WindowContext)!;
  const { settings } = useSettings();
  const scale = Number(settings.interfaceScale) / 100;
  const ref = useRef<HTMLElement>(null);
  const [position, setPosition] = useState({ x: 8, y: 8 });
  const [front, setFront] = useState(1);
  const [placed, setPlaced] = useState(false);
  const drag = useRef<{ x: number; y: number; startX: number; startY: number } | null>(null);
  useLayoutEffect(() => {
    const element = ref.current;
    if (!element) return;
    const viewport = () => ({ width: window.innerWidth / scale, height: window.innerHeight / scale - 40 });
    const size = () => ({ width: element.offsetWidth, height: element.offsetHeight });
    const occupied = [...arrangement.elements.entries()].filter(([key]) => key !== id).map(([, node]) => {
      const box = node.getBoundingClientRect();
      return { x: box.x / scale, y: box.y / scale, width: box.width / scale, height: box.height / scale };
    });
    const prior = arrangement.positions.get(id);
    const anchor = placement === 'below-anchor' ? anchorRef?.current?.getBoundingClientRect() : null;
    const initial = anchor
      ? placeWindowBelowAnchor(size(), { x: anchor.left / scale, y: anchor.top / scale,
        width: anchor.width / scale, height: anchor.height / scale }, viewport())
      : prior ? clampWindow({ ...prior, ...size() }, viewport()) : placeWindow(size(), occupied, viewport());
    arrangement.positions.set(id, initial); arrangement.elements.set(id, element);
    setPosition(initial); setFront(++arrangement.front); setPlaced(true);
    const contain = () => {
      const current = arrangement.positions.get(id)!;
      const next = clampWindow({ ...current, ...size() }, viewport());
      arrangement.positions.set(id, next);
      setPosition((old) => old.x === next.x && old.y === next.y ? old : next);
    };
    const observer = new ResizeObserver(contain); observer.observe(element);
    window.addEventListener('resize', contain);
    return () => { observer.disconnect(); window.removeEventListener('resize', contain); arrangement.elements.delete(id); };
  }, [arrangement, id, scale, placement, anchorRef]);
  const move = (x: number, y: number) => {
    const element = ref.current!;
    const next = clampWindow({ x, y, width: element.offsetWidth, height: element.offsetHeight },
      { width: window.innerWidth / scale, height: window.innerHeight / scale - 40 });
    arrangement.positions.set(id, next); setPosition(next);
  };
  return <section ref={ref} className={`game-window${variant === 'menu' ? ' game-window--menu menu-container' : ''}`} aria-label={title}
    style={{ left: position.x, top: position.y, zIndex: front, visibility: placed ? 'visible' : 'hidden' }}
    onPointerDownCapture={() => setFront(++arrangement.front)}>
    <header className="game-window-header" tabIndex={0} aria-label={`Move ${title}`}
      title="Drag to move this window, or focus the header and use arrow keys"
      onKeyDown={(event) => {
        if (event.target !== event.currentTarget || !['ArrowLeft', 'ArrowRight', 'ArrowUp', 'ArrowDown'].includes(event.key)) return;
        event.preventDefault(); event.stopPropagation();
        move(position.x + (event.key === 'ArrowRight' ? 8 : event.key === 'ArrowLeft' ? -8 : 0),
          position.y + (event.key === 'ArrowDown' ? 8 : event.key === 'ArrowUp' ? -8 : 0));
      }}
      onPointerDown={(event) => {
        if (event.button !== 0 || (event.target as HTMLElement).closest('button')) return;
        drag.current = { x: event.clientX, y: event.clientY, startX: position.x, startY: position.y };
        event.currentTarget.setPointerCapture(event.pointerId); event.preventDefault();
      }} onPointerMove={(event) => {
        if (drag.current) move(drag.current.startX + (event.clientX - drag.current.x) / scale,
          drag.current.startY + (event.clientY - drag.current.y) / scale);
      }} onPointerUp={() => { drag.current = null; }} onPointerCancel={() => { drag.current = null; }}>
      <h2>{title}</h2>
      {onPin && <button className="ui-icon-button game-window-pin" aria-label={`${pinned ? 'Unpin' : 'Pin'} ${title}`}
        aria-pressed={pinned} onClick={onPin}><Icon name="pin" /></button>}
      <button className="ui-icon-button" aria-label={`Close ${title}`} onClick={onClose}><Icon name="close" /></button>
    </header>
    {tabs}
    <div className="workspace-body game-window-body">{children}</div>
  </section>;
}
