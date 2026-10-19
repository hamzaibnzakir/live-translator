import { describe, expect, it } from 'vitest';
import { gzipSync } from 'node:zlib';
import { createHash } from 'node:crypto';
import { Scheduler } from '../../src/content/common/scheduler';
import type { TranslateItem, TranslateResult } from '../../src/shared/messages';
import { BergamotEngine } from '../../src/engines/bergamot/bergamotEngine';
import { MemoryPackStore } from '../../src/engines/bergamot/packStore';
import type { PinnedRegistry } from '../../src/engines/bergamot/registry';
import type { WorkerLike } from '../../src/engines/bergamot/workerClient';
import { isMessageBox } from '../../src/content/discord/discordSelectors';

const tick = (ms = 5) => new Promise((r) => setTimeout(r, ms));

describe('scheduler robustness', () => {
  it('retries a batch after a transient failure, at most 3 times', async () => {
    let calls = 0;
    const results: Record<string, TranslateResult[]> = {};
    const s = new Scheduler({
      to: () => 'en', purpose: 'read',
      onResult: (k, rs) => { results[k] = rs; },
      transport: async (items: TranslateItem[]) => { calls++; if (calls < 2) throw new Error('Could not establish connection. Receiving end does not exist.'); return items.map((i) => ({ id: i.id, status: 'ok' as const, from: 'fr', confidence: 1, translation: 'ok' })); },
    });
    s.request({ key: 'm', texts: ['Bonjour'] }, 0);
    await tick(10);
    expect(results.m).toBeUndefined();
    await tick(3100);
    expect(results.m?.[0]?.translation).toBe('ok');
    expect(calls).toBe(2);
  }, 10_000);

  it('stops for good when the extension context is gone (extension updated)', async () => {
    let disconnected = 0;
    const s = new Scheduler({
      to: () => 'en', purpose: 'read', onResult: () => undefined, onDisconnected: () => { disconnected++; },
      transport: async () => { throw new Error('Extension context invalidated.'); },
    });
    s.request({ key: 'm', texts: ['Bonjour'] }, 0);
    await tick(20);
    expect(disconnected).toBe(1);
    expect(s.dead).toBe(true);
    s.request({ key: 'n', texts: ['Salut'] }, 0);
    expect(s.isPending('n')).toBe(false);
  });

  it('does not cache "too unsure" answers (the channel language may be known later)', async () => {
    const s = new Scheduler({
      to: () => 'en', purpose: 'read', onResult: () => undefined,
      transport: async (items: TranslateItem[]) => items.map((i) => ({ id: i.id, status: 'skipped' as const, from: 'und', confidence: 0.2, reason: i.text === 'salut' ? 'low-confidence' : 'same-language' })),
    });
    s.request({ key: 'a', texts: ['salut', 'hello there my friend'] }, 0);
    await tick(20);
    expect(s.cached('salut')).toBeUndefined();
    expect(s.cached('hello there my friend')?.reason).toBe('same-language');
  });
});

describe('Bergamot worker crash recovery', () => {
  it('a crashed worker is replaced by a fresh one on the next request', async () => {
    const gz = (t: string) => new Uint8Array(gzipSync(Buffer.from(t)));
    const sha = (b: Uint8Array) => createHash('sha256').update(b).digest('hex');
    const files = { model: gz('m'), lexicalShortlist: gz('l'), vocab: gz('v') };
    const reg: PinnedRegistry = { source: '', baseUrl: 'https://p.test', registryGenerated: '', pinnedAt: '', pairs: { 'fr-en': { from: 'fr', to: 'en', architecture: 'tiny', releaseStatus: null, files: Object.fromEntries(Object.entries(files).map(([k, b]) => [k, { path: k, sha256: sha(b), size: b.byteLength }])) } } };
    let created = 0;
    const make = (): WorkerLike => {
      const n = ++created;
      let msgCb: ((e: { data: unknown }) => void) | null = null, errCb: ((e: unknown) => void) | null = null;
      return {
        addEventListener: (t: string, cb: (e: { data: unknown }) => void) => { if (t === 'message') msgCb = cb; else errCb = cb as (e: unknown) => void; },
        postMessage: (m: unknown) => {
          const { id, name, args } = m as { id: number; name: string; args: unknown[] };
          // The first worker dies (out of memory) on its first translation.
          if (name === 'translate' && n === 1) { queueMicrotask(() => errCb?.({ message: 'out of memory' })); return; }
          const result = name === 'translate' ? (args[0] as { texts: { text: string }[] }).texts.map((t) => ({ target: { text: `EN:${t.text}` } })) : null;
          queueMicrotask(() => msgCb?.({ data: { id, result } }));
        },
        terminate: () => undefined,
      } as WorkerLike;
    };
    const e = new BergamotEngine({ store: new MemoryPackStore(), registry: reg, baseUrl: () => reg.baseUrl, fetcher: async (u) => new Response(files[u.split('/').pop() as keyof typeof files] as unknown as BodyInit), createWorker: make });
    await e.ensurePack('fr', 'en');
    await expect(e.translate(['salut'], 'fr', 'en')).rejects.toThrow(/crashed/);
    expect(await e.translate(['salut'], 'fr', 'en')).toEqual(['EN:salut']);
    expect(created).toBe(2);
  });
});

describe('message boxes', () => {
  it('Discord search is not a message box', () => {
    document.body.innerHTML = '<div class="search_abc"><div role="textbox" contenteditable="true" aria-label="Search"></div></div><div role="textbox" contenteditable="true" aria-label="Message #général"></div><div role="combobox"><div role="textbox" contenteditable="true"></div></div>';
    const [search, msg, combo] = [...document.querySelectorAll('[role=textbox]')];
    expect(isMessageBox(search!)).toBe(false);
    expect(isMessageBox(msg!)).toBe(true);
    expect(isMessageBox(combo!)).toBe(false);
  });
});
