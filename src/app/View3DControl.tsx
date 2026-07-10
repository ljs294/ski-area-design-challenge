export function View3DControl({ is3D, onToggle }: { is3D: boolean; onToggle: () => void }) {
  return (
    <div className="view3d-control">
      <button
        className={`view3d-btn${is3D ? ' view3d-btn-active' : ''}`}
        aria-pressed={is3D}
        title={is3D ? 'Return to top-down 2D view' : 'Tilt into 3D terrain view'}
        onClick={onToggle}
      >
        {is3D ? '2D' : '3D'}
      </button>
    </div>
  );
}
