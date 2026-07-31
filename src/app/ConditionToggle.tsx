/**
 * Open / Closed operating condition for a run or a lift. Shared by the trail
 * and lift detail panels and the node map inspector so the control reads the
 * same everywhere. The condition is persisted on SavedTrail / SavedLift; every
 * network segment derived from a closed run inherits it.
 */
export function ConditionToggle({
  closed,
  label = 'Condition',
  onChange,
}: {
  closed: boolean;
  label?: string;
  onChange: (closed: boolean) => void;
}) {
  return (
    <div className="condition-row">
      <span className="condition-label">{label}</span>
      <div className="segmented network-condition">
        <button
          className={`seg-btn${closed ? '' : ' seg-btn-active'}`}
          aria-pressed={!closed}
          onClick={() => onChange(false)}
        >
          Open
        </button>
        <button
          className={`seg-btn${closed ? ' seg-btn-active' : ''}`}
          aria-pressed={closed}
          onClick={() => onChange(true)}
        >
          Closed
        </button>
      </div>
    </div>
  );
}
