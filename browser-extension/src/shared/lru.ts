/** Map-backed LRU: get() refreshes recency, set() evicts the least recently used entry beyond `max`. */
export class LRU<K, V> {
  private map = new Map<K, V>();
  constructor(private max: number) {}

  get size(): number { return this.map.size; }

  get(key: K): V | undefined {
    const v = this.map.get(key);
    if (v === undefined) return undefined;
    this.map.delete(key);
    this.map.set(key, v);
    return v;
  }

  peek(key: K): V | undefined { return this.map.get(key); }
  has(key: K): boolean { return this.map.has(key); }

  set(key: K, value: V): this {
    if (this.map.has(key)) this.map.delete(key);
    this.map.set(key, value);
    while (this.map.size > this.max) {
      const oldest = this.map.keys().next();
      if (oldest.done) break;
      this.map.delete(oldest.value);
    }
    return this;
  }

  delete(key: K): boolean { return this.map.delete(key); }
  clear(): void { this.map.clear(); }
  resize(max: number): void {
    this.max = max;
    while (this.map.size > max) {
      const k = this.map.keys().next();
      if (k.done) break;
      this.map.delete(k.value);
    }
  }
  keys(): IterableIterator<K> { return this.map.keys(); }
}
