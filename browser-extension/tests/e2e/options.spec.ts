import { test, expect } from './harness';
import { shot } from './helpers';

test('settings page: pack manager lists, downloads and removes packs; settings persist', async ({ bb }) => {
  const page = await bb.context.newPage();
  await page.goto(`chrome-extension://${bb.extensionId}/options.html#packs`);
  await page.fill('#pack-search', 'Italian');
  const row = page.locator('tr[data-lang="it"]');
  await expect(row).toBeVisible();
  const dl = row.locator('button[data-dl="it-en"]');
  await expect(dl).toContainText('MB');
  await expect(row.locator('button[data-offline="it"]')).toBeVisible();
  // Download/remove with a pair the local mirror has: Spanish.
  await page.fill('#pack-search', 'Spanish');
  const es = page.locator('tr[data-lang="es"]');
  if (await es.locator('button[data-rm="es-en"]').count()) await es.locator('button[data-rm="es-en"]').click();
  await expect(es.locator('button[data-dl="es-en"]')).toBeVisible();
  await es.locator('button[data-dl="es-en"]').click();
  await expect(es.locator('td').nth(1)).toContainText('✓');
  await expect(page.locator('#storage-line')).toContainText('offline pack');
  await shot(page, 'options-packs');
  await es.locator('button[data-rm="es-en"]').click();
  await expect(es.locator('button[data-dl="es-en"]')).toBeVisible();
  await es.locator('button[data-dl="es-en"]').click();
  await expect(es.locator('td').nth(1)).toContainText('✓');

  // A setting change is stored and reaches the Discord content script.
  await page.goto(`chrome-extension://${bb.extensionId}/options.html#discord`);
  await page.locator('[data-k="discord.marker"]').uncheck();
  await expect.poll(() => bb.sw.evaluate(async () => ((await chrome.storage.local.get('settings')).settings as { discord: { marker: boolean } }).discord.marker)).toBe(false);
  const discord = await bb.context.newPage();
  await discord.goto('https://discord.com/channels/111/222');
  await expect(discord.locator('html')).toHaveAttribute('data-bb-marker', 'off');
  await page.locator('[data-k="discord.marker"]').check();
  await expect(discord.locator('html')).toHaveAttribute('data-bb-marker', 'on');
  await shot(page, 'options-discord');
  await discord.close();
  await page.close();
});
