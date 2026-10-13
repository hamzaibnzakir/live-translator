// Discord replying: a small Brainbox pill above the message box + a hotkey. It rewrites the
// English draft into the channel's language, shows what they will read (back-translated), and
// can undo. It never presses Enter / sends anything.
import { searchLanguages, languageLabel, languageName } from '../../shared/languages';
import { send, type TranslateResult } from '../../shared/messages';
import { matchesHotkey, parseHotkey } from '../../shared/hotkey';
import { translateProtected } from '../../shared/protect';
import { pushRecent, saveSettings, type Settings } from '../../shared/settings';
import { escapeHtml, formatBytes, type Overlay } from '../common/overlay';
import { COMPOSER_CONTAINER, findComposer, parseRoute, readDraft } from './discordSelectors';
import { replaceDraft } from './editor';
import type { DiscordReader } from './reader';

interface UndoState { english: string; inserted: string; lang: string }

export class Composer {
  private pill: HTMLElement;
  private preview: HTMLElement;
  private menu: HTMLElement;
  private textbox: HTMLElement | null = null;
  private override: string | null = null;
  private undo: UndoState | null = null;
  private busy = false;
  private raf = 0;
  lastMethod = '';

  constructor(private overlay: Overlay, private reader: DiscordReader, private settings: () => Settings, private onNeedsPack: (r: TranslateResult) => void) {
    this.pill = overlay.add(overlay.el('div', 'panel pill', `<button class="go" data-bb="reply" title="Translate my draft (hotkey)">🧠 <span class="tl"></span></button><button data-bb="lang" title="Choose language">▾</button>`));
    this.pill.style.display = 'none';
    this.preview = overlay.add(overlay.el('div', 'panel preview'));
    this.preview.style.display = 'none';
    this.menu = overlay.add(overlay.el('div', 'panel menu', `<input placeholder="Search language…" aria-label="Search language"><div class="list"></div>`));
    this.menu.style.display = 'none';
    this.pill.querySelector('[data-bb="reply"]')!.addEventListener('click', () => void this.run());
    this.pill.querySelector('[data-bb="lang"]')!.addEventListener('click', () => this.toggleMenu());
    const input = this.menu.querySelector('input')!;
    input.addEventListener('input', () => this.renderMenu(input.value));
    input.addEventListener('keydown', (e) => {
      if (e.key === 'Enter') { const first = this.menu.querySelector<HTMLElement>('.opt'); first?.click(); }
      if (e.key === 'Escape') this.menu.style.display = 'none';
    });
    window.addEventListener('keydown', (e) => this.onKey(e), true);
    window.addEventListener('resize', () => this.position());
    document.addEventListener('scroll', () => this.position(), true);
  }

  channelId(): string { return parseRoute(location.pathname)?.channelId ?? 'unknown'; }

  targetLang(): string | null {
    return this.override ?? this.reader.tracker.main(this.channelId(), this.settings().targetLang);
  }

  /** Called on DOM changes (throttled to one frame) to find the message box and keep the pill attached. */
  refresh(): void {
    if (this.raf) return;
    this.raf = requestAnimationFrame(() => {
      this.raf = 0;
      const tb = findComposer();
      if (tb !== this.textbox) { this.textbox = tb; this.undo = null; this.hidePreview(); }
      this.position();
    });
  }

  private position(): void {
    const tb = this.textbox;
    if (!tb || !tb.isConnected || !this.settings().discord.enabled) { this.pill.style.display = 'none'; return; }
    const box = (tb.closest(COMPOSER_CONTAINER) ?? tb).getBoundingClientRect();
    const lang = this.targetLang();
    (this.pill.querySelector('.tl') as HTMLElement).textContent = this.undo ? 'Undo' : lang ? `→ ${lang.toUpperCase()}` : '→ ?';
    this.pill.style.display = 'flex';
    this.pill.style.top = `${Math.max(4, box.top - 32)}px`;
    this.pill.style.left = `${Math.max(4, box.right - this.pill.offsetWidth - 8)}px`;
    if (this.preview.style.display !== 'none') {
      this.preview.style.left = `${Math.max(4, box.left)}px`;
      this.preview.style.top = `${Math.max(4, box.top - this.preview.offsetHeight - 40)}px`;
    }
    if (this.menu.style.display !== 'none') {
      this.menu.style.top = `${Math.max(4, box.top - 330)}px`;
      this.menu.style.left = `${Math.max(4, box.right - 248)}px`;
    }
  }

  private onKey(e: KeyboardEvent): void {
    const hk = parseHotkey(this.settings().discord.replyHotkey);
    if (matchesHotkey(e, hk)) {
      if (!findComposer()) return;
      e.preventDefault();
      e.stopImmediatePropagation();
      void this.run();
      return;
    }
    // Ctrl+Z right after our rewrite restores the English draft (instead of Slate's step-by-step undo).
    if (this.undo && (e.ctrlKey || e.metaKey) && !e.shiftKey && e.key.toLowerCase() === 'z' && this.textbox && document.activeElement === this.textbox) {
      if (readDraft(this.textbox).trim() === this.undo.inserted.trim()) {
        e.preventDefault();
        e.stopImmediatePropagation();
        void this.restoreEnglish();
      }
    }
  }

  private toggleMenu(): void {
    const open = this.menu.style.display === 'none';
    this.menu.style.display = open ? 'flex' : 'none';
    if (open) {
      this.renderMenu('');
      this.position();
      this.menu.querySelector('input')!.focus();
    }
  }

  private renderMenu(q: string): void {
    const s = this.settings();
    const recent = s.recentLangs.filter((c) => c !== s.targetLang);
    const all = searchLanguages(q).filter((l) => l.code !== s.targetLang);
    const list = q ? all : [...recent.map((c) => all.find((l) => l.code === c)).filter((l): l is NonNullable<typeof l> => !!l), ...all.filter((l) => !recent.includes(l.code))];
    const box = this.menu.querySelector('.list')!;
    box.innerHTML = list.map((l) => `<div class="opt${l.code === this.targetLang() ? ' sel' : ''}" data-code="${l.code}">${l.flag} ${escapeHtml(l.name)} <span class="muted">${escapeHtml(l.native)}</span></div>`).join('');
    box.querySelectorAll<HTMLElement>('.opt').forEach((o) => o.addEventListener('click', () => {
      this.override = o.dataset.code!;
      this.menu.style.display = 'none';
      void saveSettings((st) => ({ ...st, recentLangs: pushRecent(st.recentLangs, this.override!) }));
      this.position();
      this.textbox?.focus();
    }));
  }

  private async translateText(text: string, from: string, to: string): Promise<{ text: string; needs?: TranslateResult }> {
    let needs: TranslateResult | undefined;
    const out = await translateProtected(text, async (parts) => {
      const items = parts.map((t, i) => ({ id: `reply#${i}`, text: t, from }));
      const res = await send<TranslateResult[]>({ type: 'bb:translate', items, to, priority: 0, purpose: 'reply' });
      needs = res.find((r) => r.status === 'needs-pack');
      return res.map((r, i) => (r.status === 'ok' && r.translation !== undefined ? r.translation : parts[i]!));
    });
    return { text: out, needs };
  }

  async run(): Promise<void> {
    const tb = findComposer();
    if (!tb || this.busy) return;
    this.textbox = tb;
    const draft = readDraft(tb);
    if (this.undo && draft.trim() === this.undo.inserted.trim()) { await this.restoreEnglish(); return; }
    if (!draft.trim()) { this.showPreview(`<span class="muted">Type your message in ${escapeHtml(languageName(this.settings().targetLang))} first, then press the Brainbox button.</span>`); return; }
    const lang = this.targetLang();
    if (!lang) { this.toggleMenu(); return; }
    this.busy = true;
    this.showPreview(`<span class="muted">Translating to ${escapeHtml(languageLabel(lang))}…</span>`);
    try {
      const from = this.settings().targetLang;
      const { text, needs } = await this.translateText(draft, from, lang);
      if (needs?.pack) {
        this.onNeedsPack(needs);
        this.showPreview(`<span class="err">Needs the ${escapeHtml(languageName(needs.pack.from))} → ${escapeHtml(languageName(needs.pack.to))} pack${needs.pack.sizeBytes ? ` (${formatBytes(needs.pack.sizeBytes)})` : ''}. Download it from the banner, then press again.</span>`);
        return;
      }
      const method = await replaceDraft(tb, text);
      this.lastMethod = method;
      if (method === 'failed') { this.showPreview('<span class="err">Couldn\'t write into the message box. Your draft is unchanged.</span>'); return; }
      this.undo = { english: draft, inserted: text, lang };
      this.position();
      const parts = [`<div class="lbl">They will read (${escapeHtml(languageLabel(lang))}):</div><div class="they" data-bb="they">${escapeHtml(text)}</div>`];
      if (this.settings().discord.backTranslationPreview) parts.push(`<div class="lbl">Meaning, back in ${escapeHtml(languageName(from))}:</div><div class="back" data-bb="back">…</div>`);
      parts.push(`<div class="row"><button class="ghost" data-bb="undo">Undo (Ctrl+Z)</button><button class="ghost" data-bb="close">Close</button><span class="muted" style="align-self:center">Press Enter yourself to send.</span></div>`);
      this.showPreview(parts.join(''));
      if (this.settings().discord.backTranslationPreview) {
        const back = await this.translateText(text, lang, from);
        const el = this.preview.querySelector('[data-bb="back"]');
        if (el) el.textContent = back.needs ? `(install the ${languageName(lang)} → ${languageName(from)} pack to see this)` : back.text;
      }
    } finally {
      this.busy = false;
    }
  }

  async restoreEnglish(): Promise<void> {
    if (!this.undo || !this.textbox) return;
    const english = this.undo.english;
    this.undo = null;
    await replaceDraft(this.textbox, english);
    this.hidePreview();
    this.position();
  }

  private showPreview(html: string): void {
    this.preview.innerHTML = `<div class="hd" style="display:flex;align-items:center;gap:6px;color:#8A90A2;font-size:11.5px;margin-bottom:4px"><span class="dot"></span>Brainbox reply</div>${html}`;
    this.preview.style.display = 'block';
    this.preview.querySelector('[data-bb="undo"]')?.addEventListener('click', () => void this.restoreEnglish());
    this.preview.querySelector('[data-bb="close"]')?.addEventListener('click', () => this.hidePreview());
    this.position();
  }

  hidePreview(): void { this.preview.style.display = 'none'; }
}
