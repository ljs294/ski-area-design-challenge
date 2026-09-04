import { describe, expect, it } from 'vitest';
import { ContinuousSimulationCoordinator, SIMULATION_SPEED_RATES } from './continuousSimulationCoordinator';

describe('ContinuousSimulationCoordinator', () => {
  it('accumulates continuous seconds and exposes bounded worker targets', () => {
    const coordinator = new ContinuousSimulationCoordinator(100, 'normal');
    coordinator.advanceWall(1_000);
    coordinator.advanceWall(1_050);
    expect(coordinator.status().targetSecond).toBe(103);
    expect(coordinator.nextTarget()).toBe(103);
  });

  it('discards hidden time without deleting a visible wall interval', () => {
    const coordinator = new ContinuousSimulationCoordinator(0, 'normal');
    coordinator.advanceWall(0);
    coordinator.advanceWall(10_000, true);
    coordinator.advanceWall(11_000);
    expect(coordinator.status().targetSecond).toBe(60);
  });

  it('throttles after repeated over-budget work without dropping the target', () => {
    const coordinator = new ContinuousSimulationCoordinator(0, 'fast');
    coordinator.advanceWall(0);
    coordinator.advanceWall(50);
    const target = coordinator.status().targetSecond;
    coordinator.acknowledge(4, 9, 50);
    coordinator.acknowledge(8, 9, 100);
    const status = coordinator.acknowledge(12, 9, 150);
    expect(status.throttled).toBe(true);
    expect(status.effectiveSpeed).toBe('normal');
    expect(status.targetSecond).toBe(target);
  });

  it('uses the approved tier rates', () => {
    expect(SIMULATION_SPEED_RATES).toEqual({ slow: 30, normal: 60, fast: 240, ultrafast: 960 });
  });

  it('accepts a monotonic fractional acknowledgement without rounding it', () => {
    const coordinator = new ContinuousSimulationCoordinator(0, 'normal');
    coordinator.advanceWall(0);
    coordinator.advanceWall(50);
    expect(coordinator.acknowledge(2.5, 1, 50).committedSecond).toBe(2.5);
  });
});
