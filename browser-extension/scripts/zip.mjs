// Packs dist/ into brainbox-translate-<version>.zip (load-unpacked / drag-in ready).
import { execFileSync } from 'node:child_process';
import { existsSync, readFileSync, rmSync } from 'node:fs';
import { join } from 'node:path';

const root = new URL('..', import.meta.url).pathname;
const { version } = JSON.parse(readFileSync(join(root, 'package.json'), 'utf8'));
if (!existsSync(join(root, 'dist/manifest.json'))) throw new Error('Run npm run build first');
const out = join(root, `brainbox-translate-${version}.zip`);
rmSync(out, { force: true });
execFileSync('zip', ['-r', '-q', '-X', out, '.', '-x', '*.map'], { cwd: join(root, 'dist'), stdio: 'inherit' });
console.log(out);
