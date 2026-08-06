import { describe, expect, it, vi } from 'vitest';
import { ConstructionLock } from './constructionLock';

describe('ConstructionLock', () => {
  it('takes ownership synchronously, before the operation reaches its first await', () => {
    const published: (string | null)[] = [];
    const lock = new ConstructionLock((activity) => published.push(activity));
    let ownedAtEntry: string | null = null;

    void lock.run('dam', async () => {
      ownedAtEntry = lock.active;
      await Promise.resolve();
    });

    expect(ownedAtEntry).toBe('dam');
    expect(published).toEqual(['dam']);
  });

  it('rejects a second confirmation dispatched in the same tick', async () => {
    const lock = new ConstructionLock();
    const first = lock.run('road', () => Promise.resolve('built'));
    const second = lock.run('road', () => Promise.resolve('built twice'));

    expect(await second).toEqual({ ok: false, reason: 'busy' });
    expect(await first).toEqual({ ok: true, value: 'built' });
  });

  it('releases ownership after the operation succeeds', async () => {
    const published: (string | null)[] = [];
    const lock = new ConstructionLock((activity) => published.push(activity));

    await lock.run('pond', () => Promise.resolve(undefined));

    expect(lock.active).toBeNull();
    expect(published).toEqual(['pond', null]);
  });

  it('releases ownership after the operation throws, and rethrows', async () => {
    const lock = new ConstructionLock();

    await expect(lock.run('trail', () => Promise.reject(new Error('grade failed'))))
      .rejects.toThrow('grade failed');
    expect(lock.active).toBeNull();
    expect(lock.acquire('trail')).not.toBeNull();
  });

  it('releases ownership when the operation is cancelled mid-flight', async () => {
    const lock = new ConstructionLock();
    const cancelled = new Error('cancelled');

    await lock.run('lift', async () => {
      await Promise.resolve();
      throw cancelled;
    }).catch(() => {});

    expect(lock.active).toBeNull();
  });

  it('does not let an older operation release ownership belonging to a newer one', () => {
    const publish = vi.fn();
    const lock = new ConstructionLock(publish);

    const older = lock.acquire('road');
    older?.release();
    const newer = lock.acquire('trail');
    older?.release();

    expect(lock.active).toBe('trail');
    expect(newer?.activity).toBe('trail');
    expect(publish.mock.calls.map(([activity]) => activity)).toEqual(['road', null, 'trail']);
  });

  it('ignores a repeated release from the operation that still owns the lock', () => {
    const publish = vi.fn();
    const lock = new ConstructionLock(publish);

    const handle = lock.acquire('dam');
    handle?.release();
    handle?.release();

    expect(publish.mock.calls.map(([activity]) => activity)).toEqual(['dam', null]);
  });

  it('drops ownership on disposal and stays usable afterwards', () => {
    const published: (string | null)[] = [];
    const lock = new ConstructionLock((activity) => published.push(activity));

    const handle = lock.acquire('pond');
    lock.dispose();
    handle?.release();

    expect(lock.active).toBeNull();
    expect(published).toEqual(['pond', null]);
    expect(lock.acquire('lift')?.activity).toBe('lift');
  });
});
