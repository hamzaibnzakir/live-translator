// Downloads one pinned model pair, verifying every file against its pinned SHA-256 (compressed)
// and, where Mozilla publishes one, the decompressed model hash too.
import type { PinnedFile, PinnedPair } from './registry';

export type Fetcher = (url: string) => Promise<Response>;

export async function sha256Hex(data: ArrayBuffer | Uint8Array): Promise<string> {
  const view = data instanceof Uint8Array ? data : new Uint8Array(data);
  const digest = await crypto.subtle.digest('SHA-256', view as BufferSource);
  return [...new Uint8Array(digest)].map((b) => b.toString(16).padStart(2, '0')).join('');
}

export async function gunzip(data: Uint8Array): Promise<ArrayBuffer> {
  const stream = new Response(data as BodyInit).body!.pipeThrough(new DecompressionStream('gzip'));
  return new Response(stream).arrayBuffer();
}

async function readWithProgress(res: Response, onBytes: (n: number) => void): Promise<Uint8Array> {
  if (!res.body) { const b = new Uint8Array(await res.arrayBuffer()); onBytes(b.byteLength); return b; }
  const chunks: Uint8Array[] = [];
  let total = 0;
  const reader = res.body.getReader();
  for (;;) {
    const { done, value } = await reader.read();
    if (done) break;
    chunks.push(value);
    total += value.byteLength;
    onBytes(value.byteLength);
  }
  const out = new Uint8Array(total);
  let p = 0;
  for (const c of chunks) { out.set(c, p); p += c.byteLength; }
  return out;
}

export class IntegrityError extends Error {}

export async function downloadFile(baseUrl: string, file: PinnedFile, fetcher: Fetcher, onBytes: (n: number) => void): Promise<ArrayBuffer> {
  const url = `${baseUrl.replace(/\/$/, '')}/${file.path}`;
  const res = await fetcher(url);
  if (!res.ok) throw new Error(`Download failed (${res.status}) for ${file.path}`);
  const gz = await readWithProgress(res, onBytes);
  if (gz.byteLength !== file.size) throw new IntegrityError(`Size mismatch for ${file.path}: expected ${file.size} bytes, got ${gz.byteLength}`);
  const hash = await sha256Hex(gz);
  if (hash !== file.sha256) throw new IntegrityError(`SHA-256 mismatch for ${file.path}: expected ${file.sha256}, got ${hash}`);
  const raw = await gunzip(gz);
  if (file.uncompressedSha256) {
    const h2 = await sha256Hex(raw);
    if (h2 !== file.uncompressedSha256) throw new IntegrityError(`Decompressed SHA-256 mismatch for ${file.path}`);
  }
  return raw;
}

export async function downloadPair(
  baseUrl: string,
  pair: PinnedPair,
  fetcher: Fetcher,
  onProgress?: (loaded: number, total: number) => void,
): Promise<Record<string, ArrayBuffer>> {
  const entries = Object.entries(pair.files).filter((e): e is [string, PinnedFile] => !!e[1]);
  const total = entries.reduce((s, [, f]) => s + f.size, 0);
  let loaded = 0;
  const files = await Promise.all(entries.map(async ([kind, f]) => [kind, await downloadFile(baseUrl, f, fetcher, (n) => { loaded += n; onProgress?.(loaded, total); })] as const));
  return Object.fromEntries(files);
}
