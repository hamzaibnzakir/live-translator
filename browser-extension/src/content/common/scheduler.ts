// Content-side scheduler: collects translation requests from the page, sends the most urgent
// ones first in small batches, de-duplicates identical texts, caches results for instant
// re-application, and parks requests that wait for a language pack.
import type { PackRef, TranslateItem, TranslateResult } from '../../shared/messages';
import { send } from '../../shared/messages';
import { LRU } from '../../shared/lru';
import { normalizeText } from '../../shared/normalize';
import { PriorityQueue } from '../../shared/priorityQueue';
import { debug } from './debug';

export interface BlockRequest {
  key: string;
  texts: string[];
  /** Text used for language detection of all `texts` (the whole block). */
  detectText?: string;
  from?: string;
  hint?: string;
}

export interface SchedulerOptions {
  to: () => string;
  purpose: 'read' | 'page';
  onResult: (key: string, results: TranslateResult[], req: BlockRequest) => void;
  onNeedsPack?: (pack: PackRef, req: BlockRequest) => void;
  /** The extension was updated/reloaded: this copy of the script can no longer talk to it. */
  onDisconnected?: () => void;
  batchSize?: number;
  maxInflight?: number;
  transport?: (items: TranslateItem[], to: string, priority: number) => Promise<TranslateResult[]>;
}

const defaultTransport = (items: TranslateItem[], to: string, priority: number, purpose: 'read' | 'page'): Promise<TranslateResult[]> =>
  send<TranslateResult[]>({ type: 'bb:translate', items, to, priority, purpose });

export class Scheduler {
  private queue = new PriorityQueue<BlockRequest>();
  private inflight = new Map<string, string[]>();
  private waitingForPack = new Map<string, BlockRequest>();
  private active = 0;
  private scheduled = false;
  /** (to|normalized text) → result; lets re-mounted or repeated text apply synchronously. */
  readonly textCache = new LRU<string, TranslateResult>(5000);
  requestsSent = 0;
  /** Set once the extension context is gone; nothing is sent any more. */
  dead = false;
  private attempts = new Map<string, number>();

  constructor(private o: SchedulerOptions) {}

  cached(text: string): TranslateResult | undefined {
    return this.textCache.get(`${this.o.to()}|${normalizeText(text)}`);
  }

  request(req: BlockRequest, priority: number): void {
    if (this.dead) return;
    debug('request', req.key, priority, this.inflight.has(req.key));
    this.waitingForPack.delete(req.key);
    if (this.inflight.has(req.key)) return;
    this.queue.push(req.key, req, priority);
    this.schedule();
  }

  reprioritize(key: string, priority: number): void { this.queue.reprioritize(key, priority); }

  cancel(key: string): void {
    if (this.queue.has(key) || this.inflight.has(key)) debug('cancel', key);
    this.queue.cancel(key);
    this.waitingForPack.delete(key);
    const ids = this.inflight.get(key);
    if (ids) { this.inflight.delete(key); void send({ type: 'bb:cancel', ids }).catch(() => undefined); }
  }

  isPending(key: string): boolean { return this.queue.has(key) || this.inflight.has(key); }

  /** Re-queues everything that was waiting on a pack (call when a download finishes). */
  retryWaiting(priority = 1): void {
    const all = [...this.waitingForPack.values()];
    this.waitingForPack.clear();
    for (const r of all) this.request(r, priority);
  }

  clearCache(): void { this.textCache.clear(); }

  private schedule(): void {
    if (this.scheduled) return;
    this.scheduled = true;
    // Next task: every observer callback of this frame (visible + nearby) has queued its work,
    // so the most urgent batch is chosen first.
    setTimeout(() => { this.scheduled = false; this.pump(); }, 0);
  }

  private pump(): void {
    const max = this.o.maxInflight ?? 3;
    while (this.active < max && this.queue.size) {
      const batchSize = this.o.batchSize ?? 12;
      const priority = this.queue.peekPriority() ?? 1;
      const reqs: BlockRequest[] = [];
      let n = 0;
      while (this.queue.size && n < batchSize && (this.queue.peekPriority() ?? 9) <= priority) {
        const e = this.queue.pop()!;
        reqs.push(e.value);
        n += e.value.texts.length;
      }
      void this.sendBatch(reqs, priority);
    }
  }

  private async sendBatch(reqs: BlockRequest[], priority: number): Promise<void> {
    this.active++;
    const items: TranslateItem[] = [];
    for (const r of reqs) {
      const ids = r.texts.map((_, i) => `${r.key}#${i}`);
      this.inflight.set(r.key, ids);
      r.texts.forEach((text, i) => items.push({ id: ids[i]!, text, from: r.from, hint: r.hint, detectText: r.detectText }));
    }
    const to = this.o.to();
    try {
      this.requestsSent++;
      debug('send', items.length, reqs.map((r) => r.key).join(','));
      const results = await (this.o.transport ? this.o.transport(items, to, priority) : defaultTransport(items, to, priority, this.o.purpose));
      const byKey = new Map<string, TranslateResult[]>();
      for (const r of results) {
        const key = r.id.slice(0, r.id.lastIndexOf('#'));
        (byKey.get(key) ?? byKey.set(key, []).get(key)!).push(r);
      }
      for (const req of reqs) {
        if (!this.inflight.has(req.key)) continue; // cancelled meanwhile
        this.inflight.delete(req.key);
        const rs = byKey.get(req.key) ?? [];
        debug('result', req.key, rs.map((r) => r.status + ':' + (r.reason ?? r.from)).join(' '));
        rs.sort((a, b) => Number(a.id.slice(a.id.lastIndexOf('#') + 1)) - Number(b.id.slice(b.id.lastIndexOf('#') + 1)));
        const needs = rs.find((r) => r.status === 'needs-pack');
        if (needs?.pack) { this.waitingForPack.set(req.key, req); this.o.onNeedsPack?.(needs.pack, req); continue; }
        // Only answers that depend on the text alone are cached ("too unsure" may change once the
        // channel's language is known; "unsupported" may change when settings change).
        rs.forEach((r, i) => { if (r.status === 'ok' || (r.status === 'skipped' && !['low-confidence', 'unsupported-pair'].includes(r.reason ?? ''))) this.textCache.set(`${to}|${normalizeText(req.texts[i] ?? '')}`, r); });
        this.o.onResult(req.key, rs, req);
      }
    } catch (e) {
      debug('batch failed', e);
      for (const req of reqs) this.inflight.delete(req.key);
      if (isDisconnected(e)) { this.dead = true; this.queue.clear(); this.o.onDisconnected?.(); return; }
      // Transient (service worker restarting, engine busy): try these again shortly.
      for (const req of reqs) {
        const n = (this.attempts.get(req.key) ?? 0) + 1;
        this.attempts.set(req.key, n);
        if (n <= 3) setTimeout(() => { if (!this.dead) this.request(req, priority); }, 3000 * n);
      }
    } finally {
      this.active--;
      if (this.queue.size) this.schedule();
    }
  }
}

/** True when this content script belongs to an extension version that was reloaded or removed. */
export function isDisconnected(e: unknown): boolean {
  // In an extension page/content script `chrome.runtime.id` disappears once it is reloaded.
  if (typeof chrome !== 'undefined' && chrome.runtime && !chrome.runtime.id) return true;
  return /context invalidated|Extension context/i.test(String((e as Error)?.message ?? e));
}
