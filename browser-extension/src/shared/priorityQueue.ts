/** Lower number = more urgent. Visible content uses 0, nearby 1, history 2. */
export const Priority = { visible: 0, nearby: 1, history: 2, background: 3 } as const;

interface Entry<T> { key: string; priority: number; seq: number; value: T }

/**
 * Binary-heap priority queue with keyed entries: re-pushing a key updates its priority/value,
 * and entries can be cancelled by key (e.g. a message that scrolled away).
 */
export class PriorityQueue<T> {
  private heap: Entry<T>[] = [];
  private index = new Map<string, Entry<T>>();
  private seq = 0;

  get size(): number { return this.index.size; }
  has(key: string): boolean { return this.index.has(key); }
  get(key: string): T | undefined { return this.index.get(key)?.value; }

  push(key: string, value: T, priority: number): void {
    const existing = this.index.get(key);
    if (existing) {
      existing.value = value;
      if (priority < existing.priority) { existing.priority = priority; this.rebuild(); }
      return;
    }
    const e: Entry<T> = { key, priority, seq: this.seq++, value };
    this.index.set(key, e);
    this.heap.push(e);
    this.up(this.heap.length - 1);
  }

  /** Changes priority in either direction. */
  reprioritize(key: string, priority: number): boolean {
    const e = this.index.get(key);
    if (!e || e.priority === priority) return !!e;
    e.priority = priority;
    this.rebuild();
    return true;
  }

  cancel(key: string): boolean {
    const e = this.index.get(key);
    if (!e) return false;
    this.index.delete(key);
    this.heap = this.heap.filter((x) => x !== e);
    this.rebuild();
    return true;
  }

  pop(): { key: string; value: T; priority: number } | undefined {
    const top = this.heap[0];
    if (!top) return undefined;
    const last = this.heap.pop()!;
    if (this.heap.length) { this.heap[0] = last; this.down(0); }
    this.index.delete(top.key);
    return { key: top.key, value: top.value, priority: top.priority };
  }

  peekPriority(): number | undefined { return this.heap[0]?.priority; }

  /** Pops up to n entries, all of the most urgent priority class first. */
  popMany(n: number): { key: string; value: T; priority: number }[] {
    const out: { key: string; value: T; priority: number }[] = [];
    while (out.length < n) { const e = this.pop(); if (!e) break; out.push(e); }
    return out;
  }

  clear(): void { this.heap = []; this.index.clear(); }

  private less(a: Entry<T>, b: Entry<T>): boolean {
    return a.priority < b.priority || (a.priority === b.priority && a.seq < b.seq);
  }
  private up(i: number): void {
    const h = this.heap;
    while (i > 0) {
      const p = (i - 1) >> 1;
      if (!this.less(h[i]!, h[p]!)) break;
      [h[i], h[p]] = [h[p]!, h[i]!];
      i = p;
    }
  }
  private down(i: number): void {
    const h = this.heap;
    for (;;) {
      const l = 2 * i + 1, r = l + 1;
      let m = i;
      if (l < h.length && this.less(h[l]!, h[m]!)) m = l;
      if (r < h.length && this.less(h[r]!, h[m]!)) m = r;
      if (m === i) return;
      [h[i], h[m]] = [h[m]!, h[i]!];
      i = m;
    }
  }
  private rebuild(): void {
    for (let i = (this.heap.length >> 1) - 1; i >= 0; i--) this.down(i);
  }
}
