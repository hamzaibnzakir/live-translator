// Pins a versioned snapshot of Mozilla's Firefox Translations model registry into the extension.
// For every pair we pick one model (desktop release preferred), stream each compressed file through
// SHA-256 and record size + hash, so the extension can verify every byte it downloads.
//
//   node scripts/pin-models.mjs            # all English-hub pairs
//   node scripts/pin-models.mjs fr-en ja-en
import { createHash } from 'node:crypto';
import { writeFileSync, readFileSync, existsSync } from 'node:fs';

const REGISTRY = 'https://storage.googleapis.com/moz-fx-translations-data--303e-prod-translations-data/db/models.json';
const OUT = new URL('../src/engines/bergamot/registry.pinned.json', import.meta.url);
const STATUS_RANK = { 'Release Desktop': 0, Release: 1, 'Release Android': 2, Nightly: 4 };
const rank = (m) => STATUS_RANK[m.releaseStatus] ?? 3;

const registry = await (await fetch(REGISTRY)).json();
const only = process.argv.slice(2);
const previous = existsSync(OUT) ? JSON.parse(readFileSync(OUT, 'utf8')) : { pairs: {} };

async function hashUrl(url) {
  const res = await fetch(url);
  if (!res.ok) throw new Error(`${res.status} ${url}`);
  const h = createHash('sha256');
  let size = 0;
  for await (const chunk of res.body) { h.update(chunk); size += chunk.length; }
  return { sha256: h.digest('hex'), size };
}

const jobs = [];
for (const [pair, models] of Object.entries(registry.models)) {
  if (only.length && !only.includes(pair)) continue;
  const best = [...models].sort((a, b) => rank(a) - rank(b))[0];
  if (rank(best) >= 4) continue; // nightly-only pairs are not shipped
  jobs.push([pair, best]);
}

const pairs = { ...previous.pairs };
let done = 0;
async function worker() {
  while (jobs.length) {
    const [pair, m] = jobs.shift();
    const prevPair = previous.pairs[pair];
    const files = {};
    for (const [kind, f] of Object.entries(m.files)) {
      const prevFile = prevPair?.files?.[kind];
      const pinned = prevFile && prevFile.path === f.path ? prevFile : { path: f.path, ...(await hashUrl(`${registry.baseUrl}/${f.path}`)) };
      if (f.uncompressedHash) { pinned.uncompressedSha256 = f.uncompressedHash; pinned.uncompressedSize = f.uncompressedSize; }
      files[kind] = pinned;
    }
    pairs[pair] = { from: m.sourceLanguage, to: m.targetLanguage, architecture: m.architecture, releaseStatus: m.releaseStatus ?? null, files };
    console.log(`[${++done}] ${pair} ${m.architecture} ${(Object.values(files).reduce((s, f) => s + f.size, 0) / 1e6).toFixed(1)} MB`);
  }
}
await Promise.all(Array.from({ length: 6 }, worker));

const sorted = Object.fromEntries(Object.entries(pairs).sort(([a], [b]) => a.localeCompare(b)));
writeFileSync(OUT, JSON.stringify({
  source: REGISTRY,
  baseUrl: registry.baseUrl,
  registryGenerated: registry.generated,
  pinnedAt: new Date().toISOString(),
  license: 'Models: Mozilla Firefox Translations (MPL-2.0 / see NOTICE)',
  pairs: sorted,
}, null, 1) + '\n');
console.log('pinned', Object.keys(sorted).length, 'pairs');
