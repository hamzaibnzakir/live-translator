import { test } from './harness';
test('slate probe', async ({ bb }) => {
  const page = await bb.context.newPage();
  const selAll = () => { const tb = document.querySelector('[data-slate-editor]') as HTMLElement; tb.focus(); const r = document.createRange(); r.selectNodeContents(tb); const s = getSelection()!; s.removeAllRanges(); s.addRange(r); };
  const paste = (t: string) => { const dt = new DataTransfer(); dt.setData('text/plain', t); document.querySelector('[data-slate-editor]')!.dispatchEvent(new ClipboardEvent('paste', { clipboardData: dt, bubbles: true, cancelable: true })); };
  const scenarios: [string, number, 'paste' | 'insert'][] = [['A wait0 paste', 0, 'paste'], ['B wait30 paste', 30, 'paste'], ['C wait60 paste', 60, 'paste'], ['D wait150 paste', 150, 'paste'], ['E wait0 insertText', 0, 'insert'], ['F wait150 insertText', 150, 'insert']];
  for (const [name, wait, how] of scenarios) {
    for (const rep of [1, 2]) {
      await page.goto('https://discord.com/channels/111/222');
      const editor = page.locator('[data-slate-editor="true"]');
      await editor.click();
      await page.keyboard.type('Hello world');
      if (rep === 2) { await page.evaluate(selAll); await page.waitForTimeout(wait); await page.evaluate(paste, 'First'); await page.waitForTimeout(30); }
      await page.evaluate(selAll);
      await page.waitForTimeout(wait);
      if (how === 'paste') await page.evaluate(paste, 'Bonjour'); else await page.evaluate(() => document.execCommand('insertText', false, 'Bonjour'));
      await page.waitForTimeout(50);
      console.log(`${name} rep${rep}`.padEnd(26), JSON.stringify(await page.evaluate(() => window.__fixture.editorText())), JSON.stringify(await editor.innerText()));
    }
  }
});
