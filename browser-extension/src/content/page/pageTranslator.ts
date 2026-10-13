// Google-Translate-style page translation: walks text nodes (never code, inputs, editors or
// [translate=no]), groups them by block so a sentence keeps its context, translates what is on
// screen first, follows content that loads later, and can restore the original exactly.
import { LRU } from '../../shared/lru';
import type { PackRef, TranslateResult } from '../../shared/messages';
import { Priority } from '../../shared/priorityQueue';
import { collectSegments, writeSegment, INLINE_TAGS, type Segment } from '../../shared/segments';
import { skipReason } from '../../shared/skipRules';
import { FrameWriter } from '../common/overlay';
import { TextRewriter } from '../common/rewriter';
import { Scheduler } from '../common/scheduler';

const SKIP_TAGS = new Set(['SCRIPT', 'STYLE', 'NOSCRIPT', 'TEMPLATE', 'CODE', 'PRE', 'KBD', 'SAMP', 'VAR', 'TEXTAREA', 'INPUT', 'SELECT', 'OPTION', 'SVG', 'MATH', 'CANVAS', 'IFRAME', 'OBJECT', 'VIDEO', 'AUDIO']);
const BOUNDARY_TAGS = new Set(['A', 'BUTTON', 'LABEL', 'SUMMARY', 'LEGEND', 'CAPTION', 'TH', 'TD', 'LI', 'DT', 'DD']);

export function isSkippedElement(el: Element): boolean {
  if (SKIP_TAGS.has(el.tagName.toUpperCase())) return true;
  if (el.id === 'brainbox-overlay' || el.id === 'brainbox-selection-card' || el.hasAttribute('data-bb-skip')) return true;
  if (el.getAttribute('translate') === 'no' || el.classList.contains('notranslate')) return true;
  const ce = el.getAttribute('contenteditable');
  if (ce !== null && ce !== 'false') return true;
  if (el.getAttribute('aria-hidden') === 'true' && el.tagName !== 'BODY') return true;
  return false;
}

function blockOf(node: Node): Element {
  let el = node.parentElement;
  while (el && INLINE_TAGS.has(el.tagName) && el.parentElement) el = el.parentElement;
  return el ?? document.body;
}

interface Seg { seg: Segment; key: string; anchor: Element }

export interface PageTranslatorOptions {
  to: () => string;
  onNeedsPack?: (p: PackRef) => void;
  onProgress?: (translated: number, pending: number) => void;
}

export class PageTranslator {
  readonly rewriter = new TextRewriter();
  readonly scheduler: Scheduler;
  private segByKey = new Map<string, Seg>();
  private keyOfNode = new WeakMap<Text, string>();
  private anchors = new WeakMap<Element, Set<string>>();
  private results = new LRU<string, string>(20000);
  private io: IntersectionObserver | null = null;
  private mo: MutationObserver | null = null;
  private visible = new Set<Element>();
  private writer = new FrameWriter();
  private seq = 0;
  private originalTitle: string | null = null;
  private pageLang: string | undefined;
  active = false;
  translatedCount = 0;

  constructor(private o: PageTranslatorOptions) {
    this.scheduler = new Scheduler({
      to: o.to,
      purpose: 'page',
      batchSize: 16,
      onResult: (key, rs) => this.onResult(key, rs),
      onNeedsPack: (p) => o.onNeedsPack?.(p),
    });
  }

  start(root: Element = document.body): void {
    if (this.active) return;
    this.active = true;
    const lang = document.documentElement.lang;
    this.pageLang = lang && !lang.toLowerCase().startsWith(this.o.to()) ? lang.split('-')[0] : undefined;
    this.io = new IntersectionObserver((entries) => {
      for (const e of entries) {
        if (e.isIntersecting) { this.visible.add(e.target); this.queueAnchor(e.target, Priority.visible); }
        else { this.visible.delete(e.target); this.queueAnchor(e.target, Priority.history); }
      }
    }, { rootMargin: '50% 0px 50% 0px' });
    this.collect(root);
    this.mo = new MutationObserver((records) => this.onMutations(records));
    this.mo.observe(document.body, { childList: true, subtree: true, characterData: true });
    void this.translateTitle();
  }

  stop(): number {
    this.active = false;
    this.io?.disconnect();
    this.mo?.disconnect();
    this.io = this.mo = null;
    for (const k of this.segByKey.keys()) this.scheduler.cancel(k);
    const n = this.rewriter.restoreAll();
    document.querySelectorAll('[data-bb-translated]').forEach((el) => el.removeAttribute('data-bb-translated'));
    if (this.originalTitle !== null) { document.title = this.originalTitle; this.originalTitle = null; }
    this.segByKey.clear();
    this.translatedCount = 0;
    return n;
  }

  private async translateTitle(): Promise<void> {
    const t = document.title;
    if (!t || skipReason(t)) return;
    this.originalTitle = t;
    const key = `title:${this.seq++}`;
    this.scheduler.request({ key, texts: [t], hint: this.pageLang }, Priority.visible);
  }

  private collect(root: Element): void {
    if (isSkippedElement(root) || root.closest('[translate="no"], .notranslate, #brainbox-overlay')) return;
    const segs = collectSegments(root, {
      skip: isSkippedElement,
      boundary: (el) => BOUNDARY_TAGS.has(el.tagName),
      originalOf: (n) => this.rewriter.originalOf(n),
    });
    for (const seg of segs) {
      if (skipReason(seg.text)) continue;
      const first = seg.nodes[0]!;
      const existing = this.keyOfNode.get(first);
      if (existing && this.segByKey.get(existing)?.seg.text === seg.text) continue;
      const key = `p${this.seq++}`;
      const anchor = blockOf(first);
      this.segByKey.set(key, { seg, key, anchor });
      seg.nodes.forEach((n) => this.keyOfNode.set(n, key));
      let set = this.anchors.get(anchor);
      if (!set) { set = new Set(); this.anchors.set(anchor, set); this.io?.observe(anchor); }
      set.add(key);
      const cached = this.results.get(seg.text.trim());
      if (cached !== undefined) this.write(key, cached);
      else if (this.visible.has(anchor)) this.request(key, Priority.visible);
    }
  }

  private queueAnchor(anchor: Element, priority: number): void {
    for (const key of this.anchors.get(anchor) ?? []) {
      const s = this.segByKey.get(key);
      if (!s || s.seg.nodes.every((n) => this.rewriter.isOurs(n))) continue;
      if (this.scheduler.isPending(key)) this.scheduler.reprioritize(key, priority);
      else if (priority !== Priority.history) this.request(key, priority);
    }
  }

  private request(key: string, priority: number): void {
    const s = this.segByKey.get(key);
    if (!s) return;
    const text = s.seg.text.trim();
    const block = s.anchor.textContent ?? text;
    this.scheduler.request({ key, texts: [text], detectText: text.length < 40 && block.length > text.length ? block.slice(0, 400) : undefined, hint: this.pageLang }, priority);
  }

  private onResult(key: string, rs: TranslateResult[]): void {
    const r = rs[0];
    if (key.startsWith('title:')) {
      if (r?.status === 'ok' && r.translation && this.active) document.title = r.translation;
      return;
    }
    const s = this.segByKey.get(key);
    if (!r || !s) return;
    if (r.status !== 'ok' || r.translation === undefined) return;
    this.results.set(s.seg.text.trim(), r.translation);
    this.writer.run(() => this.write(key, r.translation!));
  }

  private write(key: string, translation: string): void {
    const s = this.segByKey.get(key);
    if (!s || !this.active) return;
    // Page changed underneath us? Re-collect that block instead of writing stale text.
    const current = s.seg.nodes.map((n) => this.rewriter.originalOf(n) ?? n.data).join('');
    if (s.seg.nodes.some((n) => !n.isConnected) || current !== s.seg.text) { this.segByKey.delete(key); return; }
    writeSegment(s.seg, translation, (n, v) => this.rewriter.write(n, v));
    s.anchor.setAttribute('data-bb-translated', '1');
    this.translatedCount++;
    this.o.onProgress?.(this.translatedCount, this.scheduler.isPending(key) ? 1 : 0);
  }

  private pendingRoots = new Set<Element>();
  private flushTimer = 0;
  private onMutations(records: MutationRecord[]): void {
    for (const r of records) {
      if (r.type === 'characterData') {
        const t = r.target as Text;
        if (this.rewriter.isOurs(t) || !t.parentElement) continue;
        this.pendingRoots.add(blockOf(t));
        continue;
      }
      for (const n of r.addedNodes) {
        if (n.nodeType === 1) this.pendingRoots.add(n as Element);
        else if (n.nodeType === 3 && n.parentElement) this.pendingRoots.add(blockOf(n));
      }
    }
    if (this.pendingRoots.size && !this.flushTimer) {
      // Content that loads later (infinite scroll, SPA navigation) is picked up in one pass.
      this.flushTimer = window.setTimeout(() => {
        this.flushTimer = 0;
        const roots = [...this.pendingRoots];
        this.pendingRoots.clear();
        for (const el of roots) if (el.isConnected && !roots.some((o) => o !== el && o.contains(el))) this.collect(el);
      }, 120);
    }
  }
}
