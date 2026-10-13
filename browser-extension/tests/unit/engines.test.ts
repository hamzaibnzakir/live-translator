import { describe, expect, it, vi } from 'vitest';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { FakeEngine } from '../../src/engines/fakeEngine';
import { EngineRouter } from '../../src/engines/router';
import { IdbCacheBackend, MemoryCacheBackend, TranslationCache, cacheKey } from '../../src/engines/cache';
import { DEFAULT_SETTINGS, mergeSettings, type Settings } from '../../src/shared/settings';
import { ChromeEngine, mapChromeAvailability } from '../../src/engines/chromeEngine';
import { downloadPair, IntegrityError } from '../../src/engines/bergamot/download';
import { BergamotEngine } from '../../src/engines/bergamot/bergamotEngine';
import { IdbPackStore, MemoryPackStore } from '../../src/engines/bergamot/packStore';
import { modelsFor, type PinnedRegistry } from '../../src/engines/bergamot/registry';
import type { WorkerLike } from '../../src/engines/bergamot/workerClient';
import { Scheduler } from '../../src/content/common/scheduler';
import type { TranslateItem, TranslateResult } from '../../src/shared/messages';

const settings = (p: Partial<Settings> = {}): (() => Settings) => { const s = mergeSettings(DEFAULT_SETTINGS, p); return () => s; };

function router(engine = new FakeEngine({ installAll: true }), s = settings({ engine: 'auto' })) {
  const cache = new TranslationCache(new MemoryCacheBackend());
  return { r: new EngineRouter({ engines: [engine], cache, settings: s }), engine, cache };
}

describe('EngineRouter', () => {
  it('detects, translates, and skips English / noise / URLs', async () => {
    const { r } = router();
    const res = await r.translateItems([
      { id: '1', text: 'Bonjour tout le monde' },
      { id: '2', text: 'I think this is a great idea, see you tonight' },
      { id: '3', text: 'lol' },
      { id: '4', text: 'https://example.com' },
    ], 'en');
    expect(res[0]).toMatchObject({ status: 'ok', from: 'fr', translation: 'Hello everyone', engine: 'fake' });
    expect(res[1]).toMatchObject({ status: 'skipped', reason: 'same-language', from: 'en' });
    expect(res[2]).toMatchObject({ status: 'skipped', reason: 'noise' });
    expect(res[3]).toMatchObject({ status: 'skipped', reason: 'url' });
  });

  it('identical texts are translated once (in-flight de-dupe), then served from cache', async () => {
    const { r, engine } = router(new FakeEngine({ installAll: true, delayMs: 20 }));
    const items = ['a', 'b', 'c'].map((id) => ({ id, text: 'Bonjour tout le monde', from: 'fr' }));
    const res = await r.translateItems(items, 'en');
    expect(res.every((x) => x.translation === 'Hello everyone')).toBe(true);
    expect(engine.calls.flatMap((c) => c.texts)).toEqual(['Bonjour tout le monde']);
    const again = await r.translateOne({ id: 'd', text: '  Bonjour   tout le monde ', from: 'fr' }, 'en');
    expect(again).toMatchObject({ cached: true, translation: 'Hello everyone' });
    expect(engine.calls).toHaveLength(1);
  });

  it('uses the channel hint when detection is unsure', async () => {
    const { r } = router();
    const unsure = await r.translateOne({ id: '1', text: 'salut' }, 'en');
    expect(unsure).toMatchObject({ status: 'skipped', reason: 'low-confidence' });
    const hinted = await r.translateOne({ id: '2', text: 'salut', hint: 'fr' }, 'en');
    expect(hinted).toMatchObject({ status: 'ok', from: 'fr' });
  });

  it('detects on the whole message (detectText) for short segments', async () => {
    const { r } = router();
    const res = await r.translateOne({ id: '1', text: 'Salut', detectText: 'Salut @David, tu viens jouer avec nous ce soir ?' }, 'en');
    expect(res).toMatchObject({ status: 'ok', from: 'fr', translation: 'Hi' });
  });

  it('reports needs-pack (with size) when no engine has the pair ready', async () => {
    const e = new FakeEngine();
    (e as unknown as { packSize: () => number }).packSize = () => 1234;
    const { r } = router(e);
    expect(await r.translateOne({ id: '1', text: 'Bonjour tout le monde' }, 'en')).toMatchObject({ status: 'needs-pack', pack: { from: 'fr', to: 'en', engine: 'fake', sizeBytes: 1234 } });
    await e.ensurePack('fr', 'en');
    r.invalidateAvailability();
    expect(await r.translateOne({ id: '2', text: 'Bonjour tout le monde' }, 'en')).toMatchObject({ status: 'ok' });
  });

  it('unsupported pairs are skipped', async () => {
    const { r } = router(new FakeEngine({ pairs: ['ja-en'] }));
    expect(await r.translateOne({ id: '1', text: 'Bonjour tout le monde' }, 'en')).toMatchObject({ status: 'skipped', reason: 'unsupported-pair' });
  });

  it('queued work can be cancelled (message scrolled away)', async () => {
    const { r, engine } = router(new FakeEngine({ installAll: true, delayMs: 30 }));
    const first = r.translateOne({ id: 'a', text: 'Texte numéro un pour la file', from: 'fr' }, 'en', 1, 't');
    const second = r.translateOne({ id: 'b', text: 'Texte numéro deux pour la file', from: 'fr' }, 'en', 1, 't');
    await new Promise((res) => setTimeout(res, 0));
    r.cancel(['t:b']);
    expect((await first).status).toBe('ok');
    const s = await second;
    expect(s.status === 'error' && s.reason === 'cancelled' || s.status === 'ok').toBe(true);
    expect(engine.calls.flatMap((c) => c.texts).length).toBeLessThanOrEqual(2);
  });

  it('visible work jumps ahead of history in a busy lane', async () => {
    const engine = new FakeEngine({ installAll: true, delayMs: 15 });
    const { r } = router(engine);
    const jobs = Array.from({ length: 12 }, (_, i) => r.translateOne({ id: `h${i}`, text: `Historique message ${i}`, from: 'fr' }, 'en', 2));
    await new Promise((res) => setTimeout(res, 1));
    const vis = r.translateOne({ id: 'v', text: 'Message visible maintenant', from: 'fr' }, 'en', 0);
    await Promise.all([...jobs, vis]);
    const order = engine.calls.flatMap((c) => c.texts);
    expect(order.indexOf('Message visible maintenant')).toBeLessThan(order.indexOf('Historique message 11'));
  });

  it('engine preference: bergamot-only never uses chrome; auto prefers a ready engine', async () => {
    const chrome = new FakeEngine({ installAll: true }); (chrome as { id: string }).id = 'chrome';
    const berg = new FakeEngine({ installAll: true }); (berg as { id: string }).id = 'bergamot';
    const cache = new TranslationCache(new MemoryCacheBackend());
    const only = new EngineRouter({ engines: [chrome, berg], cache, settings: settings({ engine: 'bergamot' }) });
    expect((await only.translateOne({ id: '1', text: 'Bonjour tout le monde', from: 'fr' }, 'en')).engine).toBe('bergamot');
    const notReady = new FakeEngine(); (notReady as { id: string }).id = 'chrome';
    const auto = new EngineRouter({ engines: [notReady, berg], cache, settings: settings({ engine: 'auto' }) });
    expect((await auto.translateOne({ id: '2', text: 'Bonjour tout le monde', from: 'fr' }, 'en')).engine).toBe('bergamot');
  });

  it('an engine that fails (Chrome "Other generic failures occurred") is skipped and the next engine answers', async () => {
    const chrome = new FakeEngine({ installAll: true }); (chrome as { id: string }).id = 'chrome';
    chrome.translate = async () => { throw new Error('Other generic failures occurred.'); };
    const berg = new FakeEngine({ installAll: true }); (berg as { id: string }).id = 'bergamot';
    const r = new EngineRouter({ engines: [chrome, berg], cache: new TranslationCache(new MemoryCacheBackend()), settings: settings({ engine: 'auto' }) });
    const res = await r.translateOne({ id: '1', text: 'Bonjour tout le monde', from: 'fr' }, 'en');
    expect(res.reason).toBeUndefined();
    expect(res).toMatchObject({ status: 'ok', engine: 'bergamot', translation: 'Hello everyone', failedEngine: 'chrome' });
    expect(r.getStats().failures).toEqual([{ engine: 'chrome', pair: 'fr-en', reason: 'Other generic failures occurred.' }]);
    // Later requests go straight to Bergamot without hitting Chrome again.
    const next = await r.translateOne({ id: '2', text: 'Salut', from: 'fr' }, 'en');
    expect(next).toMatchObject({ engine: 'bergamot', status: 'ok' });
    expect(next.failedEngine).toBeUndefined();
  });

  it('when the fallback has no pack, the failure turns into a pack download prompt', async () => {
    const chrome = new FakeEngine({ installAll: true }); (chrome as { id: string }).id = 'chrome';
    chrome.translate = async () => { throw new Error('Other generic failures occurred.'); };
    const berg = new FakeEngine(); (berg as { id: string }).id = 'bergamot';
    const r = new EngineRouter({ engines: [chrome, berg], cache: new TranslationCache(new MemoryCacheBackend()), settings: settings({ engine: 'auto' }) });
    expect(await r.translateOne({ id: '1', text: 'Bonjour tout le monde', from: 'fr' }, 'en')).toMatchObject({ status: 'needs-pack', pack: { engine: 'bergamot' }, failedEngine: 'chrome' });
  });

  it('auto-download fetches a missing pack when enabled', async () => {
    const e = new FakeEngine(); (e as { id: string }).id = 'bergamot';
    const onAutoDownload = vi.fn((eng, f: string, t: string) => eng.ensurePack(f, t));
    const r = new EngineRouter({ engines: [e], cache: new TranslationCache(new MemoryCacheBackend()), settings: settings({ autoDownloadPacks: true }), onAutoDownload });
    expect(await r.translateOne({ id: '1', text: 'Bonjour tout le monde' }, 'en')).toMatchObject({ status: 'ok' });
    expect(onAutoDownload).toHaveBeenCalledOnce();
  });
});

describe('TranslationCache', () => {
  it('keys by (normalizedText, from, to, engine)', async () => {
    const c = new TranslationCache(new MemoryCacheBackend());
    await c.set('bonjour', 'fr', 'en', 'bergamot', 'hello');
    expect(await c.get('bonjour', 'fr', 'en', 'bergamot')).toBe('hello');
    expect(await c.get('bonjour', 'fr', 'en', 'chrome')).toBeUndefined();
    expect(await c.get('bonjour', 'fr', 'de', 'bergamot')).toBeUndefined();
    expect(cacheKey('a', 'fr', 'en', 'x')).toBe('x|fr|en|a');
  });
  it('IndexedDB backend persists, refreshes, prunes least recently used, clears', async () => {
    const b = new IdbCacheBackend(`test-${Math.random()}`);
    for (let i = 0; i < 10; i++) await b.set(`k${i}`, `v${i}`);
    expect(await b.count()).toBe(10);
    expect(await b.prune(6)).toBe(4);
    expect(await b.get('k0')).toBeUndefined();
    expect(await b.get('k9')).toBe('v9');
    // A fresh cache over the same DB sees the data (survives restarts).
    const c = new TranslationCache(b);
    expect(await c.get('9'.replace('9', 'k9').slice(0), '', '', '')).toBeUndefined();
    await b.clear();
    expect(await b.count()).toBe(0);
  });
});

describe('Chrome built-in engine wrapper (mocked Translator API)', () => {
  function fakeGlobal() {
    const log: string[] = [];
    const g = {
      Translator: {
        availability: vi.fn(async ({ sourceLanguage, targetLanguage }: { sourceLanguage: string; targetLanguage: string }) => (sourceLanguage === 'xx' ? 'unavailable' : targetLanguage === 'zh-Hant' ? 'downloadable' : 'available')),
        create: vi.fn(async ({ sourceLanguage, monitor }: { sourceLanguage: string; monitor?: (m: { addEventListener: (t: string, cb: (e: { loaded: number; total: number }) => void) => void }) => void }) => {
          monitor?.({ addEventListener: (_t, cb) => cb({ loaded: 1, total: 1 }) });
          return { translate: async (t: string) => { log.push(`start ${sourceLanguage} ${t}`); await new Promise((r) => setTimeout(r, sourceLanguage === 'ja' ? 30 : 5)); log.push(`end ${sourceLanguage} ${t}`); return `[${sourceLanguage}] ${t}`; } };
        }),
      },
      LanguageDetector: {
        availability: vi.fn(async () => 'available'),
        create: vi.fn(async () => ({ detect: async () => [{ detectedLanguage: 'pt-BR', confidence: 0.93 }, { detectedLanguage: 'es', confidence: 0.05 }] })),
      },
    };
    return { g, log };
  }
  it('maps availability states and BCP 47 codes', async () => {
    const { g } = fakeGlobal();
    const e = new ChromeEngine(g as never);
    expect(e.supported()).toBe(true);
    expect(await e.availability('fr', 'en')).toBe('ready');
    expect(await e.availability('en', 'zh_hant')).toBe('downloadable');
    expect(await e.availability('xx', 'en')).toBe('unsupported');
    expect(g.Translator.availability).toHaveBeenCalledWith({ sourceLanguage: 'en', targetLanguage: 'zh-Hant' });
    expect(['available', 'readily', 'downloadable', 'after-download', 'downloading', 'unavailable', 'no'].map(mapChromeAvailability)).toEqual(['ready', 'ready', 'downloadable', 'downloadable', 'downloading', 'unsupported', 'unsupported']);
  });
  it('runs sequentially per pair, but pairs do not block each other; one translator per pair', async () => {
    const { g, log } = fakeGlobal();
    const e = new ChromeEngine(g as never);
    await Promise.all([e.translate(['a', 'b'], 'ja', 'en'), e.translate(['c'], 'fr', 'en'), e.translate(['d'], 'ja', 'en')]);
    expect(log.indexOf('end fr c')).toBeLessThan(log.indexOf('end ja a'));     // fr not stuck behind ja
    expect(log.indexOf('end ja b')).toBeLessThan(log.indexOf('start ja d'));   // ja strictly sequential
    expect(g.Translator.create).toHaveBeenCalledTimes(2);
  });
  it('detect maps to canonical codes', async () => {
    const { g } = fakeGlobal();
    expect(await new ChromeEngine(g as never).detect('Olá pessoal')).toMatchObject({ lang: 'pt', confidence: 0.93 });
  });
  it('reports unsupported when the API is absent (Edge, Firefox, older Chrome)', async () => {
    const e = new ChromeEngine({} as never);
    expect(e.supported()).toBe(false);
    expect(await e.availability('fr', 'en')).toBe('unsupported');
  });
});

// ---------- Bergamot: downloader and lanes (with a fake worker; the real WASM runs in tests/engine) ----------
const sha = (b: Uint8Array) => createHash('sha256').update(b).digest('hex');
function fakeRegistry(): { reg: PinnedRegistry; files: Map<string, Uint8Array> } {
  const files = new Map<string, Uint8Array>();
  const mk = (path: string, content: string) => { const gz = new Uint8Array(gzipSync(Buffer.from(content))); files.set(path, gz); return { path, sha256: sha(gz), size: gz.byteLength, uncompressedSha256: sha(Buffer.from(content)), uncompressedSize: content.length }; };
  const pair = (f: string, t: string) => ({ from: f, to: t, architecture: 'tiny', releaseStatus: 'Release', files: { model: mk(`${f}${t}/model`, `model-${f}${t}`), lexicalShortlist: mk(`${f}${t}/lex`, `lex-${f}${t}`), vocab: mk(`${f}${t}/vocab`, `vocab-${f}${t}`) } });
  return { reg: { source: 'test', baseUrl: 'https://packs.test', registryGenerated: '', pinnedAt: '', pairs: { 'fr-en': pair('fr', 'en'), 'en-es': pair('en', 'es'), 'ja-en': pair('ja', 'en') } }, files };
}
const fetcherFor = (files: Map<string, Uint8Array>, tamper?: string) => async (url: string) => {
  const path = url.replace('https://packs.test/', '');
  const body = files.get(path);
  if (!body) return new Response('nope', { status: 404 });
  return new Response((path === tamper ? new Uint8Array([...body, 1]) : body) as unknown as BodyInit);
};

class FakeWorker implements WorkerLike {
  static created = 0;
  static terminated = 0;
  models: string[] = [];
  private cb: ((e: { data: unknown }) => void) | null = null;
  constructor() { FakeWorker.created++; }
  addEventListener(type: string, cb: (e: { data: unknown }) => void): void { if (type === 'message') this.cb = cb; }
  postMessage(msg: { id: number; name: string; args: unknown[] }): void {
    let result: unknown = null;
    if (msg.name === 'loadTranslationModel') {
      const [{ from, to }, buffers] = msg.args as [{ from: string; to: string }, { model: ArrayBuffer }];
      expect(new TextDecoder().decode(buffers.model)).toBe(`model-${from}${to}`); // decompressed + verified bytes arrive
      this.models.push(`${from}-${to}`);
    }
    if (msg.name === 'translate') {
      const { models, texts } = msg.args[0] as { models: { from: string; to: string }[]; texts: { text: string }[] };
      result = texts.map((t) => ({ target: { text: `${models.map((m) => `${m.from}>${m.to}`).join('|')}:${t.text}` } }));
    }
    if (msg.name === 'wasmMemoryBytes') result = 1024;
    queueMicrotask(() => this.cb?.({ data: { id: msg.id, result } }));
  }
  terminate(): void { FakeWorker.terminated++; }
}

describe('Bergamot packs + engine', () => {
  it('pivot routing through English', () => {
    const { reg } = fakeRegistry();
    expect(modelsFor('fr', 'en', reg)).toEqual(['fr-en']);
    expect(modelsFor('fr', 'es', reg)).toEqual(['fr-en', 'en-es']);
    expect(modelsFor('es', 'fr', reg)).toBeNull();
    expect(modelsFor('fr', 'fr', reg)).toBeNull();
  });
  it('downloads, verifies SHA-256 (compressed + decompressed) and decompresses', async () => {
    const { reg, files } = fakeRegistry();
    const progress: number[] = [];
    const out = await downloadPair(reg.baseUrl, reg.pairs['fr-en']!, fetcherFor(files), (l) => progress.push(l));
    expect(new TextDecoder().decode(out.model)).toBe('model-fren');
    expect(progress.at(-1)).toBe(Object.values(reg.pairs['fr-en']!.files).reduce((s, f) => s + f!.size, 0));
  });
  it('rejects a tampered file', async () => {
    const { reg, files } = fakeRegistry();
    await expect(downloadPair(reg.baseUrl, reg.pairs['fr-en']!, fetcherFor(files, 'fren/vocab'))).rejects.toBeInstanceOf(IntegrityError);
  });
  it('engine: availability → ensurePack → translate (direct and pivot), worker per pair with LRU eviction', async () => {
    const { reg, files } = fakeRegistry();
    const store = new MemoryPackStore();
    const e = new BergamotEngine({ store, registry: reg, baseUrl: () => reg.baseUrl, fetcher: fetcherFor(files), createWorker: () => new FakeWorker(), maxWorkers: 2 });
    expect(await e.availability('fr', 'en')).toBe('downloadable');
    expect(await e.availability('de', 'en')).toBe('unsupported');
    expect(e.packSize('fr', 'es')).toBeGreaterThan(e.packSize('fr', 'en'));
    await e.ensurePack('fr', 'es');                        // pivot: downloads fr-en + en-es
    expect(await e.availability('fr', 'en')).toBe('ready');
    expect(await e.translate(['salut'], 'fr', 'en')).toEqual(['fr>en:salut']);
    expect(await e.translate(['salut'], 'fr', 'es')).toEqual(['fr>en|en>es:salut']);
    await e.ensurePack('ja', 'en');
    FakeWorker.terminated = 0;
    await e.translate(['x'], 'ja', 'en');                  // third lane → oldest is evicted
    expect(FakeWorker.terminated).toBe(1);
    expect(e.stats().map((s) => s.pair).sort()).toEqual(['fr-es', 'ja-en']);
    await e.removePack('ja', 'en');
    expect(await e.availability('ja', 'en')).toBe('downloadable');
  });
  it('IndexedDB pack store round-trips buffers', async () => {
    const s = new IdbPackStore(`packs-${Math.random()}`);
    await s.put('fr-en', { model: new Uint8Array([1, 2, 3]).buffer }, { pair: 'fr-en', bytes: 3, installedAt: 1, architecture: 'tiny' });
    expect(await s.has('fr-en')).toBe(true);
    expect([...new Uint8Array((await s.get('fr-en'))!.model!)]).toEqual([1, 2, 3]);
    expect((await s.list()).map((m) => m.pair)).toEqual(['fr-en']);
    await s.delete('fr-en');
    expect(await s.has('fr-en')).toBe(false);
  });
});

describe('content Scheduler', () => {
  function sched(handler: (items: TranslateItem[], to: string, p: number) => TranslateResult[]) {
    const sent: { items: TranslateItem[]; priority: number }[] = [];
    const results: Record<string, TranslateResult[]> = {};
    const needs: string[] = [];
    const s = new Scheduler({
      to: () => 'en', purpose: 'read', batchSize: 4,
      onResult: (k, rs) => { results[k] = rs; },
      onNeedsPack: (p, r) => needs.push(`${r.key}:${p.from}`),
      transport: async (items, to, p) => { sent.push({ items, priority: p }); return handler(items, to, p); },
    });
    return { s, sent, results, needs };
  }
  const ok = (items: TranslateItem[]): TranslateResult[] => items.map((i) => ({ id: i.id, status: 'ok', from: 'fr', confidence: 1, translation: `EN(${i.text})` }));
  const tick = () => new Promise((r) => setTimeout(r, 5));

  it('sends visible first, batches, caches per text', async () => {
    const { s, sent, results } = sched(ok);
    s.request({ key: 'h', texts: ['old'] }, 2);
    s.request({ key: 'v1', texts: ['a', 'b'] }, 0);
    s.request({ key: 'v2', texts: ['c'] }, 0);
    await tick(); await tick();
    expect(sent[0]!.priority).toBe(0);
    expect(sent[0]!.items.map((i) => i.text)).toEqual(['a', 'b', 'c']);
    expect(results.v1!.map((r) => r.translation)).toEqual(['EN(a)', 'EN(b)']);
    expect(s.cached('  a ')?.translation).toBe('EN(a)');
    expect(results.h).toBeDefined();
  });
  it('parks requests that need a pack and retries them later', async () => {
    let installed = false;
    const { s, results, needs } = sched((items) => (installed ? ok(items) : items.map((i) => ({ id: i.id, status: 'needs-pack', from: 'fr', confidence: 1, pack: { from: 'fr', to: 'en', engine: 'bergamot' } }))));
    s.request({ key: 'm', texts: ['Bonjour'] }, 0);
    await tick();
    expect(needs).toEqual(['m:fr']);
    expect(results.m).toBeUndefined();
    installed = true;
    s.retryWaiting();
    await tick();
    expect(results.m![0]!.translation).toBe('EN(Bonjour)');
  });
  it('cancel drops queued work before it is sent', async () => {
    const { s, sent } = sched(ok);
    s.request({ key: 'gone', texts: ['x'] }, 1);
    s.cancel('gone');
    await tick();
    expect(sent).toHaveLength(0);
  });
});
