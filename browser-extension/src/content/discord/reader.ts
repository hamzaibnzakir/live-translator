// Discord reading: rewrites message text in place. Messages are keyed by Discord's stable
// message ids; translations are cached per block so a virtualized row that re-mounts gets its
// translation back synchronously inside the MutationObserver callback (before paint: no flicker).
import { ChannelLanguageTracker } from '../../shared/channelLanguage';
import { languageName } from '../../shared/languages';
import type { PackRef, TranslateResult } from '../../shared/messages';
import { LRU } from '../../shared/lru';
import { Priority } from '../../shared/priorityQueue';
import { blockText, collectSegments, writeSegment, type Segment } from '../../shared/segments';
import type { Settings } from '../../shared/settings';
import { channelMode } from '../../shared/settings';
import { skipReason } from '../../shared/skipRules';
import { FrameWriter } from '../common/overlay';
import { TextRewriter } from '../common/rewriter';
import { Scheduler, type BlockRequest } from '../common/scheduler';
import { findScroller, isProtected, messageBlocks, MESSAGE_ITEM, MESSAGE_CONTENT, parseMessageItemId, parseRoute, REPLY_TEXT, EMBED_TEXT, type Block } from './discordSelectors';

interface Cached { source: string; translations: (string | null)[]; from: string; engine?: string; confidence: number }
interface BlockState { key: string; channelId: string; source: string; showing: 'translated' | 'original' | 'none' }

export interface ReaderHooks {
  onNeedsPack(pack: PackRef): void;
  onAsk(guildId: string, channelId: string, lang: string): void;
  /** A message's language was counted towards the channel's main language. */
  onVote?(channelId: string): void;
  /** An engine failed; the router fell back to another one (or could not). */
  onEngineProblem?(engine: string, reason: string, recovered: boolean): void;
  onDisconnected?(): void;
}

export class DiscordReader {
  readonly rewriter = new TextRewriter();
  readonly tracker = new ChannelLanguageTracker(30, 0.6);
  readonly cache = new LRU<string, Cached>(4000);
  private states = new WeakMap<Element, BlockState>();
  private itemOf = new WeakMap<Element, Element>();
  private writer = new FrameWriter();
  private observer: MutationObserver | null = null;
  /** One pair of observers per scroll container (channel list, thread sidebar…). */
  private observers = new Map<Element | null, { near: IntersectionObserver; visible: IntersectionObserver }>();
  private visibleItems = new Set<Element>();
  private nearItems = new Set<Element>();
  /** Channels the user allowed "just now" while in ask mode. */
  readonly sessionAllowed = new Set<string>();
  readonly scheduler: Scheduler;
  appliedSync = 0;
  private stopped = false;

  constructor(private settings: () => Settings, private hooks: ReaderHooks) {
    this.scheduler = new Scheduler({
      to: () => this.settings().targetLang,
      purpose: 'read',
      // One message per request: a slow pair (e.g. a Japanese base model) never delays others.
      batchSize: 1,
      maxInflight: 6,
      onResult: (key, results, req) => this.onResult(key, results, req),
      onNeedsPack: (pack) => this.hooks.onNeedsPack(pack),
      onDisconnected: () => this.hooks.onDisconnected?.(),
    });
  }

  private observersFor(li: Element): { near: IntersectionObserver; visible: IntersectionObserver } {
    const root = findScroller(li);
    let o = this.observers.get(root);
    if (!o) {
      // Visible first; "nearby" = one screen above and below inside the scroller; the rest of
      // the history only when it scrolls into that range.
      const visible = new IntersectionObserver((entries) => {
        for (const e of entries) {
          if (e.isIntersecting) { this.visibleItems.add(e.target); this.nearItems.add(e.target); this.processItem(e.target, Priority.visible); }
          else this.visibleItems.delete(e.target);
        }
      }, { root });
      const near = new IntersectionObserver((entries) => {
        for (const e of entries) {
          if (e.isIntersecting) { this.nearItems.add(e.target); if (!this.visibleItems.has(e.target)) this.processItem(e.target, Priority.nearby); }
          else { this.nearItems.delete(e.target); this.cancelItem(e.target); }
        }
      }, { root, rootMargin: '100% 0px 100% 0px' });
      o = { near, visible };
      this.observers.set(root, o);
    }
    return o;
  }

  start(): void {
    this.observer = new MutationObserver((records) => this.onMutations(records));
    this.observer.observe(document.body, { childList: true, subtree: true, characterData: true });
    document.querySelectorAll(MESSAGE_ITEM).forEach((li) => this.mountItem(li));
  }

  stop(): void {
    this.stopped = true;
    this.scheduler.dead = true;
    this.observer?.disconnect();
    this.observer = null;
    for (const o of this.observers.values()) { o.near.disconnect(); o.visible.disconnect(); }
    this.observers.clear();
  }

  /** Puts every message back to the original text (extension turned off for this channel). */
  restoreAll(): void {
    this.rewriter.restoreAll();
    document.querySelectorAll('[data-bb-translated]').forEach((el) => { el.removeAttribute('data-bb-translated'); el.removeAttribute('data-bb-state'); });
  }

  rescan(): void {
    document.querySelectorAll(MESSAGE_ITEM).forEach((li) => {
      if (this.nearItems.has(li)) this.processItem(li, this.visibleItems.has(li) ? Priority.visible : Priority.nearby);
    });
  }

  private route(): { guildId: string; channelId: string } {
    return parseRoute(location.pathname) ?? { guildId: '@me', channelId: 'unknown' };
  }

  private allowed(channelId: string): 'yes' | 'no' | 'ask' {
    const s = this.settings();
    if (!s.discord.enabled) return 'no';
    const { guildId } = this.route();
    const mode = channelMode(s, guildId, channelId);
    if (mode === 'always' || this.sessionAllowed.has(channelId)) return 'yes';
    return mode === 'never' ? 'no' : 'ask';
  }

  private onMutations(records: MutationRecord[]): void {
    const dirty = new Set<Element>();
    for (const r of records) {
      if (r.type === 'characterData') {
        const t = r.target as Text;
        if (this.rewriter.isOurs(t)) continue;
        const item = t.parentElement?.closest(MESSAGE_ITEM);
        if (item) dirty.add(item);
        continue;
      }
      for (const n of r.addedNodes) {
        if (n.nodeType === 3) { const item = (n.parentElement ?? (r.target as Element)).closest?.(MESSAGE_ITEM); if (item) dirty.add(item); continue; }
        if (n.nodeType !== 1) continue;
        const el = n as Element;
        if (el.id === 'brainbox-overlay') continue;
        if (el.matches(MESSAGE_ITEM)) { this.mountItem(el); continue; }
        const inner = el.querySelectorAll(MESSAGE_ITEM);
        if (inner.length) { inner.forEach((li) => this.mountItem(li)); continue; }
        const item = (r.target as Element).closest?.(MESSAGE_ITEM);
        if (item) dirty.add(item);
      }
      if (r.removedNodes.length && r.target.nodeType === 1) {
        const item = (r.target as Element).closest(MESSAGE_ITEM);
        if (item) dirty.add(item);
      }
    }
    for (const item of dirty) {
      if (!item.isConnected) continue;
      this.applyCachedSync(item);
      if (this.nearItems.has(item)) this.processItem(item, this.visibleItems.has(item) ? Priority.visible : Priority.nearby);
    }
  }

  private mountItem(li: Element): void {
    const o = this.observersFor(li);
    o.visible.observe(li);
    o.near.observe(li);
    this.applyCachedSync(li);
  }

  private cancelItem(li: Element): void {
    for (const b of messageBlocks(li)) this.scheduler.cancel(b.key);
  }

  private segmentsOf(el: Element): Segment[] {
    return collectSegments(el, { skip: isProtected, originalOf: (n) => this.rewriter.originalOf(n) });
  }

  /** Re-applies cached translations synchronously (re-mount after scrolling, React re-render). */
  private applyCachedSync(li: Element): void {
    if (this.stopped) return;
    const ids = parseMessageItemId(li.id);
    if (!ids || this.allowed(ids.channelId) !== 'yes') return;
    for (const b of messageBlocks(li)) {
      const st = this.states.get(b.el);
      if (st?.showing === 'original') continue;
      const segs = this.segmentsOf(b.el);
      const source = blockText(segs);
      const c = this.cache.peek(b.key);
      if (c && c.source === source && segs.length === c.translations.length) {
        if (this.isApplied(segs, c)) continue;
        this.write(b, segs, c, ids.channelId);
        this.appliedSync++;
      }
    }
  }

  private isApplied(segs: Segment[], c: Cached): boolean {
    return segs.every((s, i) => c.translations[i] == null || s.nodes.every((n) => this.rewriter.isOurs(n)));
  }

  processItem(li: Element, priority: number): void {
    if (this.stopped) return;
    const ids = parseMessageItemId(li.id);
    if (!ids) return;
    const allowed = this.allowed(ids.channelId);
    if (allowed === 'no') return;
    for (const b of messageBlocks(li)) {
      this.itemOf.set(b.el, li);
      this.processBlock(b, ids.channelId, priority, allowed === 'ask');
    }
  }

  private processBlock(b: Block, channelId: string, priority: number, askOnly: boolean): void {
    const segs = this.segmentsOf(b.el);
    const source = blockText(segs);
    if (!source.trim()) return;
    const st = this.states.get(b.el);
    if (st?.showing === 'original' && st.source === source) return;
    const cached = this.cache.peek(b.key);
    if (cached && cached.source === source) {
      if (!askOnly && !this.isApplied(segs, cached)) this.write(b, segs, cached, channelId);
      return;
    }
    if (st && st.source === source && this.scheduler.isPending(b.key)) { this.scheduler.reprioritize(b.key, priority); return; }
    this.states.set(b.el, { key: b.key, channelId, source, showing: st?.showing === 'original' ? 'original' : 'none' });

    // Segment texts to translate; segments that are pure noise are left alone.
    const texts = segs.map((s) => s.text.trim());
    const hint = this.tracker.main(channelId, this.settings().targetLang) ?? undefined;
    const req: BlockRequest = { key: b.key, texts, detectText: source, hint };
    if (askOnly) req.key = `ask:${b.key}`;

    // Everything already known? Apply now without a round-trip.
    const known = texts.map((t) => (skipReason(t) ? null : this.scheduler.cached(t)));
    if (!askOnly && known.every((k, i) => k !== undefined || skipReason(texts[i]!))) {
      this.onResult(b.key, known.map((k, i) => k ?? { id: `${b.key}#${i}`, status: 'skipped', from: 'und', confidence: 0 }), req);
      return;
    }
    this.scheduler.request(req, priority);
  }

  private onResult(key: string, results: TranslateResult[], req: BlockRequest): void {
    const failed = results.find((r) => r.failedEngine);
    if (failed) this.hooks.onEngineProblem?.(failed.failedEngine!, failed.failedReason ?? '', failed.status === 'ok' || failed.status === 'needs-pack');
    const hardError = results.find((r) => r.status === 'error' && r.reason !== 'cancelled');
    if (hardError && !failed) this.hooks.onEngineProblem?.(hardError.engine ?? 'engine', hardError.reason ?? 'unknown error', false);
    const ask = key.startsWith('ask:');
    const blockKey = ask ? key.slice(4) : key;
    const first = results.find((r) => r.from && r.from !== 'und') ?? results[0];
    const from = first?.from ?? 'und';
    const confidence = first?.confidence ?? 0;
    const messageId = blockKey.split(':')[0]!;
    const li = document.querySelector(`li[id$="-${messageId}"]`);
    const ids = li ? parseMessageItemId(li.id) : null;
    const channelId = ids?.channelId ?? this.route().channelId;
    if (blockKey.endsWith(':content') && from !== 'und') { this.tracker.add(channelId, from, confidence); this.hooks.onVote?.(channelId); }

    if (ask) {
      if (from !== 'und' && from !== this.settings().targetLang && results.some((r) => r.status === 'ok' || r.status === 'skipped' && r.reason !== 'same-language')) {
        const { guildId } = this.route();
        this.hooks.onAsk(guildId, channelId, from);
      }
      return;
    }
    // Errors/cancellations are not an answer: leave the block untouched so it is retried later.
    if (results.some((r) => r.status === 'error' || r.status === 'needs-pack')) {
      const el = li ? messageBlocks(li).find((b) => b.key === blockKey)?.el : undefined;
      const st = el ? this.states.get(el) : undefined;
      if (st) st.source = '';
      return;
    }
    const translations = results.map((r) => (r.status === 'ok' && r.translation !== undefined ? r.translation : null));
    const entry: Cached = { source: req.detectText ?? req.texts.join('\n'), translations, from, engine: first?.engine, confidence };
    if (translations.every((t) => t === null)) {
      // Nothing to change (already English, noise…). Remember so we don't ask again.
      this.cache.set(blockKey, entry);
      return;
    }
    this.cache.set(blockKey, entry);
    if (!li) return;
    this.writer.run(() => {
      if (this.stopped) return;
      for (const b of messageBlocks(li)) {
        if (b.key !== blockKey) continue;
        const segs = this.segmentsOf(b.el);
        if (blockText(segs) !== entry.source || segs.length !== translations.length) { this.processItem(li, Priority.visible); return; }
        const st = this.states.get(b.el);
        if (st?.showing === 'original') return;
        this.write(b, segs, entry, channelId, true);
      }
    });
  }

  private write(b: Block, segs: Segment[], c: Cached, channelId: string, fresh = false): void {
    segs.forEach((seg, i) => {
      const t = c.translations[i];
      if (t != null) writeSegment(seg, t, (n, v) => this.rewriter.write(n, v));
    });
    b.el.setAttribute('data-bb-translated', '1');
    b.el.setAttribute('data-bb-original', c.source);
    b.el.setAttribute('data-bb-lang', c.from);
    if (c.engine) b.el.setAttribute('data-bb-engine', c.engine);
    b.el.removeAttribute('data-bb-state');
    if (fresh) { b.el.setAttribute('data-bb-fresh', ''); setTimeout(() => b.el.removeAttribute('data-bb-fresh'), 400); }
    this.states.set(b.el, { key: b.key, channelId, source: c.source, showing: 'translated' });
  }

  /** Alt+click: swap one block between original and translation. */
  toggle(el: Element): 'original' | 'translated' | null {
    const block = this.blockFor(el);
    if (!block) return null;
    const st = this.states.get(block.el);
    const c = this.cache.peek(block.key);
    if (!c) return null;
    if (st?.showing !== 'original') {
      const walker = document.createTreeWalker(block.el, NodeFilter.SHOW_TEXT);
      for (let n = walker.nextNode(); n; n = walker.nextNode()) this.rewriter.restore(n as Text);
      block.el.setAttribute('data-bb-state', 'original');
      this.states.set(block.el, { key: block.key, channelId: st?.channelId ?? '', source: c.source, showing: 'original' });
      return 'original';
    }
    const segs = this.segmentsOf(block.el);
    this.write(block, segs, c, st.channelId);
    return 'translated';
  }

  blockFor(el: Element): Block | null {
    const target = el.closest('[data-bb-translated]');
    const li = target?.closest(MESSAGE_ITEM);
    if (!target || !li) return null;
    return messageBlocks(li).find((b) => b.el === target) ?? null;
  }

  info(el: Element): { original: string; lang: string; engine: string; key: string } | null {
    const b = this.blockFor(el);
    if (!b) return null;
    return { original: b.el.getAttribute('data-bb-original') ?? '', lang: languageName(b.el.getAttribute('data-bb-lang') ?? ''), engine: b.el.getAttribute('data-bb-engine') ?? '', key: b.key };
  }

  /** Replace a single-segment block with a better translation (Polish with AI). */
  replaceTranslation(el: Element, text: string): boolean {
    const b = this.blockFor(el);
    const c = b ? this.cache.peek(b.key) : undefined;
    if (!b || !c) return false;
    const idx = c.translations.findIndex((t) => t != null);
    const segs = this.segmentsOf(b.el);
    if (idx < 0 || c.translations.filter((t) => t != null).length !== 1) return false;
    const next: Cached = { ...c, translations: c.translations.map((t, i) => (i === idx ? text : t)), engine: 'ai' };
    this.cache.set(b.key, next);
    this.write(b, segs, next, this.states.get(b.el)?.channelId ?? '');
    return true;
  }
}

export const TRANSLATABLE_SELECTOR = `${MESSAGE_CONTENT}, ${REPLY_TEXT}, ${EMBED_TEXT}`;
