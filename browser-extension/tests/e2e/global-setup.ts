import { execSync } from 'node:child_process';
import { ensureMirror } from '../support/packs';

export default async function globalSetup(): Promise<void> {
  if (!process.env.BB_SKIP_BUILD) execSync('node scripts/build.mjs --test', { stdio: 'inherit' });
  execSync('node tests/e2e/fixtures/build-fixtures.mjs', { stdio: 'inherit' });
  await ensureMirror();
}
