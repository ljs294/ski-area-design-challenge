import { describe, expect, it, vi } from 'vitest';
import { ToolCoordinator, type ToolCoordinatorSnapshot } from './toolCoordinator';

describe('ToolCoordinator', () => {
  it('cancels the prior tool synchronously before publishing the replacement', () => {
    const events: string[] = [];
    const coordinator = new ToolCoordinator((snapshot) => events.push(`publish:${snapshot.activeTool}`));
    coordinator.register('lift', () => {
      events.push(`cancel:${coordinator.snapshot.activeTool}`);
      coordinator.release('lift');
    });

    coordinator.activate('lift');
    coordinator.activate('road');

    expect(events).toEqual(['publish:lift', 'cancel:lift', 'publish:road']);
    expect(coordinator.snapshot.activeTool).toBe('road');
  });

  it('ignores completion or release from anything except the active tool', () => {
    const coordinator = new ToolCoordinator();
    coordinator.activate('trail');

    expect(coordinator.release('road')).toBe(false);
    expect(coordinator.snapshot.activeTool).toBe('trail');
    expect(coordinator.release('trail')).toBe(true);
    expect(coordinator.release('trail')).toBe(false);
    expect(coordinator.snapshot.activeTool).toBeNull();
  });

  it('cancels the active tool once when its dock closes', () => {
    const cancel = vi.fn();
    const coordinator = new ToolCoordinator();
    coordinator.register('ski-path', cancel);
    coordinator.setOpenDock('trails');
    coordinator.activate('ski-path');

    expect(coordinator.toggleDock('trails', true)).toBe('closed');
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(coordinator.snapshot).toEqual({
      activeTool: null,
      openDock: null,
      layersAlongsideBuild: false,
    });
  });

  it.each(['road', 'dam', 'pond', 'trail', 'building'] as const)(
    'keeps the active %s tool while toggling Layers alongside it',
    (toolId) => {
      const states: ToolCoordinatorSnapshot[] = [];
      const coordinator = new ToolCoordinator((snapshot) => states.push(snapshot));
      coordinator.activate(toolId);

      expect(coordinator.toggleDock('layers', false)).toBe('layers-alongside');
      expect(coordinator.snapshot.activeTool).toBe(toolId);
      expect(coordinator.snapshot.layersAlongsideBuild).toBe(true);
      expect(states.at(-1)?.layersAlongsideBuild).toBe(true);
    },
  );

  it('cancels a lift before opening Layers because lift does not support the alongside layout', () => {
    const cancel = vi.fn();
    const coordinator = new ToolCoordinator();
    coordinator.register('lift', cancel);
    coordinator.activate('lift');

    expect(coordinator.toggleDock('layers', false)).toBe('opened');
    expect(cancel).toHaveBeenCalledTimes(1);
    expect(coordinator.snapshot).toEqual({
      activeTool: null,
      openDock: 'layers',
      layersAlongsideBuild: false,
    });
  });

  it('turns an alongside Layers panel into the normal dock when a build ends', () => {
    const coordinator = new ToolCoordinator();
    coordinator.activate('road');
    coordinator.toggleDock('layers', false);

    coordinator.release('road');

    expect(coordinator.snapshot).toEqual({
      activeTool: null,
      openDock: 'layers',
      layersAlongsideBuild: false,
    });
  });
});
