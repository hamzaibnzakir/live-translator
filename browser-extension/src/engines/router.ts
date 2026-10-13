// Orchestrates one translation request: skip rules → detection → engine choice → cache →
// in-flight de-duplication → per-pair priority lanes (cancellable) → engine.
import type { EngineStats, TranslateItem, TranslateResult } from '../shared/messages';
import { normalizeText } from '../shared/normalize';
import { PriorityQueue } from '../shared/priorityQueue';
import type { Settings } from '../shared/settings';
import { skipReason } from '../shared/skipRules';
import type { TranslationCache } from './cache';
import { detectOffline } from './detect';
import type { Availability, Detection, TranslationEngine } from './types';

interface Job { text: string; resolve: (v: string) => void; reject: (e: Error) => void }

class Lane {
  queue = new PriorityQueue<Job>();
  running = false;
  constructor(public engine: TranslationEngine, public from: string, public to: string, private batchSize: number, private onBatch: (n: number) => void) {}

  add(key: string, text: string, priority: number): Promise<string> {
    return new Promise((resolve, reject) => {
      this.queue.push(key, { text, resolve, reject }, priority);
      void this.pump();
    });
  }

  cancel(key: string): void {
    // Only queued (not started) work can be cancelled; its promise rejects with "cancelled".
    const job = this.queue.get(key);
    if (job && this.queue.cancel(key)) job.reject(new Error('cancelled'));
  }

  private async pump(): Promise<void> {
    if (this.running) return;
    this.running = true;
    try {
      while (this.queue.size) {
        await Promise.resolve(); // let same-tick requests join the batch
        const batch = this.queue.popMany(this.batchSize);
        this.onBatch(batch.length);
        try {
          const out = await this.engine.translate(batch.map((b) => b.value.text), this.from, this.to);
          batch.forEach((b, i) => b.value.resolve(out[i] ?? ''));
        } catch (e) {
          batch.forEach((b) => b.value.reject(e as Error));
        }
      }
    } finally {
      this.running = false;
    }
  }
}

export interface RouterOptions {
  engines: TranslationEngine[];
  cache: TranslationCache;
  settings: () => Settings;
  /** Called when a Bergamot pack is fetched automatically (autoDownloadPacks). */
  onAutoDownload?: (engine: TranslationEngine, from: string, to: string) => Promise<void>;
}

export class EngineRouter {
  private lanes = new Map<string, Lane>();
  /** One engine job per distinct text; several requests (waiters) can share it. */
  private inflight = new Map<string, { promise: Promise<string>; waiters: Set<string>; lane: Lane; laneKey: string }>();
  private flightOf = new Map<string, string>();
  private availCache = new Map<string, { at: number; v: Availability }>();
  private detectCache = new Map<string, Detection | null>();
  /** Engines that failed for a pair (e.g. Chrome's "Other generic failures occurred"): skipped for a while. */
  private broken = new Map<string, { until: number; reason: string }>();
  stats: EngineStats = { engineCalls: 0, engineTexts: 0, cacheHits: 0, memoryCacheHits: 0, detectCalls: 0, workers: [] };

  constructor(private o: RouterOptions) {}

  engine(id: string): TranslationEngine | undefined { return this.o.engines.find((e) => e.id === id); }

  invalidateAvailability(): void { this.availCache.clear(); }

  resetStats(): void { this.stats = { engineCalls: 0, engineTexts: 0, cacheHits: 0, memoryCacheHits: 0, detectCalls: 0, workers: [] }; }

  async availability(engine: TranslationEngine, from: string, to: string): Promise<Availability> {
    const k = `${engine.id}|${from}|${to}`;
    const c = this.availCache.get(k);
    if (c && Date.now() - c.at < (c.v === 'ready' ? 60_000 : 3_000)) return c.v;
    const v = engine.supported() ? await engine.availability(from, to) : 'unsupported';
    this.availCache.set(k, { at: Date.now(), v });
    return v;
  }

  /** Engines in preference order for the current setting. */
  ordered(): TranslationEngine[] {
    const pref = this.o.settings().engine;
    const by = (id: string): TranslationEngine | undefined => this.engine(id);
    const list = pref === 'bergamot' ? [by('bergamot')] : pref === 'chrome' ? [by('chrome'), by('bergamot')] : [by('chrome'), by('bergamot'), ...this.o.engines];
    return [...new Set(list.filter((e): e is TranslationEngine => !!e && e.supported()))];
  }

  /** First engine whose pack is ready, else the first one that could become ready. */
  async choose(from: string, to: string): Promise<{ engine: TranslationEngine; state: Availability } | null> {
    const engines = this.ordered();
    let fallback: { engine: TranslationEngine; state: Availability } | null = null;
    for (const engine of engines) {
      if (this.isBroken(engine.id, from, to)) continue;
      const state = await this.availability(engine, from, to);
      if (state === 'ready') return { engine, state };
      if (state === 'unsupported') continue;
      // With auto-download on, prefer Bergamot: it can fetch its pack without a user gesture.
      const better = !fallback || (this.o.settings().autoDownloadPacks && engine.id === 'bergamot' && fallback.engine.id !== 'bergamot');
      if (better) fallback = { engine, state };
    }
    return fallback;
  }

  isBroken(engine: string, from: string, to: string): boolean {
    const b = this.broken.get(`${engine}|${from}|${to}`);
    if (b && b.until < Date.now()) this.broken.delete(`${engine}|${from}|${to}`);
    return !!b && b.until >= Date.now();
  }

  /** What failed recently, for the popup / settings status line. */
  failures(): { engine: string; pair: string; reason: string }[] {
    return [...this.broken.entries()].filter(([, b]) => b.until >= Date.now()).map(([k, b]) => { const [engine, f, t] = k.split('|'); return { engine: engine!, pair: `${f}-${t}`, reason: b.reason }; });
  }

  private markBroken(engine: string, from: string, to: string, reason: string): void {
    this.broken.set(`${engine}|${from}|${to}`, { until: Date.now() + 10 * 60_000, reason });
    this.onEngineFailure?.(engine, from, to, reason);
  }

  onEngineFailure?: (engine: string, from: string, to: string, reason: string) => void;

  async detect(text: string): Promise<Detection | null> {
    const norm = normalizeText(text);
    if (this.detectCache.has(norm)) return this.detectCache.get(norm) ?? null;
    this.stats.detectCalls++;
    let d: Detection | null = null;
    const chrome = this.engine('chrome');
    if (chrome?.supported() && chrome.detect) d = await chrome.detect(norm).catch(() => null);
    if (!d) d = detectOffline(norm);
    if (this.detectCache.size > 5000) this.detectCache.clear();
    this.detectCache.set(norm, d);
    return d;
  }

  /**
   * Cancels queued work for requests that no longer need it (message scrolled away). A shared
   * job is only cancelled when nobody else is still waiting for the same text.
   */
  cancel(keys: string[]): void {
    for (const k of keys) {
      const fk = this.flightOf.get(k);
      this.flightOf.delete(k);
      const f = fk ? this.inflight.get(fk) : undefined;
      if (!f) continue;
      f.waiters.delete(k);
      if (f.waiters.size === 0) f.lane.cancel(f.laneKey);
    }
  }

  async translateItems(items: TranslateItem[], to: string, priority = 1, tabKey = ''): Promise<TranslateResult[]> {
    return Promise.all(items.map((it) => this.translateOne(it, to, priority, tabKey)));
  }

  async translateOne(item: TranslateItem, to: string, priority = 1, tabKey = ''): Promise<TranslateResult> {
    const s = this.o.settings();
    const norm = normalizeText(item.text);
    const base = { id: item.id, from: 'und', confidence: 0 };
    const skip = skipReason(norm);
    if (skip) return { ...base, status: 'skipped', reason: skip };

    let from = item.from && item.from !== 'auto' ? item.from : '';
    let confidence = 1;
    if (!from) {
      const d = await this.detect(item.detectText ? normalizeText(item.detectText) : norm);
      confidence = d?.confidence ?? 0;
      from = d?.lang ?? 'und';
      if (confidence < s.minConfidence) {
        if (item.hint && item.hint !== to && from !== to) { from = item.hint; confidence = s.minConfidence; }
        else return { ...base, from, confidence, status: 'skipped', reason: 'low-confidence' };
      }
    }
    if (from === to) return { id: item.id, from, confidence, status: 'skipped', reason: 'same-language' };

    const choice = await this.choose(from, to);
    if (!choice) return { id: item.id, from, confidence, status: 'skipped', reason: 'unsupported-pair' };
    let { engine } = choice;
    if (choice.state !== 'ready') {
      if (engine.id === 'bergamot' && s.autoDownloadPacks && this.o.onAutoDownload) {
        try { await this.o.onAutoDownload(engine, from, to); this.invalidateAvailability(); }
        catch (e) { return { id: item.id, from, confidence, status: 'error', reason: String((e as Error).message) }; }
      } else {
        return { id: item.id, from, confidence, status: 'needs-pack', pack: { from, to, engine: engine.id as 'chrome' | 'bergamot', sizeBytes: engine.packSize?.(from, to) } };
      }
      engine = choice.engine;
    }

    const cached = await this.o.cache.get(norm, from, to, engine.id);
    if (cached !== undefined) {
      this.stats.cacheHits++;
      return { id: item.id, from, confidence, status: 'ok', translation: cached, engine: engine.id, cached: true };
    }

    const flightKey = `${engine.id}|${from}|${to}|${norm}`;
    const jobKey = `${tabKey}:${item.id}`;
    let f = this.inflight.get(flightKey);
    if (!f) {
      const laneId = `${engine.id}|${from}|${to}`;
      let lane = this.lanes.get(laneId);
      if (!lane) {
        const id = engine.id;
        lane = new Lane(engine, from, to, id === 'chrome' ? 4 : 8, (n) => { this.stats.engineCalls++; this.stats.engineTexts += n; this.stats.lastEngine = id; });
        this.lanes.set(laneId, lane);
      }
      const eng = engine;
      const promise = lane.add(flightKey, norm, priority).then(async (t) => { await this.o.cache.set(norm, from, to, eng.id, t); return t; });
      const flight = { promise, waiters: new Set<string>(), lane, laneKey: flightKey };
      this.inflight.set(flightKey, flight);
      promise.finally(() => { this.inflight.delete(flightKey); for (const w of flight.waiters) if (this.flightOf.get(w) === flightKey) this.flightOf.delete(w); }).catch(() => undefined);
      f = flight;
    } else {
      // Someone more urgent may want the same text: push() only ever raises priority.
      const queued = f.lane.queue.get(f.laneKey);
      if (queued) f.lane.queue.push(f.laneKey, queued, priority);
    }
    f.waiters.add(jobKey);
    this.flightOf.set(jobKey, flightKey);
    try {
      const translation = await f.promise;
      return { id: item.id, from, confidence, status: 'ok', translation, engine: engine.id };
    } catch (e) {
      const msg = (e as Error).message;
      if (msg === 'cancelled') return { id: item.id, from, confidence, status: 'error', reason: 'cancelled' };
      // The engine itself failed: take it out of rotation for this pair and try the next engine once.
      this.markBroken(engine.id, from, to, msg);
      this.invalidateAvailability();
      if (!item.__retried) {
        const retry = await this.translateOne({ ...item, from, __retried: true } as TranslateItem, to, priority, tabKey);
        return { ...retry, reason: retry.status === 'ok' ? undefined : retry.reason ?? `${engine.id}: ${msg}`, failedEngine: engine.id, failedReason: msg };
      }
      return { id: item.id, from, confidence, status: 'error', reason: `${engine.id}: ${msg}` };
    }
  }

  getStats(): EngineStats {
    const workers = this.o.engines.flatMap((e) => e.stats?.() ?? []);
    return { ...this.stats, memoryCacheHits: this.o.cache.memoryHits, workers, failures: this.failures() };
  }
}
