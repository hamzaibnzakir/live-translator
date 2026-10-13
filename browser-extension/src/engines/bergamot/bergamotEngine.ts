// Mozilla Bergamot (Firefox Translations) running as WASM on the CPU. One worker per language
// pair ("lane"), so a slow Japanese batch never blocks French; at most `maxWorkers` stay alive.
import type { Availability, PackProgress, TranslationEngine } from '../types';
import { downloadPair, type Fetcher } from './download';
import type { PackStore } from './packStore';
import { modelsFor, pairDownloadSize, pairInstalledSize, REGISTRY, type PinnedRegistry } from './registry';
import { WorkerClient, type WorkerLike } from './workerClient';

export interface BergamotOptions {
  store: PackStore;
  createWorker: () => WorkerLike;
  baseUrl: () => string;
  fetcher?: Fetcher;
  registry?: PinnedRegistry;
  maxWorkers?: number;
}

interface Lane { key: string; models: string[]; client: Promise<WorkerClient>; lastUsed: number; chain: Promise<unknown> }

export class BergamotEngine implements TranslationEngine {
  readonly id = 'bergamot';
  private lanes = new Map<string, Lane>();
  private downloads = new Map<string, Promise<void>>();
  private registry: PinnedRegistry;
  private wasmBytes = new Map<string, number>();

  constructor(private o: BergamotOptions) {
    this.registry = o.registry ?? REGISTRY;
  }

  supported(): boolean { return typeof WebAssembly === 'object'; }

  models(from: string, to: string): string[] | null { return modelsFor(from, to, this.registry); }

  async availability(from: string, to: string): Promise<Availability> {
    const models = this.models(from, to);
    if (!models) return 'unsupported';
    const installed = await Promise.all(models.map((m) => this.o.store.has(m)));
    if (installed.every(Boolean)) return 'ready';
    return models.some((m) => this.downloads.has(m)) ? 'downloading' : 'downloadable';
  }

  packSize(from: string, to: string): number {
    return (this.models(from, to) ?? []).reduce((s, m) => s + pairDownloadSize(m, this.registry), 0);
  }

  async ensurePack(from: string, to: string, onProgress?: (p: PackProgress) => void): Promise<void> {
    const models = this.models(from, to);
    if (!models) throw new Error(`No Bergamot model for ${from}→${to}`);
    const missing: string[] = [];
    for (const m of models) if (!(await this.o.store.has(m))) missing.push(m);
    const total = missing.reduce((s, m) => s + pairDownloadSize(m, this.registry), 0);
    const loaded = new Map<string, number>();
    const report = (): void => onProgress?.({ loaded: [...loaded.values()].reduce((a, b) => a + b, 0), total });
    await Promise.all(missing.map((m) => this.downloadModel(m, (l) => { loaded.set(m, l); report(); })));
    onProgress?.({ loaded: total, total });
  }

  private downloadModel(pair: string, onLoaded: (loaded: number) => void): Promise<void> {
    let p = this.downloads.get(pair);
    if (!p) {
      const pinned = this.registry.pairs[pair];
      if (!pinned) return Promise.reject(new Error(`Unknown pair ${pair}`));
      p = (async () => {
        const files = await downloadPair(this.o.baseUrl(), pinned, this.o.fetcher ?? ((u) => fetch(u, { credentials: 'omit', cache: 'no-store' })), (l) => onLoaded(l));
        await this.o.store.put(pair, files, { pair, bytes: pairInstalledSize(pair, this.registry), installedAt: Date.now(), architecture: pinned.architecture });
      })().finally(() => this.downloads.delete(pair));
      this.downloads.set(pair, p);
    }
    return p;
  }

  async removePack(from: string, to: string): Promise<void> {
    const pair = `${from}-${to}`;
    for (const [key, lane] of this.lanes) if (lane.models.includes(pair)) this.closeLane(key);
    await this.o.store.delete(pair);
  }

  private closeLane(key: string): void {
    const lane = this.lanes.get(key);
    if (!lane) return;
    this.lanes.delete(key);
    this.wasmBytes.delete(key);
    void lane.client.then((c) => c.terminate()).catch(() => undefined);
  }

  private lane(from: string, to: string): Lane {
    const key = `${from}-${to}`;
    let lane = this.lanes.get(key);
    if (lane) { lane.lastUsed = Date.now(); return lane; }
    const models = this.models(from, to);
    if (!models) throw new Error(`No Bergamot model for ${from}→${to}`);
    const max = this.o.maxWorkers ?? 3;
    while (this.lanes.size >= max) {
      const oldest = [...this.lanes.values()].sort((a, b) => a.lastUsed - b.lastUsed)[0];
      if (!oldest) break;
      this.closeLane(oldest.key);
    }
    const client = (async () => {
      const c = new WorkerClient(this.o.createWorker());
      await c.call('initialize', [{ cacheSize: 0, useNativeIntGemm: false }]);
      for (const m of models) {
        const files = await this.o.store.get(m);
        if (!files) throw new Error(`Pack ${m} is not installed`);
        const [mf, mt] = m.split('-') as [string, string];
        const vocabs = files.vocab ? [files.vocab] : [files.srcVocab!, files.trgVocab!];
        await c.call('loadTranslationModel', [{ from: mf, to: mt }, { model: files.model, shortlist: files.lexicalShortlist, vocabs }]);
      }
      return c;
    })();
    lane = { key, models, client, lastUsed: Date.now(), chain: Promise.resolve() };
    client.catch(() => this.lanes.delete(key));
    this.lanes.set(key, lane);
    return lane;
  }

  async translate(texts: string[], from: string, to: string): Promise<string[]> {
    if (!texts.length) return [];
    const lane = this.lane(from, to);
    const run = async (): Promise<string[]> => {
      const c = await lane.client;
      const models = lane.models.map((m) => { const [f, t] = m.split('-'); return { from: f, to: t }; });
      const res = await c.call<{ target: { text: string } }[]>('translate', [{ models, texts: texts.map((text) => ({ text, html: false })) }]);
      void c.call<number>('wasmMemoryBytes').then((b) => this.wasmBytes.set(lane.key, b)).catch(() => undefined);
      return res.map((r) => r.target.text);
    };
    const next = lane.chain.then(run, run);
    lane.chain = next.catch(() => undefined);
    return next;
  }

  stats(): { pair: string; wasmBytes: number }[] {
    return [...this.lanes.keys()].map((pair) => ({ pair, wasmBytes: this.wasmBytes.get(pair) ?? 0 }));
  }

  dispose(): void { for (const k of [...this.lanes.keys()]) this.closeLane(k); }
}
