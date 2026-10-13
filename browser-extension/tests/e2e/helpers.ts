import type { Page } from '@playwright/test';
import { mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { ROOT } from './harness';

export const SHOTS = join(ROOT, 'reports', 'screenshots');
mkdirSync(SHOTS, { recursive: true });

export const content = (page: Page, id: string) => page.locator(`li[id$="-${id}"] #message-content-${id}`);

/** Waits until engine stats stop changing (background translation settled). */
export async function settle(stats: () => Promise<{ engineTexts: number }>, quietMs = 1200, maxMs = 60_000): Promise<number> {
  const end = Date.now() + maxMs;
  let last = (await stats()).engineTexts, since = Date.now();
  while (Date.now() < end) {
    await new Promise((r) => setTimeout(r, 200));
    const now = (await stats()).engineTexts;
    if (now !== last) { last = now; since = Date.now(); }
    else if (Date.now() - since >= quietMs) return now;
  }
  return last;
}

export async function shot(page: Page, name: string): Promise<void> {
  await page.screenshot({ path: join(SHOTS, `${name}.png`) });
}
