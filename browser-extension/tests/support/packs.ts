// Test helper: keeps a local, verified mirror of the model files the tests need, so CI
// downloads each pack once (cached between runs) and tests run without the network.
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { createServer, type Server } from 'node:http';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';


export const MIRROR_DIR = fileURLToPath(new URL('../.cache/packs', import.meta.url));
export const TEST_PAIRS = ['fr-en', 'ja-en', 'en-fr', 'en-es', 'es-en', 'en-ja'];

const pinned: unknown = JSON.parse(readFileSync(fileURLToPath(new URL('../../src/engines/bergamot/registry.pinned.json', import.meta.url)), 'utf8'));
type Files = Record<string, { path: string; sha256: string; size: number }>;
const pairs = (pinned as unknown as { pairs: Record<string, { files: Files }>; baseUrl: string }).pairs;
const baseUrl = (pinned as unknown as { baseUrl: string }).baseUrl;

export async function ensureMirror(pairList: string[] = TEST_PAIRS): Promise<void> {
  for (const pair of pairList) {
    for (const f of Object.values(pairs[pair]!.files)) {
      const out = join(MIRROR_DIR, f.path);
      if (existsSync(out) && createHash('sha256').update(readFileSync(out)).digest('hex') === f.sha256) continue;
      const res = await fetch(`${baseUrl}/${f.path}`);
      if (!res.ok) throw new Error(`${res.status} ${f.path}`);
      const buf = Buffer.from(await res.arrayBuffer());
      const h = createHash('sha256').update(buf).digest('hex');
      if (h !== f.sha256) throw new Error(`hash mismatch ${f.path}`);
      mkdirSync(dirname(out), { recursive: true });
      writeFileSync(out, buf);
    }
  }
}

/** Serves the mirror like the real bucket (plus CORS, which the extension needs for a non-pinned host). */
export function serveMirror(port = 0): Promise<{ server: Server; url: string; hits: () => number }> {
  let hits = 0;
  const server = createServer((req, res) => {
    const p = join(MIRROR_DIR, decodeURIComponent((req.url ?? '/').split('?')[0]!));
    if (!p.startsWith(MIRROR_DIR) || !existsSync(p)) { res.writeHead(404, { 'Access-Control-Allow-Origin': '*' }).end(); return; }
    hits++;
    const body = readFileSync(p);
    res.writeHead(200, { 'Content-Type': 'application/octet-stream', 'Content-Length': body.length, 'Access-Control-Allow-Origin': '*' }).end(body);
  });
  return new Promise((resolve) => server.listen(port, '127.0.0.1', () => {
    const addr = server.address() as { port: number };
    resolve({ server, url: `http://127.0.0.1:${addr.port}`, hits: () => hits });
  }));
}
