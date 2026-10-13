// Translation cache keyed by (normalizedText, from, to, engine). Memory LRU in front of IndexedDB,
// with an entry cap enforced by evicting the least recently used rows.
import { LRU } from '../shared/lru';

export interface CacheBackend {
  get(key: string): Promise<string | undefined>;
  set(key: string, value: string): Promise<void>;
  clear(): Promise<void>;
  prune(max: number): Promise<number>;
  count(): Promise<number>;
}

export const cacheKey = (norm: string, from: string, to: string, engine: string): string => `${engine}|${from}|${to}|${norm}`;

export class MemoryCacheBackend implements CacheBackend {
  map = new Map<string, { v: string; used: number }>();
  private tick = 0;
  async get(key: string): Promise<string | undefined> { const e = this.map.get(key); if (e) e.used = ++this.tick; return e?.v; }
  async set(key: string, value: string): Promise<void> { this.map.set(key, { v: value, used: ++this.tick }); }
  async clear(): Promise<void> { this.map.clear(); }
  async count(): Promise<number> { return this.map.size; }
  async prune(max: number): Promise<number> {
    if (this.map.size <= max) return 0;
    const sorted = [...this.map.entries()].sort((a, b) => a[1].used - b[1].used);
    const drop = sorted.slice(0, this.map.size - max);
    for (const [k] of drop) this.map.delete(k);
    return drop.length;
  }
}

function req<T>(r: IDBRequest<T>): Promise<T> {
  return new Promise((res, rej) => { r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
}

export class IdbCacheBackend implements CacheBackend {
  private db: Promise<IDBDatabase>;
  constructor(name = 'brainbox-cache') {
    this.db = new Promise((res, rej) => {
      const open = indexedDB.open(name, 1);
      open.onupgradeneeded = () => {
        const s = open.result.createObjectStore('t', { keyPath: 'k' });
        s.createIndex('used', 'used');
      };
      open.onsuccess = () => res(open.result);
      open.onerror = () => rej(open.error);
    });
  }
  async get(key: string): Promise<string | undefined> {
    const db = await this.db;
    const store = db.transaction('t', 'readwrite').objectStore('t');
    const row = (await req(store.get(key))) as { k: string; v: string; used: number } | undefined;
    if (!row) return undefined;
    // Refresh recency at most once per minute per row to keep writes rare.
    if (Date.now() - row.used > 60_000) store.put({ ...row, used: Date.now() });
    return row.v;
  }
  async set(key: string, value: string): Promise<void> {
    const db = await this.db;
    await req(db.transaction('t', 'readwrite').objectStore('t').put({ k: key, v: value, used: Date.now() }));
  }
  async clear(): Promise<void> { const db = await this.db; await req(db.transaction('t', 'readwrite').objectStore('t').clear()); }
  async count(): Promise<number> { const db = await this.db; return req(db.transaction('t', 'readonly').objectStore('t').count()); }
  async prune(max: number): Promise<number> {
    const n = await this.count();
    if (n <= max) return 0;
    let toDrop = n - max, dropped = 0;
    const db = await this.db;
    const t = db.transaction('t', 'readwrite');
    await new Promise<void>((res, rej) => {
      const cur = t.objectStore('t').index('used').openCursor();
      cur.onsuccess = () => {
        const c = cur.result;
        if (!c || toDrop <= 0) { res(); return; }
        c.delete(); toDrop--; dropped++; c.continue();
      };
      cur.onerror = () => rej(cur.error);
    });
    return dropped;
  }
}

export class TranslationCache {
  private mem: LRU<string, string>;
  private writes = 0;
  memoryHits = 0;
  hits = 0;
  constructor(private backend: CacheBackend, private maxEntries = 20000, memEntries = 5000) {
    this.mem = new LRU(memEntries);
  }
  setMax(n: number): void { this.maxEntries = n; }
  async get(norm: string, from: string, to: string, engine: string): Promise<string | undefined> {
    const k = cacheKey(norm, from, to, engine);
    const m = this.mem.get(k);
    if (m !== undefined) { this.memoryHits++; this.hits++; return m; }
    const v = await this.backend.get(k).catch(() => undefined);
    if (v !== undefined) { this.mem.set(k, v); this.hits++; }
    return v;
  }
  async set(norm: string, from: string, to: string, engine: string, value: string): Promise<void> {
    const k = cacheKey(norm, from, to, engine);
    this.mem.set(k, value);
    await this.backend.set(k, value).catch(() => undefined);
    if (++this.writes % 200 === 0) void this.backend.prune(this.maxEntries).catch(() => undefined);
  }
  async clear(): Promise<void> { this.mem.clear(); await this.backend.clear(); }
}
