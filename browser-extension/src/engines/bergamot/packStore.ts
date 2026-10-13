// Stores downloaded (already verified + decompressed) model files. IndexedDB in the extension;
// an in-memory implementation for tests and Node.

export type PackFiles = Record<string, ArrayBuffer>;
export interface PackMeta { pair: string; bytes: number; installedAt: number; architecture: string }

export interface PackStore {
  has(pair: string): Promise<boolean>;
  get(pair: string): Promise<PackFiles | null>;
  put(pair: string, files: PackFiles, meta: PackMeta): Promise<void>;
  delete(pair: string): Promise<void>;
  list(): Promise<PackMeta[]>;
}

export class MemoryPackStore implements PackStore {
  private files = new Map<string, PackFiles>();
  private meta = new Map<string, PackMeta>();
  async has(pair: string): Promise<boolean> { return this.files.has(pair); }
  async get(pair: string): Promise<PackFiles | null> { return this.files.get(pair) ?? null; }
  async put(pair: string, files: PackFiles, meta: PackMeta): Promise<void> { this.files.set(pair, files); this.meta.set(pair, meta); }
  async delete(pair: string): Promise<void> { this.files.delete(pair); this.meta.delete(pair); }
  async list(): Promise<PackMeta[]> { return [...this.meta.values()]; }
}

function req<T>(r: IDBRequest<T>): Promise<T> {
  return new Promise((res, rej) => { r.onsuccess = () => res(r.result); r.onerror = () => rej(r.error); });
}

export class IdbPackStore implements PackStore {
  private db: Promise<IDBDatabase>;
  private known = new Map<string, boolean>();
  constructor(name = 'brainbox-packs') {
    this.db = new Promise((res, rej) => {
      const open = indexedDB.open(name, 1);
      open.onupgradeneeded = () => {
        open.result.createObjectStore('files');
        open.result.createObjectStore('meta', { keyPath: 'pair' });
      };
      open.onsuccess = () => res(open.result);
      open.onerror = () => rej(open.error);
    });
  }
  private async tx(stores: string[], mode: IDBTransactionMode): Promise<IDBTransaction> {
    return (await this.db).transaction(stores, mode);
  }
  async has(pair: string): Promise<boolean> {
    const k = this.known.get(pair);
    if (k !== undefined) return k;
    const t = await this.tx(['meta'], 'readonly');
    const v = !!(await req(t.objectStore('meta').get(pair)));
    this.known.set(pair, v);
    return v;
  }
  async get(pair: string): Promise<PackFiles | null> {
    const t = await this.tx(['files'], 'readonly');
    return ((await req(t.objectStore('files').get(pair))) as PackFiles | undefined) ?? null;
  }
  async put(pair: string, files: PackFiles, meta: PackMeta): Promise<void> {
    const t = await this.tx(['files', 'meta'], 'readwrite');
    t.objectStore('files').put(files, pair);
    t.objectStore('meta').put(meta);
    await new Promise<void>((res, rej) => { t.oncomplete = () => res(); t.onerror = () => rej(t.error); });
    this.known.set(pair, true);
  }
  async delete(pair: string): Promise<void> {
    const t = await this.tx(['files', 'meta'], 'readwrite');
    t.objectStore('files').delete(pair);
    t.objectStore('meta').delete(pair);
    await new Promise<void>((res, rej) => { t.oncomplete = () => res(); t.onerror = () => rej(t.error); });
    this.known.set(pair, false);
  }
  async list(): Promise<PackMeta[]> {
    const t = await this.tx(['meta'], 'readonly');
    return (await req(t.objectStore('meta').getAll())) as PackMeta[];
  }
}
