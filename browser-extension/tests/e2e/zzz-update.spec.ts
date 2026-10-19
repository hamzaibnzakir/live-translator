// Runs last: reloads the extension (like installing an update) while Discord stays open.
import { test, expect } from './harness';
import { content } from './helpers';

test('extension update while Discord is open: the tab keeps translating without a refresh', async ({ bb }) => {
  await bb.installPacks(['fr-en']);
  const page = await bb.context.newPage();
  await page.goto('https://discord.com/channels/111/222');
  await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
  const swReady = bb.context.waitForEvent('serviceworker', { predicate: (w) => w.url().includes(bb.extensionId) });
  await bb.sw.evaluate(() => chrome.runtime.reload());
  const sw = await swReady;
  await sw.evaluate(() => new Promise((r) => setTimeout(r, 500)));
  // A new message after the update is translated by the new copy of Brainbox.
  const id = await page.evaluate(() => window.__fixture.post('Camille', 'Merci beaucoup pour votre aide hier soir.'));
  await expect(page.locator(`li[id$="-${id}"] #message-content-${id}`)).toHaveText(/thank/i, { timeout: 30_000 });
  // Earlier messages still read in English and only one Brainbox overlay exists.
  await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
  await expect(page.locator('#brainbox-overlay')).toHaveCount(1);
  await expect(page.locator('#brainbox-overlay .notice')).toHaveCount(0);
  await page.close();
});
