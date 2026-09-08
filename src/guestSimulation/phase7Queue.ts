/**
 * Fixed-capacity intrusive FIFO primitives for Phase 7.
 *
 * A node is an integer handle owned by the caller.  The queue stores only
 * prev/next links and a membership bit, so enqueue, dequeue, and remove are
 * O(1) and do not allocate.  Keeping capacity explicit makes backlog policy
 * observable instead of allowing an accidental unbounded queue.
 */

function validNode(node: number, capacity: number): void {
  if (!Number.isSafeInteger(node) || node < 0 || node >= capacity) {
    throw new RangeError(`queue node must be an integer in [0, ${capacity})`);
  }
}

function validCapacity(capacity: number): void {
  if (!Number.isSafeInteger(capacity) || capacity < 1) {
    throw new RangeError('queue capacity must be a positive safe integer');
  }
}

export class IntrusiveFifoQueue {
  public readonly capacity: number;
  private readonly previous: Int32Array;
  private readonly next: Int32Array;
  private readonly members: Uint8Array;
  private headNode = -1;
  private tailNode = -1;
  private queuedCount = 0;

  public constructor(capacity: number) {
    validCapacity(capacity);
    this.capacity = capacity;
    this.previous = new Int32Array(capacity);
    this.next = new Int32Array(capacity);
    this.members = new Uint8Array(capacity);
    this.previous.fill(-1);
    this.next.fill(-1);
  }

  public get size(): number {
    return this.queuedCount;
  }

  public get isEmpty(): boolean {
    return this.queuedCount === 0;
  }

  public get isFull(): boolean {
    return this.queuedCount === this.capacity;
  }

  public get head(): number | undefined {
    return this.headNode < 0 ? undefined : this.headNode;
  }

  public get tail(): number | undefined {
    return this.tailNode < 0 ? undefined : this.tailNode;
  }

  public contains(node: number): boolean {
    validNode(node, this.capacity);
    return this.members[node] !== 0;
  }

  /** Enqueue a node or throw when the caller violates ownership/capacity. */
  public enqueue(node: number): void {
    validNode(node, this.capacity);
    if (this.members[node] !== 0) throw new Error(`queue node ${node} is already enqueued`);
    if (this.isFull) throw new RangeError('intrusive FIFO queue is full');
    this.members[node] = 1;
    this.previous[node] = this.tailNode;
    this.next[node] = -1;
    if (this.tailNode < 0) this.headNode = node;
    else this.next[this.tailNode] = node;
    this.tailNode = node;
    this.queuedCount += 1;
  }

  /** Bounded producers can use this branch-free success/failure primitive. */
  public tryEnqueue(node: number): boolean {
    validNode(node, this.capacity);
    if (this.members[node] !== 0 || this.isFull) return false;
    this.enqueue(node);
    return true;
  }

  public dequeue(): number | undefined {
    if (this.headNode < 0) return undefined;
    const node = this.headNode;
    const successor = this.next[node];
    this.headNode = successor;
    if (successor < 0) this.tailNode = -1;
    else this.previous[successor] = -1;
    this.next[node] = -1;
    this.previous[node] = -1;
    this.members[node] = 0;
    this.queuedCount -= 1;
    return node;
  }

  /** Remove an arbitrary known node in O(1), preserving FIFO order of others. */
  public remove(node: number): boolean {
    validNode(node, this.capacity);
    if (this.members[node] === 0) return false;
    const predecessor = this.previous[node];
    const successor = this.next[node];
    if (predecessor < 0) this.headNode = successor;
    else this.next[predecessor] = successor;
    if (successor < 0) this.tailNode = predecessor;
    else this.previous[successor] = predecessor;
    this.previous[node] = -1;
    this.next[node] = -1;
    this.members[node] = 0;
    this.queuedCount -= 1;
    return true;
  }

  public clear(): void {
    let node = this.headNode;
    while (node >= 0) {
      const successor = this.next[node];
      this.previous[node] = -1;
      this.next[node] = -1;
      this.members[node] = 0;
      node = successor;
    }
    this.headNode = -1;
    this.tailNode = -1;
    this.queuedCount = 0;
  }

  /** Drain at most limit nodes; returns the number visited. */
  public drain(visit: (node: number) => void, limit = this.capacity): number {
    if (!Number.isSafeInteger(limit) || limit < 0) throw new RangeError('drain limit must be non-negative');
    let drained = 0;
    while (drained < limit) {
      const node = this.dequeue();
      if (node === undefined) break;
      visit(node);
      drained += 1;
    }
    return drained;
  }

  /** Debug invariant check; linear and intentionally not used by hot paths. */
  public assertConsistent(): void {
    let count = 0;
    let previous = -1;
    let node = this.headNode;
    while (node >= 0) {
      validNode(node, this.capacity);
      if (this.members[node] === 0 || this.previous[node] !== previous) {
        throw new Error('intrusive FIFO links are inconsistent');
      }
      previous = node;
      node = this.next[node];
      count += 1;
      if (count > this.capacity) throw new Error('intrusive FIFO contains a cycle');
    }
    if (count !== this.queuedCount || previous !== this.tailNode) {
      throw new Error('intrusive FIFO size or tail is inconsistent');
    }
    if ((this.headNode < 0) !== (this.tailNode < 0) || (this.queuedCount === 0) !== (count === 0)) {
      throw new Error('intrusive FIFO empty state is inconsistent');
    }
  }
}
