import { useId, useRef } from 'react';
export function GameTabs<T extends string>({ label, value, options, onChange, panelId }: {
  label: string; value: T; options: readonly { value: T; label: string; disabled?: boolean }[];
  onChange(value: T): void; panelId: string;
}) {
  const id = useId(), refs = useRef(new Map<T, HTMLButtonElement>());
  return <div className="game-tabs" role="tablist" aria-label={label}>
    {options.map((option) => <button key={option.value} id={`${id}-${option.value}`}
      ref={(element) => { if (element) refs.current.set(option.value, element); else refs.current.delete(option.value); }}
      role="tab" aria-selected={value === option.value} aria-controls={panelId}
      disabled={option.disabled} tabIndex={value === option.value ? 0 : -1}
      onClick={() => onChange(option.value)} onKeyDown={(event) => {
        if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
        event.preventDefault(); event.stopPropagation();
        const available = options.filter((item) => !item.disabled), index = available.findIndex((item) => item.value === option.value);
        const next = event.key === 'Home' ? 0 : event.key === 'End' ? available.length - 1
          : (index + (event.key === 'ArrowRight' ? 1 : -1) + available.length) % available.length;
        onChange(available[next].value); refs.current.get(available[next].value)?.focus();
      }}>{option.label}</button>)}
  </div>;
}
