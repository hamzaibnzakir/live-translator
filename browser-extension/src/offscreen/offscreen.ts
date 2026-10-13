// The engine host. Lives in an offscreen document (extension origin) because it needs DOM-only
// APIs (Worker for WASM, Chrome's Translator API, IndexedDB shared across tabs, clipboard).
import { TranslationCache, IdbCacheBackend } from '../engines/cache';
import { ChromeEngine } from '../engines/chromeEngine';
import { BergamotEngine } from '../engines/bergamot/bergamotEngine';
import { IdbPackStore } from '../engines/bergamot/packStore';
import { DEFAULT_PACK_BASE_URL, REGISTRY, pairDownloadSize } from '../engines/bergamot/registry';
import { EngineRouter } from '../engines/router';
import type { PackInfo, PackProgressEvent, Request } from '../shared/messages';
import { DEFAULT_SETTINGS, mergeSettings, type Settings } from '../shared/settings';
import type { WorkerLike } from '../engines/bergamot/workerClient';

let settings: Settings = DEFAULT_SETTINGS;
const store = new IdbPackStore();
const chromeEngine = new ChromeEngine();
const bergamot = new BergamotEngine({
  store,
  createWorker: () => new Worker(chrome.runtime.getURL('vendor/bergamot/bb-worker.js')) as unknown as WorkerLike,
  baseUrl: () => settings.packBaseUrl || DEFAULT_PACK_BASE_URL,
  maxWorkers: 3,
});
const cache = new TranslationCache(new IdbCacheBackend(), settings.cacheMaxEntries);

const broadcast = (e: PackProgressEvent): void => { chrome.runtime.sendMessage({ ...e, target: 'ui' }).catch(() => undefined); };

async function downloadPack(from: string, to: string): Promise<void> {
  let last = 0;
  try {
    await bergamot.ensurePack(from, to, ({ loaded, total }) => {
      if (Date.now() - last > 200 || loaded === total) { last = Date.now(); broadcast({ type: 'bb:packs:progress', from, to, loaded, total }); }
    });
    router.invalidateAvailability();
    broadcast({ type: 'bb:packs:progress', from, to, loaded: 1, total: 1, done: true });
  } catch (e) {
    broadcast({ type: 'bb:packs:progress', from, to, loaded: 0, total: 0, done: true, error: String((e as Error).message) });
    throw e;
  }
}

const router = new EngineRouter({
  engines: [chromeEngine, bergamot],
  cache,
  settings: () => settings,
  onAutoDownload: (_e, from, to) => downloadPack(from, to),
});

async function listPacks(): Promise<PackInfo[]> {
  const installed = new Map((await store.list()).map((m) => [m.pair, m]));
  return Object.entries(REGISTRY.pairs).map(([pair, p]) => ({
    pair, from: p.from, to: p.to, engine: 'bergamot' as const,
    installed: installed.has(pair), sizeBytes: pairDownloadSize(pair), installedBytes: installed.get(pair)?.bytes,
  }));
}

// Clipboard (optional clipboard mode). Offscreen documents cannot use navigator.clipboard, so we
// go through a textarea + execCommand, which needs the clipboardRead / clipboardWrite permissions.
function readClipboard(): string {
  const ta = document.createElement('textarea');
  document.body.appendChild(ta);
  ta.focus();
  document.execCommand('paste');
  const v = ta.value;
  ta.remove();
  return v;
}
function writeClipboard(text: string): void {
  const ta = document.createElement('textarea');
  ta.value = text;
  document.body.appendChild(ta);
  ta.select();
  document.execCommand('copy');
  ta.remove();
}

async function handle(msg: Request): Promise<unknown> {
  switch (msg.type) {
    case 'bb:translate':
      return router.translateItems(msg.items, msg.to, msg.priority ?? 1, msg.tabKey ?? '');
    case 'bb:cancel':
      router.cancel(msg.ids.map((id) => `${msg.tabKey ?? ''}:${id}`));
      return true;
    case 'bb:detect':
      return router.detect(msg.text);
    case 'bb:availability':
      return {
        chrome: await chromeEngine.availability(msg.from, msg.to),
        bergamot: await bergamot.availability(msg.from, msg.to),
        bergamotSize: bergamot.packSize(msg.from, msg.to),
        detector: await chromeEngine.detectorAvailability(),
      };
    case 'bb:packs:list':
      return listPacks();
    case 'bb:packs:download':
      await downloadPack(msg.from, msg.to);
      return true;
    case 'bb:packs:remove':
      await bergamot.removePack(msg.from, msg.to);
      router.invalidateAvailability();
      return true;
    case 'bb:packs:chrome-installed':
      router.invalidateAvailability();
      return true;
    case 'bb:cache:clear':
      await cache.clear();
      return true;
    case 'bb:stats':
      return router.getStats();
    case 'bb:stats:reset':
      router.resetStats();
      return true;
    case 'bb:clipboard:translate': {
      const text = readClipboard().trim();
      if (!text) return { ok: false, reason: 'Clipboard is empty' };
      const d = await router.detect(text);
      const to = d?.lang === settings.targetLang ? (settings.recentLangs.find((l) => l !== settings.targetLang) ?? 'es') : settings.targetLang;
      const [r] = await router.translateItems([{ id: 'clip', text, from: d?.lang ?? 'auto' }], to, 0, 'clipboard');
      if (r?.status !== 'ok' || !r.translation) return { ok: false, reason: r?.reason ?? r?.status, result: r };
      writeClipboard(r.translation);
      return { ok: true, to, translation: r.translation };
    }
    default:
      throw new Error(`Unknown request ${(msg as { type: string }).type}`);
  }
}

chrome.runtime.onMessage.addListener((raw: Request & { target?: string; settings?: Settings }, _sender, sendResponse) => {
  if (raw.target !== 'offscreen') return false;
  if (raw.settings) { settings = mergeSettings(DEFAULT_SETTINGS, raw.settings); cache.setMax(settings.cacheMaxEntries); }
  handle(raw).then(sendResponse, (e: Error) => sendResponse({ __error: e.message }));
  return true;
});

chrome.runtime.sendMessage({ type: 'bb:offscreen-ready', target: 'bg' }).catch(() => undefined);
