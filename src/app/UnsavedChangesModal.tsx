import { useEffect } from 'react';
import { useDialogFocus } from './ui';

export type UnsavedChoice = 'save' | 'discard' | 'cancel';

/**
 * Gate on leaving a resort with work that has not reached the disk. Terrain
 * grading and felled ground cover are named explicitly: they are the most
 * expensive thing in a session and, until this gate existed, the only thing
 * that survived an unsaved exit.
 *
 * Escape and a backdrop click both mean Cancel — the non-destructive choice.
 */
export function UnsavedChangesModal({
  saving,
  onChoice,
}: {
  saving: boolean;
  onChoice: (choice: UnsavedChoice) => void;
}) {
  const focusRef = useDialogFocus();
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') { e.stopPropagation(); if (!saving) onChoice('cancel'); }
    };
    const root = focusRef.current;
    root?.addEventListener('keydown', onKey);
    return () => root?.removeEventListener('keydown', onKey);
  }, [onChoice, saving, focusRef]);

  return (
    <div
      className="modal-overlay"
      onClick={() => { if (!saving) onChoice('cancel'); }}
    >
      <div
        ref={focusRef} tabIndex={-1} onKeyDown={(event) => event.stopPropagation()} className="settings-panel unsaved-panel"
        role="dialog"
        aria-modal="true"
        aria-labelledby="unsaved-title"
        onClick={(e) => e.stopPropagation()}
      >
        <div className="settings-header">
          <h2 className="settings-title" id="unsaved-title">Unsaved changes</h2>
        </div>
        <p className="unsaved-body">
          This resort has changes that have not been saved — including any terrain
          you have graded and any ground cover you have cleared. Leaving without
          saving discards them.
        </p>
        <div className="unsaved-actions">
          <button
            type="button"
            className="site-btn site-btn-primary"
            onClick={() => onChoice('save')}
            disabled={saving}
          >
            {saving ? 'Saving…' : 'Save and leave'}
          </button>
          <button
            type="button"
            className="site-btn site-btn-danger-ghost"
            onClick={() => onChoice('discard')}
            disabled={saving}
          >
            Discard changes
          </button>
          <button
            type="button"
            className="site-btn"
            onClick={() => onChoice('cancel')}
            disabled={saving}
          >
            Keep editing
          </button>
        </div>
      </div>
    </div>
  );
}
