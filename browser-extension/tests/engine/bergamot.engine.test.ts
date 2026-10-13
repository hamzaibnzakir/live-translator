// Real Bergamot WASM + real Mozilla models (fr→en, ja→en, en→es, and fr→es via the English pivot).
// Files come from the verified local mirror (tests/.cache/packs, filled from Mozilla's bucket);
// the engine re-verifies every file's SHA-256 exactly like the extension does.
import { afterAll, beforeAll, describe, expect, it } from 'vitest';
import { Worker } from 'node:worker_threads';
import { copyFileSync, mkdirSync, readFileSync, writeFileSync, existsSync } from 'node:fs';
import { join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { BergamotEngine } from '../../src/engines/bergamot/bergamotEngine';
import { MemoryPackStore } from '../../src/engines/bergamot/packStore';
import type { WorkerLike } from '../../src/engines/bergamot/workerClient';
import { detectOffline } from '../../src/engines/detect';
import { ensureMirror, MIRROR_DIR } from '../support/packs';

const ROOT = fileURLToPath(new URL('../..', import.meta.url));
const VENDOR = join(ROOT, 'tests/.cache/bergamot-node');

function nodeWorker(): WorkerLike {
  const w = new Worker(join(VENDOR, 'translator-worker.js'));
  return {
    postMessage: (m, transfer) => w.postMessage(m, transfer as never),
    addEventListener: (type: string, cb: (e: { data: unknown }) => void) => { if (type === 'message') w.on('message', (data) => cb({ data })); else w.on('error', cb); },
    terminate: () => void w.terminate(),
  } as WorkerLike;
}

let engine: BergamotEngine;
const timings: Record<string, number> = {};

beforeAll(async () => {
  await ensureMirror(['fr-en', 'ja-en', 'en-es']);
  // The npm package is "type: module"; its worker is a classic script, so give it a CommonJS home.
  mkdirSync(VENDOR, { recursive: true });
  const src = join(ROOT, 'node_modules/@browsermt/bergamot-translator/worker');
  for (const f of ['translator-worker.js', 'bergamot-translator-worker.js', 'bergamot-translator-worker.wasm']) copyFileSync(join(src, f), join(VENDOR, f));
  writeFileSync(join(VENDOR, 'package.json'), '{"type":"commonjs"}');
  engine = new BergamotEngine({
    store: new MemoryPackStore(),
    createWorker: nodeWorker,
    baseUrl: () => 'https://mirror.local',
    fetcher: async (url) => {
      const p = join(MIRROR_DIR, url.replace('https://mirror.local/', ''));
      return existsSync(p) ? new Response(readFileSync(p)) : new Response('missing', { status: 404 });
    },
    maxWorkers: 4,
  });
  for (const pair of ['fr-en', 'ja-en', 'en-es']) {
    const [f, t] = pair.split('-') as [string, string];
    const t0 = performance.now();
    await engine.ensurePack(f, t);
    timings[`install ${pair}`] = Math.round(performance.now() - t0);
  }
});

afterAll(() => { engine?.dispose(); console.log('engine timings (ms):', JSON.stringify(timings)); });

async function timed(label: string, texts: string[], from: string, to: string): Promise<string[]> {
  const t0 = performance.now();
  const out = await engine.translate(texts, from, to);
  timings[label] = Math.round(performance.now() - t0);
  return out;
}

describe('Bergamot (real WASM, real models)', () => {
  it('fr → en', async () => {
    const [a, b, c] = await timed('fr-en first (incl. model load)', ['Bonjour tout le monde, comment ça va ?', 'Merci beaucoup pour votre aide.', 'Je ne peux pas venir ce soir.'], 'fr', 'en');
    expect(a).toMatch(/hello everyone/i);
    expect(a).toMatch(/how are you/i);
    expect(b).toMatch(/thank you (very much|so much)/i);
    expect(c).toMatch(/can'?t come tonight|cannot come tonight/i);
  });

  it('ja → en', async () => {
    const [a, b] = await timed('ja-en first (incl. model load)', ['こんにちは、元気ですか？', '今日はとても暑いですね。'], 'ja', 'en');
    expect(a).toMatch(/hello/i);
    expect(a).toMatch(/how are you/i);
    expect(b).toMatch(/hot today/i);
  });

  it('en → es', async () => {
    const [a, b] = await timed('en-es first (incl. model load)', ['Good morning, how are you today?', 'Thank you for your help.'], 'en', 'es');
    expect(a).toMatch(/buenos días/i);
    expect(a).toMatch(/cómo estás/i);
    expect(b).toMatch(/gracias/i);
  });

  it('fr → es through the English pivot (two models in one worker)', async () => {
    const [a] = await timed('fr-es pivot', ['Merci beaucoup pour votre aide.'], 'fr', 'es');
    expect(a).toMatch(/gracias/i);
  });

  it('warm latency and throughput (fr → en, 20 chat messages)', async () => {
    const msgs = Array.from({ length: 20 }, (_, i) => `Message numéro ${i} : merci beaucoup pour la partie d'hier soir.`);
    const t0 = performance.now();
    const out = await engine.translate(msgs, 'fr', 'en');
    const ms = performance.now() - t0;
    timings['fr-en 20 msgs batch'] = Math.round(ms);
    timings['fr-en msgs/s'] = Math.round((20 / ms) * 1000);
    expect(out.filter((o) => /thank/i.test(o)).length).toBeGreaterThanOrEqual(18);
    const t1 = performance.now();
    await engine.translate(['Salut, ça va ?'], 'fr', 'en');
    timings['fr-en single warm'] = Math.round(performance.now() - t1);
  });

  it('detection agrees with the engine inputs', () => {
    expect(detectOffline('Bonjour tout le monde, comment ça va ?')?.lang).toBe('fr');
    expect(detectOffline('こんにちは、元気ですか？')?.lang).toBe('ja');
  });
});

describe('Chrome built-in Translator API', () => {
  it('feature-detect (Node has no Translator API: real coverage happens in tests/e2e/chrome-builtin.spec.ts)', () => {
    const g = globalThis as { Translator?: unknown };
    expect(g.Translator).toBeUndefined();
  });
});
