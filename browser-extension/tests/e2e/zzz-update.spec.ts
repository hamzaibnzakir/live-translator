// Runs last: reloads the extension (like installing an update) while Discord stays open.
// Playwright's Chromium does not bring a reloaded --load-extension build back, so this covers the
// old copy's side: it must let go cleanly instead of breaking the page or throwing errors.
import { test, expect } from './harness';
import { content } from './helpers';

test('extension update while Discord is open: the old copy restores the page and asks for a refresh', async ({ bb }) => {
  await bb.installPacks(['fr-en']);
  const page = await bb.context.newPage();
  const errors: string[] = [];
  page.on('pageerror', (e) => errors.push(e.message));
  page.on('console', (m) => { if (m.type() === 'error' && /context invalidated/i.test(m.text())) errors.push(m.text()); });
  await page.goto('https://discord.com/channels/111/222');
  await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
  // The worker dies mid-call, so the evaluate itself may reject.
  await bb.sw.evaluate(() => { setTimeout(() => chrome.runtime.reload(), 50); }).catch(() => undefined);
  // Messages go back to the original text (the new copy would translate them again)…
  await expect(content(page, '900001')).toHaveText(/Bonjour tout le monde/, { timeout: 10_000 });
  // …and one clear notice with a refresh button, not a broken overlay.
  await expect(page.locator('#brainbox-overlay')).toHaveCount(1);
  await expect(page.locator('#brainbox-overlay .notice', { hasText: 'Brainbox was updated' })).toBeVisible({ timeout: 10_000 });
  await expect(page.locator('#brainbox-overlay .notice button', { hasText: 'Refresh now' })).toBeVisible();
  // New messages are left alone and nothing throws.
  const id = await page.evaluate(() => window.__fixture.post('Camille', 'Merci beaucoup pour votre aide hier soir.'));
  await page.waitForTimeout(1500);
  await expect(page.locator(`#message-content-${id}`)).toHaveText('Merci beaucoup pour votre aide hier soir.');
  expect(errors).toEqual([]);
  await page.close();
});
