export const TOOL_IDS = [
  'lift',
  'road',
  'dam',
  'pond',
  'ski-node',
  'ski-path',
  'trail',
  'snowmaking-pipe',
  'snowmaking-node',
] as const;

export type ToolId = (typeof TOOL_IDS)[number];
export type DockId = 'layers' | 'lifts' | 'trails' | 'snowmaking' | 'infrastructure';

export interface ToolCoordinatorSnapshot {
  activeTool: ToolId | null;
  openDock: DockId | null;
  layersAlongsideBuild: boolean;
}

export type DockToggleResult = 'layers-alongside' | 'opened' | 'closed';

const TOOL_DOCK: Record<ToolId, Exclude<DockId, 'layers'>> = {
  lift: 'lifts',
  road: 'infrastructure',
  dam: 'snowmaking',
  pond: 'snowmaking',
  'ski-node': 'trails',
  'ski-path': 'trails',
  trail: 'trails',
  'snowmaking-pipe': 'snowmaking',
  'snowmaking-node': 'snowmaking',
};

const LAYERS_ALONGSIDE_TOOLS = new Set<ToolId>([
  'road', 'dam', 'pond', 'trail', 'snowmaking-pipe', 'snowmaking-node',
]);

const INITIAL_SNAPSHOT: ToolCoordinatorSnapshot = Object.freeze({
  activeTool: null,
  openDock: null,
  layersAlongsideBuild: false,
});

/**
 * Synchronous arbitration for mutually-exclusive construction tools. React
 * state is only a projection of this object; event handlers can always read
 * the authoritative active tool without waiting for a render.
 */
export class ToolCoordinator {
  private current: ToolCoordinatorSnapshot = INITIAL_SNAPSHOT;
  private readonly cancellations = new Map<ToolId, () => void>();
  private readonly onChange: (snapshot: ToolCoordinatorSnapshot) => void;
  private cancelling: ToolId | null = null;
  private transitioning = false;

  constructor(onChange: (snapshot: ToolCoordinatorSnapshot) => void = () => {}) {
    this.onChange = onChange;
  }

  get snapshot(): ToolCoordinatorSnapshot {
    return this.current;
  }

  isActive(toolId: ToolId): boolean {
    return this.current.activeTool === toolId;
  }

  register(toolId: ToolId, cancel: () => void): () => void {
    this.cancellations.set(toolId, cancel);
    return () => {
      if (this.cancellations.get(toolId) === cancel) this.cancellations.delete(toolId);
    };
  }

  activate(toolId: ToolId): boolean {
    if (this.transitioning) return false;
    if (this.current.activeTool === toolId) return true;

    this.transitioning = true;
    let cancellationError: unknown;
    try {
      const previous = this.current.activeTool;
      if (previous) {
        this.cancelling = previous;
        try {
          this.cancellations.get(previous)?.();
        } catch (error) {
          cancellationError = error;
        } finally {
          this.cancelling = null;
        }
      }
      const layersSupported = LAYERS_ALONGSIDE_TOOLS.has(toolId);
      this.publish({
        ...this.current,
        activeTool: toolId,
        openDock: this.current.layersAlongsideBuild && !layersSupported ? 'layers' : this.current.openDock,
        layersAlongsideBuild: layersSupported ? this.current.layersAlongsideBuild : false,
      });
    } finally {
      this.transitioning = false;
    }

    if (cancellationError) throw cancellationError;
    return true;
  }

  /** Completion/release is accepted only from the authoritative active tool. */
  release(toolId: ToolId): boolean {
    if (this.cancelling === toolId || this.current.activeTool !== toolId) return false;
    this.publish(this.withoutActiveBuild());
    return true;
  }

  cancelActive(): boolean {
    if (this.transitioning) return false;
    const active = this.current.activeTool;
    if (!active) return false;

    this.transitioning = true;
    let cancellationError: unknown;
    this.cancelling = active;
    try {
      this.cancellations.get(active)?.();
    } catch (error) {
      cancellationError = error;
    } finally {
      this.cancelling = null;
      this.publish(this.withoutActiveBuild());
      this.transitioning = false;
    }
    if (cancellationError) throw cancellationError;
    return true;
  }

  setOpenDock(next: DockId | null | ((current: DockId | null) => DockId | null)): void {
    const openDock = typeof next === 'function' ? next(this.current.openDock) : next;
    if (openDock === this.current.openDock) return;
    this.publish({ ...this.current, openDock });
  }

  setLayersAlongsideBuild(next: boolean | ((current: boolean) => boolean)): void {
    const layersAlongsideBuild = typeof next === 'function'
      ? next(this.current.layersAlongsideBuild)
      : next;
    if (layersAlongsideBuild === this.current.layersAlongsideBuild) return;
    this.publish({ ...this.current, layersAlongsideBuild });
  }

  toggleDock(which: DockId, visiblyOpen: boolean): DockToggleResult {
    const active = this.current.activeTool;
    if (which === 'layers' && active && LAYERS_ALONGSIDE_TOOLS.has(active)) {
      this.setLayersAlongsideBuild((open) => !open);
      return 'layers-alongside';
    }

    if (which !== 'layers') this.setLayersAlongsideBuild(false);
    if (active && (TOOL_DOCK[active] !== which || visiblyOpen)) this.cancelActive();
    this.setOpenDock(visiblyOpen ? null : which);
    return visiblyOpen ? 'closed' : 'opened';
  }

  private publish(next: ToolCoordinatorSnapshot): void {
    this.current = Object.freeze(next);
    this.onChange(this.current);
  }

  private withoutActiveBuild(): ToolCoordinatorSnapshot {
    return {
      ...this.current,
      activeTool: null,
      openDock: this.current.layersAlongsideBuild ? 'layers' : this.current.openDock,
      layersAlongsideBuild: false,
    };
  }
}
