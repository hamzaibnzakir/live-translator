import { test, expect } from './harness';
import { shot } from './helpers';

test.describe.serial('Quick translator (popup)', () => {
  test.beforeAll(async ({ bb }) => { await bb.installPacks(['fr-en', 'en-fr', 'ja-en']); });

  test('paste French → English → copy; then English → French with Ctrl+Enter; swap', async ({ bb }) => {
    // The clipboard is browser-wide; read it from an ordinary page (CDP can't grant extension origins).
    const reader = await bb.context.newPage();
    await reader.goto(`${bb.fixtureUrl}/mixed.html`);
    await bb.context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: bb.fixtureUrl });
    const clipboard = () => reader.evaluate(() => navigator.clipboard.readText());
    const page = await bb.openPopup();
    await page.setViewportSize({ width: 420, height: 600 });
    await page.fill('#src', 'Merci beaucoup pour ton aide, à demain !');
    await expect(page.locator('#out')).toHaveValue(/thank/i);
    await expect(page.locator('#detected')).toContainText('French');
    await page.click('#copy-out');
    const english = await page.inputValue('#out');
    await expect.poll(clipboard).toBe(english);
    await shot(page, 'popup-fr-en');

    // English → French, Ctrl+Enter translates and copies.
    await page.selectOption('#to', 'fr');
    await page.fill('#src', 'See you tomorrow at the tournament.');
    await page.focus('#src');
    await page.keyboard.press('Control+Enter');
    await expect(page.locator('#out')).toHaveValue(/demain/i);
    await expect.poll(clipboard).toMatch(/demain/i);
    await expect(page.locator('#toast')).toContainText('Copied');
    await shot(page, 'popup-en-fr');

    // ⇄ swaps text and direction.
    await page.click('#swap');
    await expect(page.locator('#src')).toHaveValue(/demain/i);
    await expect(page.locator('#to')).toHaveValue('en');
    await expect(page.locator('#out')).toHaveValue(/tomorrow/i);
    await page.close();
    await reader.close();
  });

  test('Japanese → English; a missing pack shows a download button that installs it', async ({ bb }) => {
    await bb.sw.evaluate(() => (globalThis as unknown as { __bbHandle: (m: unknown) => Promise<unknown> }).__bbHandle({ type: 'bb:packs:remove', from: 'es', to: 'en' }));
    const page = await bb.openPopup();
    await page.fill('#src', '明日の夜は時間がありますか？');
    await expect(page.locator('#out')).toHaveValue(/tomorrow/i);
    await page.fill('#src', '¿Alguien quiere jugar esta noche?');
    const dl = page.locator('#pack-need button');
    await expect(dl).toContainText('Spanish → English');
    await dl.click();
    await expect(page.locator('#out')).toHaveValue(/tonight/i);
    await page.close();
  });
});
