// Brainbox Translate for discord.com. Reads (in-place rewrite) and helps replying.
// Never uses Discord's API or your token, never sends anything: it only edits text on screen.
import { languageName } from '../../shared/languages';
import type { PackProgressEvent, PackRef, TabMessage, TranslateResult } from '../../shared/messages';
import { send } from '../../shared/messages';
import { DEFAULT_SETTINGS, loadSettings, onSettingsChanged, saveSettings, type Settings } from '../../shared/settings';
import { escapeHtml, formatBytes, Notices, Overlay, type NoticeHandle } from '../common/overlay';
import { icon, LOGO_SVG } from '../common/theme';
import { Composer } from './composer';
import { DiscordReader } from './reader';

declare global { interface Window { __brainboxDiscord?: unknown } }

async function main(): Promise<void> {
  if (window.__brainboxDiscord) return;
  let settings: Settings = DEFAULT_SETTINGS;
  settings = await loadSettings();
  const overlay = new Overlay();
  const get = (): Settings => settings;

  // ---------- notices (pack download, ask mode, engine problems) ----------
  const notices = new Notices(overlay, LOGO_SVG, icon('close'));
  const dismissedPacks = new Set<string>();
  const askedChannels = new Set<string>();
  let packNotice: { key: string; handle: NoticeHandle } | null = null;

  function showPackBanner(pack: PackRef): void {
    const key = `${pack.from}-${pack.to}`;
    if (dismissedPacks.has(key) || (packNotice?.key === key && packNotice.handle.open)) return;
    const from = languageName(pack.from), to = languageName(pack.to);
    const size = pack.sizeBytes ? ` (${formatBytes(pack.sizeBytes)})` : '';
    const start = (): void => {
      if (pack.engine === 'chrome') { void send({ type: 'bb:open-packs', from: pack.from, to: pack.to }); return; }
      handle.update({ title: `Downloading ${from} → ${to}…`, body: 'Verified and stored on this computer. Messages translate as soon as it finishes.', progress: 0, actions: [] });
      void send({ type: 'bb:packs:download', from: pack.from, to: pack.to }).catch((e: Error) => {
        handle.update({ kind: 'warn', title: 'Download failed', body: e.message, progress: null, actions: [{ label: 'Try again', primary: true, onClick: start }] });
      });
    };
    const handle = notices.show('pack', {
      title: `Translate ${from} messages?`,
      body: `Needs the ${from} → ${to} ${pack.engine === 'chrome' ? 'Chrome built-in' : 'offline'} pack${size}. Downloaded once, then it works offline.`,
      actions: [
        { label: `Download ${from}`, primary: true, icon: icon('download'), onClick: start },
        { label: 'Not now', onClick: () => { dismissedPacks.add(key); handle.close(); } },
      ],
    });
    packNotice = { key, handle };
  }

  function onPackProgress(e: PackProgressEvent): void {
    const mine = packNotice && packNotice.key === `${e.from}-${e.to}` && packNotice.handle.open ? packNotice.handle : null;
    if (mine && !e.done) mine.update({ progress: e.total ? e.loaded / e.total : 'busy' });
    if (!e.done) return;
    if (e.error) { mine?.update({ kind: 'warn', title: 'Download failed', body: e.error, progress: null }); return; }
    mine?.close();
    reader.scheduler.retryWaiting();
    reader.rescan();
  }

  function showAsk(guildId: string, channelId: string, lang: string): void {
    if (askedChannels.has(channelId)) return;
    askedChannels.add(channelId);
    const choose = (m: 'channel' | 'server' | 'once' | 'never') => (): void => {
      handle.close();
      if (m === 'once') reader.sessionAllowed.add(channelId);
      else void saveSettings((s) => {
        const d = { ...s.discord, channels: { ...s.discord.channels }, servers: { ...s.discord.servers } };
        if (m === 'channel') d.channels[`${guildId}/${channelId}`] = 'always';
        if (m === 'server') d.servers[guildId] = 'always';
        if (m === 'never') d.channels[`${guildId}/${channelId}`] = 'never';
        return { ...s, discord: d };
      });
      if (m !== 'never') { reader.sessionAllowed.add(channelId); reader.rescan(); }
    };
    const handle = notices.show('ask', {
      title: `This channel is in ${languageName(lang)}`,
      body: `Translate it to ${languageName(settings.targetLang)} as you read?`,
      actions: [
        { label: 'Always here', primary: true, onClick: choose('channel') },
        { label: 'Whole server', onClick: choose('server') },
        { label: 'Just now', onClick: choose('once') },
        { label: 'Never', onClick: choose('never') },
      ],
    });
  }

  const reportedProblems = new Set<string>();
  function onEngineProblem(engine: string, reason: string, recovered: boolean): void {
    const key = `${engine}|${recovered}`;
    if (reportedProblems.has(key)) return;
    reportedProblems.add(key);
    const name = engine === 'chrome' ? 'Chrome’s built-in translator' : engine === 'bergamot' ? 'The offline engine' : 'The translator';
    notices.show(`engine-${engine}`, {
      kind: 'warn',
      title: recovered ? `${name} failed, switched to the offline engine` : `${name} failed`,
      body: `“${reason}”.${recovered ? ' Messages keep translating offline. Brainbox will try it again in 10 minutes.' : ' Check Settings → Engine.'}`,
      actions: [
        { label: 'Use offline only', primary: !recovered, onClick: () => { void saveSettings((s) => ({ ...s, engine: 'bergamot' })); notices.get(`engine-${engine}`)?.close(); } },
        { label: 'Open settings', onClick: () => void send({ type: 'bb:open-packs' }) },
      ],
    });
  }

  const reader = new DiscordReader(get, { onNeedsPack: showPackBanner, onAsk: showAsk, onVote: () => composer.refresh(), onEngineProblem });
  const composer = new Composer(overlay, reader, get, (r: TranslateResult) => r.pack && showPackBanner(r.pack));
  window.__brainboxDiscord = { reader, composer, settings: get };

  // ---------- hover tooltip ----------
  const tip = overlay.add(overlay.el('div', 'panel tip in'));
  tip.style.display = 'none';
  let tipTarget: Element | null = null;
  let tipTimer = 0;
  const hideTip = (): void => { tip.style.display = 'none'; tipTarget = null; };
  function showTip(target: Element): void {
    const info = reader.info(target);
    if (!info) return;
    tipTarget = target;
    const engine = info.engine === 'bergamot' ? 'Offline' : info.engine === 'chrome' ? 'Chrome built-in' : info.engine === 'ai' ? 'AI polished' : info.engine;
    tip.innerHTML = `<div class="hd">${LOGO_SVG}<span>Original</span><span class="sp"></span><span class="chip">${escapeHtml(info.lang)}</span>${engine ? `<span class="chip ok">${escapeHtml(engine)}</span>` : ''}</div>
      <div class="orig" data-bb="orig"></div>
      <div class="row"><button class="ghost" data-bb="swap">${icon('undo')}Show original <kbd>Alt</kbd>+click</button>${settings.polish.enabled ? `<button data-bb="polish">${icon('spark')}Polish with AI</button>` : ''}</div>`;
    (tip.querySelector('[data-bb="orig"]') as HTMLElement).textContent = info.original;
    tip.style.display = 'block';
    const r = target.getBoundingClientRect();
    const h = tip.offsetHeight;
    tip.style.left = `${Math.max(8, Math.min(window.innerWidth - tip.offsetWidth - 8, r.left))}px`;
    tip.style.top = `${r.top - h - 8 > 8 ? r.top - h - 8 : r.bottom + 8}px`;
    tip.querySelector('[data-bb="swap"]')!.addEventListener('click', () => { reader.toggle(target); hideTip(); });
    tip.querySelector('[data-bb="polish"]')?.addEventListener('click', () => {
      const btn = tip.querySelector('[data-bb="polish"]') as HTMLButtonElement;
      btn.disabled = true;
      btn.textContent = 'Polishing…';
      const block = reader.blockFor(target);
      const current = block?.el.textContent ?? '';
      void send<{ text: string; model: string }>({ type: 'bb:polish', text: current, original: info.original, from: block?.el.getAttribute('data-bb-lang') ?? 'auto', to: settings.targetLang })
        .then((res) => {
          if (!reader.replaceTranslation(target, res.text)) (tip.querySelector('[data-bb="orig"]') as HTMLElement).textContent = `${info.original}\n\nAI: ${res.text}`;
          btn.textContent = `Polished · ${res.model}`;
        }, (e: Error) => { btn.textContent = e.message; btn.disabled = false; });
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
