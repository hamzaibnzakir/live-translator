// Bundles the extension into dist/ with esbuild and copies static files + the vendored
// Bergamot WASM runtime (unmodified, MPL-2.0) from node_modules.
import * as esbuild from 'esbuild';
import { cpSync, mkdirSync, readFileSync, rmSync, writeFileSync, copyFileSync } from 'node:fs';
import { join } from 'node:path';

const root = new URL('..', import.meta.url).pathname;
const dist = join(root, process.argv.includes('--test') ? 'dist-test' : 'dist');
const watch = process.argv.includes('--watch');
// --test: build used by the Playwright suite (adds localhost access for fixture pages + a test hook).
const testBuild = process.argv.includes('--test');
const pkg = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));

rmSync(dist, { recursive: true, force: true });
mkdirSync(dist, { recursive: true });
cpSync(join(root, 'static'), dist, { recursive: true });
const manifest = JSON.parse(readFileSync(join(root, 'static/manifest.json'), 'utf8'));
manifest.version = pkg.version;
if (testBuild) { manifest.host_permissions.push('http://127.0.0.1/*'); manifest.name += ' (test build)'; }
writeFileSync(join(dist, 'manifest.json'), JSON.stringify(manifest, null, 2));

const bergamot = join(root, 'node_modules/@browsermt/bergamot-translator/worker');
for (const f of ['translator-worker.js', 'bergamot-translator-worker.js', 'bergamot-translator-worker.wasm']) copyFileSync(join(bergamot, f), join(dist, 'vendor/bergamot', f));
copyFileSync(join(root, 'NOTICE'), join(dist, 'NOTICE'));

const common = { bundle: true, sourcemap: 'linked', target: 'chrome120', logLevel: 'info', legalComments: 'none', define: { __BB_VERSION__: JSON.stringify(pkg.version), __BB_TEST__: String(testBuild) } };
const builds = [
  { entryPoints: { background: 'src/background/sw.ts' }, format: 'esm' },
  { entryPoints: { offscreen: 'src/offscreen/offscreen.ts' }, format: 'iife' },
  { entryPoints: { 'content-discord': 'src/content/discord/index.ts' }, format: 'iife' },
  { entryPoints: { 'content-page': 'src/content/page/index.ts' }, format: 'iife' },
  { entryPoints: { popup: 'src/ui/popup.ts', options: 'src/ui/options.ts' }, format: 'iife' },
];
const ctxs = await Promise.all(builds.map((b) => esbuild.context({ ...common, ...b, absWorkingDir: root, outdir: dist })));
if (watch) { await Promise.all(ctxs.map((c) => c.watch())); console.log('watching…'); }
else { await Promise.all(ctxs.map((c) => c.rebuild())); await Promise.all(ctxs.map((c) => c.dispose())); }
