import type { Envelope, PackProgressEvent, Request, TranslateResult } from '../shared/messages';
import { DEFAULT_SETTINGS, loadSettings, onSettingsChanged, saveSettings, type Settings } from '../shared/settings';
import { languageName } from '../shared/languages';
import { polish } from './polish';
import { showSelectionCard, showToast } from './inject';

let settings: Settings = DEFAULT_SETTINGS;
const settingsReady = loadSettings().then((s) => { settings = s; return s; });
onSettingsChanged((s) => { settings = s; void rebuildMenus(); void syncSiteScripts(); });

// ---------- offscreen engine host ----------
let creating: Promise<void> | null = null;
let readyResolve: (() => void) | null = null;

async function ensureOffscreen(): Promise<void> {
  const contexts = await chrome.runtime.getContexts({ contextTypes: [chrome.runtime.ContextType.OFFSCREEN_DOCUMENT] });
  if (contexts.length) return;
  if (!creating) {
    creating = (async () => {
      const ready = new Promise<void>((r) => { readyResolve = r; });
      await chrome.offscreen.createDocument({
        url: 'offscreen.html',
        reasons: [chrome.offscreen.Reason.WORKERS, chrome.offscreen.Reason.CLIPBOARD],
        justification: 'Runs the on-device translation engines (WASM workers, Chrome Translator API) and clipboard mode.',
      });
      await Promise.race([ready, new Promise((r) => setTimeout(r, 2000))]);
    })().finally(() => { creating = null; });
  }
  await creating;
}

async function toOffscreen<T>(msg: Request): Promise<T> {
  await settingsReady;
  await ensureOffscreen();
  const res = (await chrome.runtime.sendMessage({ ...msg, target: 'offscreen', settings })) as T | { __error: string };
  if (res && typeof res === 'object' && '__error' in (res as object)) throw new Error((res as { __error: string }).__error);
  return res as T;
}

// Tabs that are waiting on a pack and want progress events.
const interestedTabs = new Set<number>();

function relayProgress(e: PackProgressEvent): void {
  for (const tabId of interestedTabs) chrome.tabs.sendMessage(tabId, { type: 'bb:tab:pack-progress', event: e }).catch(() => interestedTabs.delete(tabId));
}

// ---------- page translation ----------
async function injectPage(tabId: number): Promise<void> {
  await chrome.scripting.executeScript({ target: { tabId }, files: ['content-page.js'] });
}

async function togglePage(tabId: number): Promise<unknown> {
  await injectPage(tabId);
  return chrome.tabs.sendMessage(tabId, { type: 'bb:tab:page-toggle' });
}

async function syncSiteScripts(): Promise<void> {
  const id = 'bb-always-sites';
  const existing = await chrome.scripting.getRegisteredContentScripts({ ids: [id] }).catch(() => []);
  const granted = await chrome.permissions.getAll();
  const origins = settings.sites.always.filter((o) => granted.origins?.some((g) => g.startsWith(o)));
  if (existing.length) await chrome.scripting.unregisterContentScripts({ ids: [id] }).catch(() => undefined);
  if (!origins.length) return;
  await chrome.scripting.registerContentScripts([{ id, matches: origins.map((o) => `${o}/*`), js: ['content-page.js'], runAt: 'document_idle', persistAcrossSessions: true }]);
}

// ---------- context menus ----------
async function rebuildMenus(): Promise<void> {
  await chrome.contextMenus.removeAll();
  chrome.contextMenus.create({ id: 'bb-sel', title: `Translate selection to ${languageName(settings.targetLang)}`, contexts: ['selection'] });
  chrome.contextMenus.create({ id: 'bb-sel-to', title: 'Translate selection to…', contexts: ['selection'] });
  for (const code of settings.recentLangs.filter((c) => c !== settings.targetLang).slice(0, 6)) {
    chrome.contextMenus.create({ id: `bb-sel-to:${code}`, parentId: 'bb-sel-to', title: languageName(code), contexts: ['selection'] });
  }
  chrome.contextMenus.create({ id: 'bb-page', title: 'Translate this page / show original', contexts: ['page'] });
}

chrome.runtime.onInstalled.addListener(() => { void settingsReady.then(() => { void rebuildMenus(); void syncSiteScripts(); }); });
chrome.runtime.onStartup.addListener(() => { void settingsReady.then(rebuildMenus); });

async function onContextMenu(info: chrome.contextMenus.OnClickData, tab?: chrome.tabs.Tab): Promise<void> {
  {
    if (!tab?.id) return;
    if (info.menuItemId === 'bb-page') { await togglePage(tab.id); return; }
    const text = info.selectionText?.trim();
    if (!text) return;
    const to = info.menuItemId === 'bb-sel' ? settings.targetLang : String(info.menuItemId).split(':')[1] ?? settings.targetLang;
    const [r] = await toOffscreen<TranslateResult[]>({ type: 'bb:translate', items: [{ id: 'sel', text }], to, priority: 0, purpose: 'quick' });
    let error: string | null = null;
    if (r?.status === 'needs-pack') error = `Download the ${languageName(r.pack!.from)} → ${languageName(r.pack!.to)} pack first (Brainbox popup → Packs).`;
    else if (r?.status === 'skipped') error = r.reason === 'same-language' ? `Already ${languageName(to)}.` : `Nothing to translate (${r.reason}).`;
    else if (r?.status === 'error') error = r.reason ?? 'Translation failed';
    const label = `${languageName(r?.from ?? 'und')} → ${languageName(to)}`;
    await chrome.scripting.executeScript({ target: { tabId: tab.id, frameIds: [info.frameId ?? 0] }, func: showSelectionCard, args: [text, r?.translation ?? '', label, error] });
    if (to !== settings.targetLang) await saveSettings((s) => ({ ...s, recentLangs: [to, ...s.recentLangs.filter((c) => c !== to)].slice(0, 6) }));
  }
}
chrome.contextMenus.onClicked.addListener((info, tab) => { void onContextMenu(info, tab); });

// ---------- keyboard commands ----------
chrome.commands.onCommand.addListener((command, tab) => {
  void (async () => {
    const tabId = tab?.id ?? (await chrome.tabs.query({ active: true, currentWindow: true }))[0]?.id;
    if (!tabId) return;
    if (command === 'translate-page') await togglePage(tabId);
    if (command === 'clipboard-translate') {
      if (!settings.clipboardMode) { await chrome.scripting.executeScript({ target: { tabId }, func: showToast, args: ['Brainbox: turn on Clipboard mode in settings first'] }).catch(() => undefined); return; }
      const r = await toOffscreen<{ ok: boolean; to?: string; reason?: string }>({ type: 'bb:clipboard:translate' });
      const msg = r.ok ? `Copied ${languageName(r.to!)} ✓` : `Brainbox: ${r.reason ?? 'could not translate the clipboard'}`;
      await chrome.scripting.executeScript({ target: { tabId }, func: showToast, args: [msg] }).catch(() => undefined);
    }
  })();
});

// ---------- message router ----------
const OFFSCREEN_TYPES = new Set(['bb:translate', 'bb:cancel', 'bb:detect', 'bb:availability', 'bb:packs:list', 'bb:packs:download', 'bb:packs:remove', 'bb:packs:chrome-installed', 'bb:cache:clear', 'bb:stats', 'bb:stats:reset', 'bb:clipboard:translate', 'bb:engine-check']);

async function handle(msg: Request, sender: chrome.runtime.MessageSender): Promise<unknown> {
  const tabId = sender.tab?.id;
  if (msg.type === 'bb:translate' && tabId !== undefined) {
    const out = await toOffscreen<TranslateResult[]>({ ...msg, tabKey: String(tabId) });
    if (out.some((r) => r.status === 'needs-pack')) interestedTabs.add(tabId);
    return out;
  }
  if (msg.type === 'bb:cancel' && tabId !== undefined) return toOffscreen({ ...msg, tabKey: String(tabId) });
  if (msg.type === 'bb:packs:download' && tabId !== undefined) interestedTabs.add(tabId);
  if (OFFSCREEN_TYPES.has(msg.type)) return toOffscreen(msg);
  switch (msg.type) {
    case 'bb:ping': return 'pong';
    case 'bb:polish': return polish(settings, msg.text, msg.from, msg.to, msg.original, msg.test);
    case 'bb:page:toggle': {
      const id = msg.tabId ?? tabId;
      if (id === undefined) throw new Error('No tab');
      return togglePage(id);
    }
    case 'bb:page:always': {
      await saveSettings((s) => ({ ...s, sites: { always: [...new Set([...s.sites.always, msg.origin])], never: s.sites.never.filter((o) => o !== msg.origin) } }));
      settings = await loadSettings();
      await syncSiteScripts();
      if (msg.tabId !== undefined) await injectPage(msg.tabId);
      return true;
    }
    case 'bb:page:never':
      await saveSettings((s) => ({ ...s, sites: { never: [...new Set([...s.sites.never, msg.origin])], always: s.sites.always.filter((o) => o !== msg.origin) } }));
      return true;
    case 'bb:open-packs': {
      const q = msg.from && msg.to ? `?from=${msg.from}&to=${msg.to}` : '';
      await chrome.windows.create({ url: chrome.runtime.getURL(`options.html${q}#packs`), type: 'popup', width: 560, height: 640 });
      return true;
    }
    default:
      throw new Error(`Unknown request ${msg.type}`);
  }
}

chrome.runtime.onMessage.addListener((raw: Envelope, sender, sendResponse) => {
  const type = (raw as { type: string }).type;
  if (type === 'bb:offscreen-ready') { readyResolve?.(); return false; }
  if (type === 'bb:packs:progress') { relayProgress(raw as unknown as PackProgressEvent); return false; }
  if (raw.target !== 'bg') return false;
  handle(raw, sender).then(sendResponse, (e: Error) => sendResponse({ __error: e.message }));
  return true;
});

// Test builds only: lets the Playwright suite call the same handlers content scripts use.
if (__BB_TEST__) {
  const g = globalThis as unknown as { __bbHandle: unknown; __bbContextMenu: unknown };
  g.__bbHandle = (m: Request) => handle(m, {});
  g.__bbContextMenu = onContextMenu;
}
