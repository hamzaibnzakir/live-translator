// Brainbox Translate for discord.com. Reads (in-place rewrite) and helps replying.
// Never uses Discord's API or your token, never sends anything: it only edits text on screen.
import { languageName } from '../../shared/languages';
import type { PackProgressEvent, PackRef, TabMessage, TranslateResult } from '../../shared/messages';
import { send } from '../../shared/messages';
import { DEFAULT_SETTINGS, loadSettings, onSettingsChanged, saveSettings, type Settings } from '../../shared/settings';
import { escapeHtml, formatBytes, Overlay } from '../common/overlay';
import { Composer } from './composer';
import { DiscordReader } from './reader';

declare global { interface Window { __brainboxDiscord?: unknown } }

async function main(): Promise<void> {
  if (window.__brainboxDiscord) return;
  let settings: Settings = DEFAULT_SETTINGS;
  settings = await loadSettings();
  const overlay = new Overlay();
  const get = (): Settings => settings;

  // ---------- banners (pack download, ask mode) ----------
  const banner = overlay.add(overlay.el('div', 'panel banner'));
  banner.style.display = 'none';
  let bannerPack: PackRef | null = null;
  const dismissedPacks = new Set<string>();
  const askedChannels = new Set<string>();

  function showPackBanner(pack: PackRef): void {
    const key = `${pack.from}-${pack.to}`;
    if (dismissedPacks.has(key) || (bannerPack && `${bannerPack.from}-${bannerPack.to}` === key && banner.style.display !== 'none')) return;
    bannerPack = pack;
    const size = pack.sizeBytes ? ` (${formatBytes(pack.sizeBytes)})` : '';
    const engine = pack.engine === 'chrome' ? 'Chrome built-in' : 'offline';
    banner.innerHTML = `<div class="hd"><span class="dot"></span>Brainbox Translate</div>
      <div>${escapeHtml(languageName(pack.from))} messages here. Download the ${escapeHtml(languageName(pack.from))} → ${escapeHtml(languageName(pack.to))} ${engine} pack${size}? It stays on your computer and works offline.</div>
      <div class="bar" style="display:none"><i></i></div>
      <div class="row"><button data-bb="dl">Download ${escapeHtml(languageName(pack.from))} → ${escapeHtml(languageName(pack.to))}</button><button class="ghost" data-bb="later">Not now</button></div>`;
    banner.style.display = 'block';
    banner.querySelector('[data-bb="later"]')!.addEventListener('click', () => { dismissedPacks.add(key); banner.style.display = 'none'; });
    banner.querySelector('[data-bb="dl"]')!.addEventListener('click', () => {
      if (pack.engine === 'chrome') { void send({ type: 'bb:open-packs', from: pack.from, to: pack.to }); return; }
      (banner.querySelector('.bar') as HTMLElement).style.display = 'block';
      (banner.querySelector('[data-bb="dl"]') as HTMLButtonElement).disabled = true;
      void send({ type: 'bb:packs:download', from: pack.from, to: pack.to }).catch((e: Error) => {
        banner.querySelector('.row')!.insertAdjacentHTML('beforebegin', `<div class="err">${escapeHtml(e.message)}</div>`);
      });
    });
  }

  function onPackProgress(e: PackProgressEvent): void {
    if (!bannerPack || e.from !== bannerPack.from || e.to !== bannerPack.to) { if (e.done && !e.error) reader.scheduler.retryWaiting(); return; }
    const bar = banner.querySelector<HTMLElement>('.bar');
    if (bar) { bar.style.display = 'block'; (bar.firstElementChild as HTMLElement).style.width = `${e.total ? Math.round((e.loaded / e.total) * 100) : 0}%`; }
    if (e.done) {
      if (e.error) { banner.querySelector('.row')!.insertAdjacentHTML('beforebegin', `<div class="err">${escapeHtml(e.error)}</div>`); return; }
      banner.style.display = 'none';
      bannerPack = null;
      reader.scheduler.retryWaiting();
      reader.rescan();
    }
  }

  function showAsk(guildId: string, channelId: string, lang: string): void {
    if (askedChannels.has(channelId)) return;
    askedChannels.add(channelId);
    banner.innerHTML = `<div class="hd"><span class="dot"></span>Brainbox Translate</div>
      <div>This channel is mostly in ${escapeHtml(languageName(lang))}. Translate it to ${escapeHtml(languageName(settings.targetLang))}?</div>
      <div class="row"><button data-m="channel">Always here</button><button data-m="server">Whole server</button><button class="ghost" data-m="once">Just now</button><button class="ghost" data-m="never">Never</button></div>`;
    banner.style.display = 'block';
    banner.querySelectorAll<HTMLElement>('[data-m]').forEach((b) => b.addEventListener('click', () => {
      const m = b.dataset.m;
      banner.style.display = 'none';
      if (m === 'once') reader.sessionAllowed.add(channelId);
      else void saveSettings((s) => {
        const d = { ...s.discord, channels: { ...s.discord.channels }, servers: { ...s.discord.servers } };
        if (m === 'channel') d.channels[`${guildId}/${channelId}`] = 'always';
        if (m === 'server') d.servers[guildId] = 'always';
        if (m === 'never') d.channels[`${guildId}/${channelId}`] = 'never';
        return { ...s, discord: d };
      });
      if (m !== 'never') { reader.sessionAllowed.add(channelId); reader.rescan(); }
    }));
  }

  const reader = new DiscordReader(get, { onNeedsPack: showPackBanner, onAsk: showAsk, onVote: () => composer.refresh() });
  const composer = new Composer(overlay, reader, get, (r: TranslateResult) => r.pack && showPackBanner(r.pack));
  window.__brainboxDiscord = { reader, composer, settings: get };

  // ---------- hover tooltip ----------
  const tip = overlay.add(overlay.el('div', 'panel tip'));
  tip.style.display = 'none';
  let tipTarget: Element | null = null;
  let tipTimer = 0;
  const hideTip = (): void => { tip.style.display = 'none'; tipTarget = null; };
  function showTip(target: Element): void {
    const info = reader.info(target);
    if (!info) return;
    tipTarget = target;
    const engine = info.engine === 'bergamot' ? 'offline (Bergamot)' : info.engine === 'chrome' ? 'Chrome built-in' : info.engine === 'ai' ? 'AI polished' : info.engine;
    tip.innerHTML = `<div class="hd"><span class="dot"></span>Original · ${escapeHtml(info.lang)}${engine ? ` · ${escapeHtml(engine)}` : ''}</div>
      <div class="orig" data-bb="orig"></div>
      <div class="row"><button class="ghost" data-bb="swap">Show original <span class="muted">(Alt+click)</span></button>${settings.polish.enabled ? '<button data-bb="polish">✨ Polish with AI</button>' : ''}</div>`;
    (tip.querySelector('[data-bb="orig"]') as HTMLElement).textContent = info.original;
    tip.style.display = 'block';
    const r = target.getBoundingClientRect();
    const h = tip.offsetHeight;
    tip.style.left = `${Math.max(8, Math.min(window.innerWidth - tip.offsetWidth - 8, r.left))}px`;
    tip.style.top = `${r.top - h - 8 > 8 ? r.top - h - 8 : r.bottom + 8}px`;
    tip.querySelector('[data-bb="swap"]')!.addEventListener('click', () => { reader.toggle(target); hideTip(); });
    tip.querySelector('[data-bb="polish"]')?.addEventListener('click', () => {
      const btn = tip.querySelector('[data-bb="polish"]') as HTMLButtonElement;
      btn.textContent = 'Polishing…';
      const block = reader.blockFor(target);
      const current = block?.el.textContent ?? '';
      void send<{ text: string; model: string }>({ type: 'bb:polish', text: current, original: info.original, from: block?.el.getAttribute('data-bb-lang') ?? 'auto', to: settings.targetLang })
        .then((res) => {
          if (!reader.replaceTranslation(target, res.text)) (tip.querySelector('[data-bb="orig"]') as HTMLElement).textContent = `${info.original}\n\nAI: ${res.text}`;
          btn.textContent = `✨ ${res.model}`;
        }, (e: Error) => { btn.textContent = e.message; });
    });
  }
  document.addEventListener('mouseover', (e) => {
    if (!settings.discord.hoverOriginal) return;
    const t = (e.target as Element | null)?.closest?.('[data-bb-translated="1"]');
    if (t === tipTarget) { clearTimeout(tipTimer); return; }
    clearTimeout(tipTimer);
    if (t) tipTimer = window.setTimeout(() => showTip(t), 350);
    else tipTimer = window.setTimeout(hideTip, 250);
  }, true);
  tip.addEventListener('mouseenter', () => clearTimeout(tipTimer));
  tip.addEventListener('mouseleave', () => { tipTimer = window.setTimeout(hideTip, 250); });

  // ---------- Alt+click restores one message (captured before Discord's own Alt+click) ----------
  const altHandler = (e: MouseEvent): void => {
    if (!e.altKey || !settings.discord.altClickRestore) return;
    const t = (e.target as Element | null)?.closest?.('[data-bb-translated]');
    if (!t) return;
    e.preventDefault();
    e.stopImmediatePropagation();
    if (e.type === 'click') { reader.toggle(t); hideTip(); }
  };
  window.addEventListener('mousedown', altHandler, true);
  window.addEventListener('click', altHandler, true);

  // ---------- settings / messages ----------
  const applyFlags = (): void => { document.documentElement.setAttribute('data-bb-marker', settings.discord.marker ? 'on' : 'off'); };
  applyFlags();
  onSettingsChanged((s) => {
    const was = settings;
    settings = s;
    applyFlags();
    if (!s.discord.enabled && was.discord.enabled) reader.restoreAll();
    if (s.targetLang !== was.targetLang) { reader.cache.clear(); reader.scheduler.clearCache(); reader.restoreAll(); }
    reader.rescan();
    composer.refresh();
  });
  chrome.runtime.onMessage.addListener((msg: TabMessage) => {
    if (msg.type === 'bb:tab:pack-progress') onPackProgress(msg.event);
    if (msg.type === 'bb:tab:reply') void composer.run();
  });

  reader.start();
  new MutationObserver(() => composer.refresh()).observe(document.body, { childList: true, subtree: true });
  composer.refresh();
  // Housekeeping for long sessions: drop references to text nodes Discord unmounted.
  setInterval(() => reader.rewriter.sweep(), 60_000);
}

void main();
