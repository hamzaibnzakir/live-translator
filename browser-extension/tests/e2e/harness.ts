// Shared Playwright fixtures: Chromium with the unpacked extension, a local pack mirror,
// a fixture web server, and helpers to configure settings / read engine stats.
import { test as base, chromium, type BrowserContext, type Page, type Worker } from '@playwright/test';
import { existsSync, readFileSync } from 'node:fs';
import { createServer, type Server } from 'node:http';
import { extname, join } from 'node:path';
import { fileURLToPath } from 'node:url';
import { serveMirror } from '../support/packs';

export const ROOT = fileURLToPath(new URL('../..', import.meta.url));
const DIST = join(ROOT, 'dist-test');
const FIXTURES = join(ROOT, 'tests/e2e/fixtures');
const TYPES: Record<string, string> = { '.html': 'text/html; charset=utf-8', '.js': 'text/javascript', '.css': 'text/css', '.json': 'application/json', '.png': 'image/png', '.svg': 'image/svg+xml' };

function serveFixtures(): Promise<{ server: Server; url: string }> {
  const server = createServer((req, res) => {
    const path = (req.url ?? '/').split('?')[0]!;
    // Discord-like routes (/channels/<guild>/<channel>) all serve the Discord fixture app.
    const file = path.startsWith('/channels/') ? join(FIXTURES, 'discord.html') : join(FIXTURES, path === '/' ? 'index.html' : path);
    if (!file.startsWith(FIXTURES) || !existsSync(file)) { res.writeHead(404).end('not found'); return; }
    res.writeHead(200, { 'Content-Type': TYPES[extname(file)] ?? 'application/octet-stream' }).end(readFileSync(file));
  });
  return new Promise((r) => server.listen(0, '127.0.0.1', () => r({ server, url: `http://127.0.0.1:${(server.address() as { port: number }).port}` })));
}

export interface Harness {
  context: BrowserContext;
  sw: Worker;
  extensionId: string;
  mirrorUrl: string;
  mirrorHits: () => number;
  fixtureUrl: string;
  configure(patch: Record<string, unknown>): Promise<void>;
  stats(): Promise<{ engineCalls: number; engineTexts: number; cacheHits: number; workers: { pair: string; wasmBytes: number }[] }>;
  resetStats(): Promise<void>;
  installPacks(pairs: string[]): Promise<void>;
  openPopup(): Promise<Page>;
}

export const test = base.extend<{ bb: Harness }, { bbWorker: Harness }>({
  bbWorker: [async ({}, use) => {
    const mirror = await serveMirror();
    const fixtures = await serveFixtures();
    const context = await chromium.launchPersistentContext('', {
      channel: 'chromium',
      headless: !process.env.HEADED,
      viewport: { width: 1280, height: 860 },
      // Content scripts for discord.com run on our fixture host too: we map discord.com to it.
      args: [`--disable-extensions-except=${DIST}`, `--load-extension=${DIST}`],
    });
    // https://discord.com/* is answered from our local fixture (no network, no real Discord).
    await context.route('https://discord.com/**', async (route) => {
      const path = new URL(route.request().url()).pathname;
      const file = path.startsWith('/channels/') ? join(FIXTURES, 'discord.html') : join(FIXTURES, path);
      if (!file.startsWith(FIXTURES) || !existsSync(file)) return route.fulfill({ status: 404, body: 'not found' });
      return route.fulfill({ status: 200, contentType: TYPES[extname(file)] ?? 'application/octet-stream', body: readFileSync(file) });
    });
    let [sw] = context.serviceWorkers();
    if (!sw) sw = await context.waitForEvent('serviceworker');
    const extensionId = new URL(sw.url()).host;
    const send = <T>(msg: Record<string, unknown>): Promise<T> => sw.evaluate(async (m) => {
      // Talk to our own background handlers the same way content scripts do.
      const g = globalThis as unknown as { __bbHandle?: (m: unknown) => Promise<unknown> };
      return g.__bbHandle!(m);
    }, msg) as Promise<T>;
    const h: Harness = {
      context, sw, extensionId, mirrorUrl: mirror.url, mirrorHits: mirror.hits, fixtureUrl: fixtures.url,
      async configure(patch) {
        await sw.evaluate(async ({ patch, base }) => {
          const cur = ((await chrome.storage.local.get('settings')).settings ?? {}) as Record<string, unknown>;
          const merged: Record<string, unknown> = { ...cur, ...patch };
          for (const [k, v] of Object.entries(patch)) if (v && typeof v === 'object' && !Array.isArray(v)) merged[k] = { ...((cur[k] as object) ?? {}), ...v };
          merged.packBaseUrl = (patch.packBaseUrl as string) ?? (cur.packBaseUrl as string) ?? base;
          await chrome.storage.local.set({ settings: merged });
        }, { patch, base: mirror.url });
      },
      stats: () => send({ type: 'bb:stats', target: 'bg' }),
      resetStats: () => send({ type: 'bb:stats:reset', target: 'bg' }),
      async installPacks(pairs) {
        for (const p of pairs) { const [from, to] = p.split('-'); await send({ type: 'bb:packs:download', from, to, target: 'bg' }); }
      },
      async openPopup() {
        const page = await context.newPage();
        await page.goto(`chrome-extension://${extensionId}/popup.html`);
        return page;
      },
    };
    await h.configure({ engine: 'bergamot', packBaseUrl: mirror.url });
    await use(h);
    await context.close();
    mirror.server.close();
    fixtures.server.close();
  }, { scope: 'worker' }],
  bb: async ({ bbWorker }, use) => { await use(bbWorker); },
});

export const expect = test.expect;
