import { describe, expect, it, vi } from 'vitest';
import { SnowmakingNetworkDocument, snowmakingNetworkProjection } from './snowmakingNetworkDocument';
import type { SnowmakingNetworkState } from '../snowmakingNetwork';

const initial: SnowmakingNetworkState = {
  nodes: [], pipes: [], nextNumbers: { hydrant: 1, junction: 1, pump: 1 },
};

describe('SnowmakingNetworkDocument', () => {
  it('owns a deeply frozen revision-zero snapshot', () => {
    const document = new SnowmakingNetworkDocument(initial);
    const snapshot = document.snapshot();
    expect(snapshot.revision).toBe(0);
    expect(Object.isFrozen(snapshot)).toBe(true);
    expect(Object.isFrozen(snapshot.nextNumbers)).toBe(true);
    const projection = snowmakingNetworkProjection(snapshot);
    projection.nextNumbers.hydrant = 10;
    expect(document.snapshot().nextNumbers.hydrant).toBe(1);
  });

  it('publishes all changed graph collections once', () => {
    const change = vi.fn();
    const document = new SnowmakingNetworkDocument(initial, change);
    const edit = document.begin();
    edit.replace({ nodes: [], pipes: [], nextNumbers: { hydrant: 2, junction: 1, pump: 1 } });
    expect(edit.commit()).toEqual({ ok: true, revision: 1, changed: true });
    expect(change).toHaveBeenCalledTimes(1);
  });

  it('rejects stale and settled transactions without publication', () => {
    const document = new SnowmakingNetworkDocument(initial);
    const stale = document.begin();
    const winner = document.begin();
    winner.replace({ ...initial, nextNumbers: { hydrant: 2, junction: 1, pump: 1 } });
    winner.commit();
    stale.replace({ ...initial, nextNumbers: { hydrant: 3, junction: 1, pump: 1 } });
    expect(stale.commit()).toEqual({ ok: false, reason: 'stale' });
    expect(stale.commit()).toEqual({ ok: false, reason: 'settled' });
  });
});
