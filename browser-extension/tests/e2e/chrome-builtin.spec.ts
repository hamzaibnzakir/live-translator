// Chrome's built-in Translator/LanguageDetector: feature-detect in every context we rely on,
// and run a real translation only if this browser can actually provide the on-device model.
import { writeFileSync, mkdirSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect } from './harness';
import { ROOT } from './harness';

test('Chrome built-in Translator API: availability in content script, offscreen document and extension page', async ({ bb }, testInfo) => {
  const page = await bb.context.newPage();
  await page.goto(`${bb.fixtureUrl}/mixed.html`);
  const mainWorld = await page.evaluate(async () => ({
    Translator: typeof (self as { Translator?: unknown }).Translator,
    LanguageDetector: typeof (self as { LanguageDetector?: unknown }).LanguageDetector,
  }));
  const offscreen = await bb.sw.evaluate(() => (globalThis as unknown as { __bbHandle: (m: unknown) => Promise<unknown> }).__bbHandle({ type: 'bb:availability', from: 'fr', to: 'en' })) as { chrome: string; detector: string };
  const ext = await bb.openPopup();
  const extPage = await ext.evaluate(async () => {
    const T = (self as unknown as { Translator?: { availability(o: object): Promise<string> } }).Translator;
    return { Translator: typeof T, availability: T ? await T.availability({ sourceLanguage: 'fr', targetLanguage: 'en' }) : 'n/a' };
  });
  await page.close();
  const browserVersion = bb.context.browser()?.version() ?? (await ext.evaluate(() => navigator.userAgent));
  const result = { browserVersion, mainWorld, offscreen, extensionPage: extPage, testedAt: new Date().toISOString() };
  mkdirSync(join(ROOT, 'reports'), { recursive: true });
  writeFileSync(join(ROOT, 'reports', 'chrome-builtin.json'), JSON.stringify(result, null, 2));
  console.log('Chrome built-in API probe:', JSON.stringify(result));
  await ext.close();
  expect(extPage.Translator).toBe('function');

  if (offscreen.chrome !== 'ready') {
    testInfo.annotations.push({ type: 'not-tested', description: `Chrome built-in translation could not be run here: availability is "${offscreen.chrome}" (this Chromium build cannot download Google's on-device model). The wrapper logic is unit-tested with a mocked API.` });
    test.skip(true, `Translator availability is "${offscreen.chrome}" — on-device model not obtainable in this browser`);
  }
  await bb.configure({ engine: 'chrome' });
  const [r] = await bb.sw.evaluate(() => (globalThis as unknown as { __bbHandle: (m: unknown) => Promise<unknown> }).__bbHandle({ type: 'bb:translate', items: [{ id: 'c', text: 'Bonjour tout le monde', from: 'fr' }], to: 'en' })) as { engine: string; translation: string }[];
  expect(r!.engine).toBe('chrome');
  expect(r!.translation).toMatch(/hello/i);
  await bb.configure({ engine: 'bergamot' });
});
