import type { ConstructionActivity } from './constructionLock';

const LABELS: Record<ConstructionActivity, string> = {
  lift: 'Building lift…',
  trail: 'Building run…',
  road: 'Building road…',
  pond: 'Building pond…',
  dam: 'Building dam…',
};

export function ConstructionStatusBug({ activity }: { activity: ConstructionActivity }) {
  return (
    <div className="build-status-bug" role="status" aria-live="polite">
      <span className="site-btn-spinner" aria-hidden="true" />
      {LABELS[activity]}
    </div>
  );
}
