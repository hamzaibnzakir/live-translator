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
