import { describe, expect, it, vi } from 'vitest';
import type { Mock } from 'vitest';
import type { CoverGrid } from '../types';
import { coverMetadataOf } from '../terrainPackage';
import type { CoverEditPayload, CoverEditRequest, CoverEditResponse } from './coverEditProtocol';
import { CoverEditAdapter } from './coverEditClient';
import type { WorkerLike } from './workerAdapter';

function request(): CoverEditPayload {
  return {
    grid: {
      bounds: { west: 0, east: 1, south: 0, north: 1 },
      width: 2, height: 2, cellSizeM: 1,
      data: new Uint8Array([1, 1, 1, 1]),
      complete: true, nodataCount: 0,
      source: 'esa-worldcover-2021-v200', vintage: '2021',
    } as CoverGrid,
    clearings: [{
      polygon: [[
        [0.25, 0.25], [0.75, 0.25], [0.75, 0.75],
        [0.25, 0.75], [0.25, 0.25],
      ]],
    }],
    deriveDisplay: false,
  };
}

/** A cleared grid and the response a worker would return for it. */
function cleared(
  input: CoverEditPayload,
  id = 1,
): Extract<CoverEditResponse, { ok: true }> {
  const gridData = new Uint8Array([3, 1, 1, 1]);
  return {
    id,
    ok: true,
    changed: 1,
    gridData,
    coverMetadata: coverMetadataOf({ ...input.grid, data: gridData }),
  };
}

interface FakeWorker extends WorkerLike<CoverEditRequest, CoverEditResponse> {
  transfer: Transferable[];
  terminate: Mock<() => void>;
  deliver(response: CoverEditResponse): void;
  crash(): void;
  failPost: boolean;
}

/** A factory plus the workers it has been asked for; none answer on their own. */
function fakeWorkers() {
  const created: FakeWorker[] = [];
  const factory = (): FakeWorker => {
    const worker: FakeWorker = {
      onmessage: null,
      onerror: null,
      transfer: [],
      terminate: vi.fn(() => {}),
      failPost: false,
      postMessage(_message: CoverEditRequest, transfer: Transferable[] = []) {
        if (worker.failPost) throw new DOMException('could not clone', 'DataCloneError');
        worker.transfer = transfer;
      },
      deliver(response: CoverEditResponse) {
        worker.onmessage?.({ data: response } as MessageEvent<CoverEditResponse>);
      },
      crash() {
        worker.onerror?.({} as ErrorEvent);
      },
    };
    created.push(worker);
    return worker;
  };
  return { created, factory };
}

describe('CoverEditAdapter', () => {
  it('transfers only the grid buffer and preserves a successful metadata response', async () => {
    const { created, factory } = fakeWorkers();
    const input = request();
    const response = cleared(input);
    const pending = new CoverEditAdapter({ workerFactory: factory }).run(input);
    expect(created[0].transfer).toEqual([(input.grid.data as Uint8Array).buffer]);
    created[0].deliver(response);

    const result = await pending;
    expect(result).toMatchObject({
      changed: 1,
      coverMetadata: {
        byteLength: response.gridData.byteLength,
        checksum: response.coverMetadata.checksum,
      },
    });
    expect(result.gridData).toBe(response.gridData);
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('rejects a refused edit, a crashed worker, and a timeout, always terminating', async () => {
    const refused = fakeWorkers();
    const refusal = new CoverEditAdapter({ workerFactory: refused.factory }).run(request());
    refused.created[0].deliver({ id: 1, ok: false, error: 'bad cover' });
    await expect(refusal).rejects.toThrow('bad cover');
    expect(refused.created[0].terminate).toHaveBeenCalledOnce();

    const crashed = fakeWorkers();
    const crashing = new CoverEditAdapter({ workerFactory: crashed.factory }).run(request());
    crashed.created[0].crash();
    await expect(crashing).rejects.toThrow('Ground-cover worker stopped unexpectedly.');
    expect(crashed.created[0].terminate).toHaveBeenCalledOnce();

    const stalled = fakeWorkers();
    await expect(new CoverEditAdapter({ workerFactory: stalled.factory, timeoutMs: 1 })
      .run(request())).rejects.toThrow('timed out');
    expect(stalled.created[0].terminate).toHaveBeenCalledOnce();
  });

  it('settles once, so a late answer cannot resolve an edit that already failed', async () => {
    const { created, factory } = fakeWorkers();
    const adapter = new CoverEditAdapter({ workerFactory: factory, timeoutMs: 1 });
    const outcome = adapter.run(request())
      .then(() => 'resolved', (error: Error) => error.message);

    // The terrain document serializes cover edits, so a second one starting is
    // a caller mistake: report it rather than run two stamps at once.
    const superseding = adapter.run(request());
    expect(await outcome).toBe('The ground-cover edit was abandoned.');
    expect(created[0].terminate).toHaveBeenCalledOnce();

    created[0].deliver(cleared(request()));
    await expect(superseding).rejects.toThrow('timed out');
  });

  it('rejects and terminates when the worker refuses the request message', async () => {
    const created: FakeWorker[] = [];
    const factory = (): FakeWorker => {
      const worker = fakeWorkers().factory();
      worker.failPost = true;
      created.push(worker);
      return worker;
    };

    await expect(new CoverEditAdapter({ workerFactory: factory }).run(request()))
      .rejects.toThrow('Ground-cover processing could not start.');
    expect(created[0].terminate).toHaveBeenCalledOnce();
  });

  it('abandons the edit in flight on teardown and stays usable afterwards', async () => {
    const { created, factory } = fakeWorkers();
    const adapter = new CoverEditAdapter({ workerFactory: factory });
    const abandoned = adapter.run(request());
    adapter.dispose();
    await expect(abandoned).rejects.toThrow('The ground-cover edit was abandoned.');
    expect(created[0].terminate).toHaveBeenCalledOnce();

    // Teardown is abandonment, not retirement: a StrictMode remount still works.
    const input = request();
    const remounted = adapter.run(input);
    created[1].deliver(cleared(input, 2));
    await expect(remounted).resolves.toMatchObject({ changed: 1 });
  });

  it('ignores another request identity and rejects a malformed current response', async () => {
    const mismatched = fakeWorkers();
    const adapter = new CoverEditAdapter({ workerFactory: mismatched.factory });
    const input = request();
    const pending = adapter.run(input);

    mismatched.created[0].deliver(cleared(input, 99));
    mismatched.created[0].deliver(cleared(input, 1));
    await expect(pending).resolves.toMatchObject({ id: 1, changed: 1 });

    const malformed = fakeWorkers();
    const invalid = new CoverEditAdapter({ workerFactory: malformed.factory }).run(request());
    malformed.created[0].deliver({ id: 1, ok: true } as CoverEditResponse);
    await expect(invalid).rejects.toThrow(
      'Ground-cover processing returned an invalid response.');
    expect(malformed.created[0].terminate).toHaveBeenCalledOnce();
  });
});
