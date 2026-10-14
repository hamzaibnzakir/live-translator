// A single shadow-DOM host appended to <html> (outside any app root), holding every piece of
// Brainbox UI shown inside pages: tooltip, banners, reply button, preview, language menu.
import { OVERLAY_CSS, PAGE_CSS } from './theme';

export class Overlay {
  readonly host: HTMLElement;
  readonly root: ShadowRoot;
  constructor() {
    const existing = document.getElementById('brainbox-overlay');
    existing?.remove();
    this.host = document.createElement('div');
    this.host.id = 'brainbox-overlay';
    this.host.setAttribute('data-bb-skip', '');
    this.host.setAttribute('translate', 'no');
    this.root = this.host.attachShadow({ mode: 'open' });
    const style = document.createElement('style');
    style.textContent = OVERLAY_CSS;
    this.root.appendChild(style);
    document.documentElement.appendChild(this.host);
    if (!document.getElementById('brainbox-page-css')) {
      const ps = document.createElement('style');
      ps.id = 'brainbox-page-css';
      ps.textContent = PAGE_CSS;
      (document.head ?? document.documentElement).appendChild(ps);
    }
  }

  el<K extends keyof HTMLElementTagNameMap>(tag: K, cls = '', html = ''): HTMLElementTagNameMap[K] {
    const e = document.createElement(tag);
    if (cls) e.className = cls;
    if (html) e.innerHTML = html;
    return e;
  }

  add<T extends HTMLElement>(e: T): T {
    e.classList.add('bb');
    this.root.appendChild(e);
    return e;
  }
}

export function escapeHtml(s: string): string {
  return s.replace(/[&<>"']/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' }[c]!));
}

export function formatBytes(n: number): string {
  if (n >= 1e9) return `${(n / 1e9).toFixed(1)} GB`;
  if (n >= 1e6) return `${Math.round(n / 1e6)} MB`;
  if (n >= 1e3) return `${Math.round(n / 1e3)} KB`;
  return `${n} B`;
}

/** Batches DOM writes into one requestAnimationFrame. */
export class FrameWriter {
  private jobs: (() => void)[] = [];
  private pending = false;
  run(job: () => void): void {
    this.jobs.push(job);
    if (this.pending) return;
    this.pending = true;
    requestAnimationFrame(() => {
      this.pending = false;
      const jobs = this.jobs;
      this.jobs = [];
      for (const j of jobs) { try { j(); } catch (e) { console.warn('[Brainbox]', e); } }
    });
  }
}

export interface NoticeAction { label: string; primary?: boolean; icon?: string; onClick: () => void }
export interface NoticeSpec { kind?: 'info' | 'warn'; title: string; body?: string; actions?: NoticeAction[]; progress?: number | 'busy' | null; logo?: string }
export interface NoticeHandle { update(spec: Partial<NoticeSpec>): void; close(): void; readonly open: boolean }

/** Top-right stack of dismissible Brainbox notices (pack downloads, ask mode, engine problems). */
export class Notices {
  private stack: HTMLElement;
  private live = new Map<string, NoticeHandle>();
  constructor(private overlay: Overlay, private logo: string, private closeIcon: string) {
    this.stack = overlay.add(overlay.el('div', 'stack'));
  }
  get(id: string): NoticeHandle | undefined { return this.live.get(id); }
  show(id: string, spec: NoticeSpec): NoticeHandle {
    this.live.get(id)?.close();
    const el = this.overlay.el('div', `panel notice ${spec.kind === 'warn' ? 'warn' : ''}`);
    el.setAttribute('role', 'status');
    let current: NoticeSpec = spec;
    let open = true;
    const render = (): void => {
      const p = current.progress;
      el.innerHTML = `<div class="hd">${this.logo}<span>Brainbox Auto Translator</span><span class="sp"></span><button class="x" data-x aria-label="Dismiss">${this.closeIcon}</button></div>
        <div class="t"></div>${current.body ? '<div class="b"></div>' : ''}
        ${p === null || p === undefined ? '' : `<div class="bar ${p === 'busy' ? 'busy' : ''}"><i style="width:${p === 'busy' ? '' : `${Math.round(p * 100)}%`}"></i></div>`}
        ${current.actions?.length ? '<div class="row"></div>' : ''}`;
      (el.querySelector('.t') as HTMLElement).textContent = current.title;
      if (current.body) (el.querySelector('.b') as HTMLElement).textContent = current.body;
      const row = el.querySelector('.row');
      for (const a of current.actions ?? []) {
        const b = document.createElement('button');
        if (!a.primary) b.className = 'ghost';
        b.innerHTML = a.icon ?? '';
        b.append(a.label);
        b.addEventListener('click', a.onClick);
        row!.appendChild(b);
      }
      el.querySelector('[data-x]')!.addEventListener('click', () => handle.close());
    };
    const handle: NoticeHandle = {
      update: (s) => { current = { ...current, ...s }; if (open) render(); },
      close: () => { if (!open) return; open = false; el.remove(); if (this.live.get(id) === handle) this.live.delete(id); },
      get open() { return open; },
    };
    render();
    this.stack.appendChild(el);
    this.live.set(id, handle);
    return handle;
  }
}
