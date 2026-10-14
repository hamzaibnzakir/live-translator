// Injected on demand (toolbar / hotkey / context menu) or registered for "Always translate" sites.
import { languageName } from '../../shared/languages';
import type { PackRef, TabMessage } from '../../shared/messages';
import { send } from '../../shared/messages';
import { loadSettings, onSettingsChanged, type Settings } from '../../shared/settings';
import { escapeHtml, formatBytes, Overlay } from '../common/overlay';
import { PageTranslator } from './pageTranslator';

declare global { interface Window { __brainboxPage?: { translator: PageTranslator } } }

async function main(): Promise<void> {
  if (window.__brainboxPage || location.hostname === 'discord.com') return;
  let resolveReady!: () => void;
  const ready = new Promise<void>((r) => { resolveReady = r; });
  window.__brainboxPage = { translator: null as unknown as PageTranslator };
  // Registered synchronously: the service worker messages us right after injecting.
  chrome.runtime.onMessage.addListener((msg: TabMessage, _sender, sendResponse) => {
    void ready.then(() => sendResponse(onMessage(msg)));
    return msg.type === 'bb:tab:page-toggle' || msg.type === 'bb:tab:page-status';
  });
  let settings: Settings = await loadSettings();
  onSettingsChanged((s) => { settings = s; });
  let overlay: Overlay | null = null;
  let banner: HTMLElement | null = null;

  const needsPack = (p: PackRef): void => {
    overlay ??= new Overlay();
    if (!banner) { banner = overlay.add(overlay.el('div', 'panel banner')); }
    banner.style.display = 'block';
    banner.innerHTML = `<div class="hd"><span class="dot"></span>Brainbox Auto Translator</div>
      <div>This page needs the ${escapeHtml(languageName(p.from))} → ${escapeHtml(languageName(p.to))} pack${p.sizeBytes ? ` (${formatBytes(p.sizeBytes)})` : ''}.</div>
      <div class="bar" style="display:none"><i></i></div>
      <div class="row"><button data-bb="dl">Download</button><button class="ghost" data-bb="x">Not now</button></div>`;
    banner.querySelector('[data-bb="x"]')!.addEventListener('click', () => { banner!.style.display = 'none'; });
    banner.querySelector('[data-bb="dl"]')!.addEventListener('click', () => {
      if (p.engine === 'chrome') { void send({ type: 'bb:open-packs', from: p.from, to: p.to }); return; }
      (banner!.querySelector('.bar') as HTMLElement).style.display = 'block';
      void send({ type: 'bb:packs:download', from: p.from, to: p.to });
    });
  };

  const translator = new PageTranslator({ to: () => settings.targetLang, onNeedsPack: needsPack });
  window.__brainboxPage = { translator };

  function onMessage(msg: TabMessage): unknown {
    if (msg.type === 'bb:tab:page-toggle') {
      if (translator.active) return { active: false, restored: translator.stop() };
      translator.start();
      return { active: true };
    }
    if (msg.type === 'bb:tab:page-status') return { active: translator.active, translated: translator.translatedCount };
    if (msg.type === 'bb:tab:pack-progress') {
      const e = msg.event;
      const bar = banner?.querySelector<HTMLElement>('.bar > i');
      if (bar) bar.style.width = `${e.total ? Math.round((e.loaded / e.total) * 100) : 0}%`;
      if (e.done && !e.error) { if (banner) banner.style.display = 'none'; translator.scheduler.retryWaiting(0); }
    }
    return undefined;
  }
  resolveReady();

  if (settings.sites.always.includes(location.origin) && !settings.sites.never.includes(location.origin)) translator.start();
}

void main();
