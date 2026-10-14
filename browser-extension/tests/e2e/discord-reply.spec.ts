import { test, expect } from './harness';
import { content, shot } from './helpers';

test.describe.serial('Discord replying', () => {
  test.beforeAll(async ({ bb }) => {
    await bb.configure({ discord: { enabled: true, replyHotkey: 'Ctrl+Shift+Y', backTranslationPreview: true } });
    await bb.installPacks(['fr-en', 'en-fr', 'ja-en']);
  });

  test('hotkey rewrites the English draft into the channel language; preview, undo, and Slate sends the translation', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    // The channel is mostly French → the pill shows the detected main language.
    const pill = page.locator('#brainbox-overlay .pill');
    await expect(pill).toContainText('FR');

    const editor = page.locator('[data-slate-editor="true"]');
    await editor.click();
    await page.keyboard.type('Thank you very much, I will join the game tonight.');
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText())).toBe('Thank you very much, I will join the game tonight.');

    await page.evaluate(() => { const w = window as unknown as { __states: string[] }; w.__states = []; const tick = () => { w.__states.push(window.__fixture.editorText()); if (w.__states.length < 400) requestAnimationFrame(tick); }; tick(); });
    await page.keyboard.press('Control+Shift+Y');
    // Slate's *model* (what Discord would send) now holds French.
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText()), { timeout: 30_000 }).toMatch(/merci/i);
    const french = await page.evaluate(() => window.__fixture.editorText());
    await expect(editor).toHaveText(french);
    // Every model state seen was either the English draft or the final French: never a mix.
    const states = await page.evaluate(() => [...new Set((window as unknown as { __states: string[] }).__states)]);
    expect(states.every((s) => s === french || s === 'Thank you very much, I will join the game tonight.')).toBe(true);
    const preview = page.locator('#brainbox-overlay .preview');
    await expect(preview.locator('[data-bb="they"]')).toHaveText(french);
    await expect(preview.locator('[data-bb="back"]')).toHaveText(/thank you/i, { timeout: 30_000 });
    await shot(page, 'discord-reply-preview');

    // Undo restores the English draft (Ctrl+Z)…
    await page.keyboard.press('Control+z');
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText())).toBe('Thank you very much, I will join the game tonight.');
    // …and the hotkey again re-translates.
    await page.keyboard.press('Control+Shift+Y');
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText()), { timeout: 30_000 }).toBe(french);

    // Nothing was sent automatically.
    expect(await page.evaluate(() => window.__fixture.sent.length)).toBe(0);
    // The user presses Enter; the fixture sends from Slate's state.
    await editor.click();
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    await expect.poll(() => page.evaluate(() => window.__fixture.sent.at(-1))).toBe(french);
    await page.close();
  });

  test('mentions, :emoji:, links and `code` in a draft survive translation; the button works too', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    const editor = page.locator('[data-slate-editor="true"]');
    await editor.click();
    const draft = 'Hey @Hugo, please read https://example.com/rules and run `npm test` before the match :smile:';
    await page.keyboard.type(draft);
    await page.locator('#brainbox-overlay [data-bb="reply"]').click();
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText()), { timeout: 30_000 }).not.toBe(draft);
    const out = await page.evaluate(() => window.__fixture.editorText());
    for (const token of ['@Hugo', 'https://example.com/rules', '`npm test`', ':smile:']) expect(out).toContain(token);
    expect(out).not.toMatch(/please read/i);
    // The button again = undo.
    await page.locator('#brainbox-overlay [data-bb="reply"]').click();
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText())).toBe(draft);
    expect(await page.evaluate(() => window.__fixture.sent.length)).toBe(0);
    await page.close();
  });

  test('language override from the dropdown (search by name)', async ({ bb }) => {
    await bb.installPacks(['en-es', 'es-en']);
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    await page.locator('#brainbox-overlay [data-bb="lang"]').click();
    await page.locator('#brainbox-overlay .menu input').fill('spani');
    await page.locator('#brainbox-overlay .menu .opt').first().click();
    await expect(page.locator('#brainbox-overlay .pill')).toContainText('ES');
    const editor = page.locator('[data-slate-editor="true"]');
    await editor.click();
    await page.keyboard.type('Good morning, how are you today?');
    await page.keyboard.press('Control+Shift+Y');
    await expect.poll(() => page.evaluate(() => window.__fixture.editorText()), { timeout: 30_000 }).toMatch(/buenos días/i);
    await page.close();
  });
});

test.describe.serial('Discord replying in any message box', () => {
  test.beforeAll(async ({ bb }) => { await bb.installPacks(['fr-en', 'en-fr']); });

  test('after you send, the pill goes back from "Undo" to "Reply in FR"', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    const pill = page.locator('#brainbox-overlay .pill');
    await page.locator('[data-slate-editor="true"]').click();
    await page.keyboard.type('See you tomorrow at the tournament.');
    await page.keyboard.press('Control+Shift+Y');
    await expect(pill).toContainText('Undo', { timeout: 30_000 });
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    await expect.poll(() => page.evaluate(() => window.__fixture.sent.at(-1))).toMatch(/demain/i);
    await expect(pill).toContainText('Reply in FR');
    await expect(page.locator('#brainbox-overlay .preview')).toBeHidden();
    // Editing the rewritten draft by hand also ends the undo state.
    await page.keyboard.type('Hello my friends, how are you?');
    await page.keyboard.press('Control+Shift+Y');
    await expect(pill).toContainText('Undo', { timeout: 30_000 });
    await page.keyboard.type(' !!');
    await expect(pill).toContainText('Reply in FR');
    await page.close();
  });

  test('works in the DM box of a profile popout; the channel box is untouched', async ({ bb }) => {
    const page = await bb.context.newPage();
    await page.goto('https://discord.com/channels/111/222');
    await expect(content(page, '900001')).toHaveText(/Hello everyone/i);
    await page.evaluate(() => window.__fixture.openProfile());
    const dm = page.locator('[role="dialog"] [data-slate-editor="true"]');
    await dm.click();
    await page.keyboard.type('Thank you very much for your help yesterday.');
    // The pill moved to the popout's box.
    const pillBox = await page.locator('#brainbox-overlay .pill').boundingBox();
    const dmBox = await dm.boundingBox();
    expect(Math.abs((pillBox!.y + pillBox!.height) - dmBox!.y)).toBeLessThan(60);
    await page.keyboard.press('Control+Shift+Y');
    await expect.poll(() => page.evaluate(() => window.__fixture.dmText()), { timeout: 30_000 }).toMatch(/merci/i);
    expect(await page.evaluate(() => window.__fixture.editorText())).toBe('');
    const french = await page.evaluate(() => window.__fixture.dmText());
    await page.keyboard.press('End');
    await page.keyboard.press('Enter');
    await expect.poll(() => page.evaluate(() => window.__fixture.dmSent.at(-1))).toBe(french);
    expect(await page.evaluate(() => window.__fixture.sent.length)).toBe(0);
    await expect(page.locator('#brainbox-overlay .pill')).toContainText('Reply in FR');
    await page.close();
  });
});
