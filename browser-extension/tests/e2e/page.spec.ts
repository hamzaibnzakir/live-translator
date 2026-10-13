import { test, expect } from './harness';
import { settle, shot } from './helpers';

type Handle = (m: unknown) => Promise<unknown>;

test.describe.serial('Translate any page', () => {
  test.beforeAll(async ({ bb }) => { await bb.installPacks(['fr-en', 'ja-en', 'es-en']); });

  async function tabIdOf(bb: { sw: import('@playwright/test').Worker }, url: string): Promise<number> {
    return bb.sw.evaluate(async (u) => (await chrome.tabs.query({})).find((t) => t.url === u)!.id!, url);
  }

  test('mixed-language page: translate, follow late content, then Show original restores it exactly', async ({ bb }) => {
    const page = await bb.context.newPage();
    const url = `${bb.fixtureUrl}/mixed.html?t=${Date.now()}`; // unique: the tab is looked up by URL
    await page.goto(url);
    await page.waitForSelector('#fr-late');                 // let the page finish its own late insert first…
    const original = await page.evaluate(() => ({ html: document.body.innerHTML, title: document.title }));
    await page.reload();
    await shot(page, 'page-before');
    const tabId = await tabIdOf(bb, url);
    const toggle = () => bb.sw.evaluate(async (id) => (globalThis as unknown as { __bbHandle: Handle }).__bbHandle({ type: 'bb:page:toggle', tabId: id }), tabId);

    expect(await toggle()).toMatchObject({ active: true });
    await expect(page.locator('#fr1')).toHaveText(/handmade|hand-made|by hand/i);
    await expect(page.locator('#fr2')).toHaveText(/France/);
    await expect(page.locator('#fr2 strong')).toHaveCount(1);              // element structure kept
    await expect(page.locator('#ja1')).toHaveText(/Tokyo/i);
    await expect(page.locator('#es1')).toHaveText(/Spain|free/i);
    await expect(page.locator('#li1')).toHaveText(/free/i);
    await expect(page.locator('nav a').first()).not.toHaveText('Accueil');  // links translated, still links
    await expect(page.locator('nav a')).toHaveCount(3);
    await expect(page.locator('#en1')).toHaveText('This paragraph is already in English and must stay exactly the same.');
    await expect(page.locator('#code1')).toHaveText('const prix = calculerPrix(panier);');
    await expect(page.locator('#code2')).toHaveText('BIENVENUE10');
    await expect(page.locator('#notr')).toHaveText('Ce texte ne doit jamais être traduit.');
    await expect(page.locator('#inp')).toHaveValue('Votre adresse e-mail');
    await expect(page.locator('#ta')).toHaveValue('Écrivez votre message ici');
    await expect(page.locator('#ce')).toHaveText("Texte modifiable par l'utilisateur");
    await expect(page).toHaveTitle(/welcome|store|shop/i);
    // Content that loads after translation started is picked up too.
    await expect(page.locator('#fr-late')).toHaveText(/later|loaded/i);
    await settle(bb.stats);
    await shot(page, 'page-after');

    expect(await toggle()).toMatchObject({ active: false });
    const restored = await page.evaluate(() => ({ html: document.body.innerHTML, title: document.title }));
    expect(restored.title).toBe(original.title);
    // Playwright's own toHaveValue/toHaveText checks leave an empty style="" on the elements they
    // inspected (Brainbox never touches style); ignore exactly that, nothing else.
    const clean = (h: string) => h.replace(/ style=""/g, '');
    expect(clean(restored.html)).toBe(clean(original.html));
    expect(await page.locator('[data-bb-translated]').count()).toBe(0);
    await page.close();
  });

  test('right-click "Translate selection to English" shows a card with Copy', async ({ bb }) => {
    const page = await bb.context.newPage();
    const url = `${bb.fixtureUrl}/mixed.html?t=${Date.now()}`;
    await page.goto(url);
    await bb.context.grantPermissions(['clipboard-read', 'clipboard-write'], { origin: bb.fixtureUrl });
    await page.evaluate(() => { const r = document.createRange(); r.selectNodeContents(document.getElementById('li2')!); getSelection()!.removeAllRanges(); getSelection()!.addRange(r); });
    const tabId = await tabIdOf(bb, url);
    await bb.sw.evaluate(async ({ id, text }) => {
      const g = globalThis as unknown as { __bbContextMenu: (info: object, tab: object) => Promise<void> };
      await g.__bbContextMenu({ menuItemId: 'bb-sel', selectionText: text, frameId: 0 }, { id });
    }, { id: tabId, text: 'Retours acceptés pendant trente jours' });
    const card = page.locator('#brainbox-selection-card .card');
    await expect(card).toContainText(/thirty days|30 days/i);
    await expect(card).toContainText('French → English');
    await shot(page, 'selection-card');
    await card.locator('button.copy').click();
    await expect.poll(() => page.evaluate(() => navigator.clipboard.readText())).toMatch(/thirty days|30 days/i);
    await page.close();
  });
});
