import { test, expect } from './harness';
import { content, settle, shot } from './helpers';

const PROTECTED = 'li [id^="message-content-"] :is(.mention, img.emoji, a, code, pre, [class*="spoiler"], time)';

test.describe.serial('Discord reading', () => {
  test.beforeAll(async ({ bb }) => {
    await bb.configure({ discord: { enabled: true } });
    await bb.installPacks(['fr-en', 'ja-en']);
  });

  test('before/after screenshots', async ({ bb }) => {
    await bb.configure({ discord: { enabled: false } });
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toBeVisible();
    await page.waitForTimeout(800);
    await shot(page, 'discord-before');
    await bb.configure({ discord: { enabled: true } });
    await expect(content(page, '900001')).toHaveText(/Hello/);
    await settle(bb.stats);
    await shot(page, 'discord-after');
    await page.close();
  });

  test('visible French and Japanese become English; mentions, emoji, links, code untouched', async ({ bb }) => {
    const page = await bb.context.newPage();
    // Snapshot every protected element exactly as the app rendered it.
    await page.goto('https://discord.com/channels/111/222');
    await bb.configure({ discord: { enabled: false } });
    await page.reload();
    await expect(content(page, '900001')).toBeVisible();
    const before = await page.locator(PROTECTED).evaluateAll((els) => els.map((e) => e.outerHTML));
    expect(before.length).toBeGreaterThan(5);

    await bb.configure({ discord: { enabled: true } });
    await page.reload();
    const t0 = Date.now();
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    console.log(`first visible message translated ${Date.now() - t0} ms after reload`);
    await expect(content(page, '900003')).toHaveText(/hello/i);          // こんにちは…
    await expect(content(page, '900003')).not.toHaveText(/[ぁ-んァ-ン]/);
    await expect(content(page, '900002')).toContainText('@David');
    await expect(content(page, '900002')).toHaveText(/^Hi @David/);
    await expect(content(page, '900005')).toContainText('https://example.com/docs');
    await expect(content(page, '900005')).toContainText('npm install brainbox');
    await expect(content(page, '900006').locator('pre')).toHaveText("const message = 'Bonjour';\nconsole.log(message);");
    await expect(content(page, '900008').locator('[aria-label="Spoiler"]')).toHaveText('le héros meurt à la fin');
    await expect(page.locator('li[id$="-900009"] [class*="embedTitle"]')).toHaveText(/server/i);
    await expect(page.locator('li[id$="-900009"] [class*="embedDescription"]')).toHaveText(/players/i);
    await expect(page.locator('li[id$="-900007"] [id^="message-reply-context-"] [class*="repliedTextContent"]')).toHaveText(/Hello everyone/i);
    // English, emoji-only and URL-only messages are left alone (and not marked).
    await expect(content(page, '900004')).toHaveText('Hey, I just joined this server!');
    await expect(content(page, '900004')).not.toHaveAttribute('data-bb-translated', '1');
    await expect(content(page, '900011')).not.toHaveAttribute('data-bb-translated', '1');
    await expect(content(page, '900012')).not.toHaveAttribute('data-bb-translated', '1');
    // Marker + original kept for the tooltip.
    await expect(content(page, '900001')).toHaveAttribute('data-bb-translated', '1');
    await expect(content(page, '900001')).toHaveAttribute('data-bb-original', 'Bonjour tout le monde, comment ça va ce soir ?');
    await expect(content(page, '900001')).toHaveAttribute('data-bb-lang', 'fr');
    await settle(bb.stats);
    const after = await page.locator(PROTECTED).evaluateAll((els) => els.map((e) => e.outerHTML));
    expect(after).toEqual(before);
    await page.close();
  });

  test('scrolling away and back re-applies from cache with no new engine calls', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    await settle(bb.stats);
    // Tag the current DOM row so we can prove it is a *new* element after scrolling back.
    await page.evaluate(() => { (document.querySelector('li[id$="-900001"]') as HTMLElement).dataset.oldRow = '1'; });
    await page.evaluate(() => window.__fixture.scrollToTop());
    await expect(page.locator('li[id$="-900001"]')).toHaveCount(0);            // unmounted (virtualized)
    await settle(bb.stats);                                                    // history at the top translates
    const snap = await bb.stats();
    await page.evaluate(() => window.__fixture.scrollToBottom());
    const row = page.locator('li[id$="-900001"]');
    await expect(row).toHaveCount(1);
    await expect(row).not.toHaveAttribute('data-old-row', '1');               // a re-mounted element
    // Translation is back immediately (applied in the mutation callback, before paint).
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i, { timeout: 500 });
    await expect(content(page, '900013')).toHaveText(/good idea/i, { timeout: 500 });
    await page.waitForTimeout(1500);
    const later = await bb.stats();
    expect(later.engineCalls).toBe(snap.engineCalls);
    expect(later.engineTexts).toBe(snap.engineTexts);
    await page.close();
  });

  test('editing a message re-translates it', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900013')).toHaveText(/good idea/i);
    await page.evaluate(() => window.__fixture.edit('900013', 'Finalement, je préfère jouer demain matin.'));
    await expect(content(page, '900013')).toHaveText(/tomorrow/i);
    await expect(content(page, '900013')).toContainText('(edited)');
    await expect(content(page, '900013')).toHaveAttribute('data-bb-original', 'Finalement, je préfère jouer demain matin.');
    await page.close();
  });

  test('hover shows the original; Alt+click swaps one message back and forth', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    const c = content(page, '900001');
    await expect(c).toHaveText(/Hello everyone/i);
    await c.hover();
    const tip = page.locator('#brainbox-overlay .tip');
    await expect(tip).toBeVisible();
    await expect(tip).toContainText('Bonjour tout le monde, comment ça va ce soir ?');
    await expect(tip).toContainText('French');
    await shot(page, 'discord-hover-original');
    await c.click({ modifiers: ['Alt'] });
    await expect(c).toHaveText('Bonjour tout le monde, comment ça va ce soir ?');
    await expect(c).toHaveAttribute('data-bb-state', 'original');
    await c.click({ modifiers: ['Alt'] });
    await expect(c).toHaveText(/Hello everyone/i);
    await page.close();
  });
});
