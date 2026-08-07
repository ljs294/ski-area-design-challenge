import type { ToolId } from './toolCoordinator';

export interface InteractionToggle {
  isEnabled(): boolean;
  enable(): void;
  disable(): void;
}

export interface MapInteractionTarget {
  getCanvas(): { style: { cursor: string } };
  dragPan: InteractionToggle;
  doubleClickZoom: InteractionToggle;
}

export interface MapInteractionOverrides {
  cursor?: string;
  dragPanEnabled?: boolean;
  doubleClickZoomEnabled?: boolean;
}

export interface MapInteractionLeaseHandle {
  release(): void;
}

interface ActiveLease {
  owner: ToolId;
  release(): void;
}

function setEnabled(toggle: InteractionToggle, enabled: boolean): void {
  if (toggle.isEnabled() === enabled) return;
  if (enabled) toggle.enable();
  else toggle.disable();
}

/** Exclusive, exactly-once restoration for controller-owned map interactions. */
export class MapInteractionLease {
  private active: ActiveLease | null = null;
  private readonly isActiveTool: (toolId: ToolId) => boolean;

  constructor(isActiveTool: (toolId: ToolId) => boolean) {
    this.isActiveTool = isActiveTool;
  }

  acquire(
    owner: ToolId,
    target: MapInteractionTarget,
    overrides: MapInteractionOverrides,
  ): MapInteractionLeaseHandle {
    if (!this.isActiveTool(owner)) {
      throw new Error(`Inactive tool ${owner} cannot acquire map interactions.`);
    }

    this.active?.release();
    const canvas = target.getCanvas();
    const priorCursor = canvas.style.cursor;
    const priorDragPan = target.dragPan.isEnabled();
    const priorDoubleClickZoom = target.doubleClickZoom.isEnabled();
    let released = false;

    if (overrides.cursor !== undefined) canvas.style.cursor = overrides.cursor;
    if (overrides.dragPanEnabled !== undefined) setEnabled(target.dragPan, overrides.dragPanEnabled);
    if (overrides.doubleClickZoomEnabled !== undefined) {
      setEnabled(target.doubleClickZoom, overrides.doubleClickZoomEnabled);
    }

    const lease: ActiveLease = {
      owner,
      release: () => {
        if (released) return;
        released = true;
        if (overrides.cursor !== undefined) canvas.style.cursor = priorCursor;
        if (overrides.dragPanEnabled !== undefined) setEnabled(target.dragPan, priorDragPan);
        if (overrides.doubleClickZoomEnabled !== undefined) {
          setEnabled(target.doubleClickZoom, priorDoubleClickZoom);
        }
        if (this.active === lease) this.active = null;
      },
    };
    this.active = lease;
    return { release: lease.release };
  }

  release(owner: ToolId): boolean {
    if (this.active?.owner !== owner) return false;
    this.active.release();
    return true;
  }

  dispose(): void {
    this.active?.release();
  }
}
