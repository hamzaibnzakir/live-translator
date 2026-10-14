// Renders the Brainbox "B" mark (green) to the extension's PNG icon sizes with Chromium.
import { chromium } from '@playwright/test';
const svg = (s) => `<svg xmlns="http://www.w3.org/2000/svg" width="${s}" height="${s}" viewBox="0 0 24 24"><defs><linearGradient id="g" x1="0" y1="0" x2="0" y2="1"><stop offset="0" stop-color="#2DB866"/><stop offset="1" stop-color="#1A8C4A"/></linearGradient></defs><rect x="0.5" y="0.5" width="23" height="23" rx="6" fill="url(#g)"/><path d="M8 6.5h4.6c2.1 0 3.4 1.1 3.4 2.8 0 1.1-.6 2-1.6 2.4 1.3.4 2.1 1.4 2.1 2.7 0 2-1.5 3.1-3.8 3.1H8z M10.4 8.5v2.6h1.9c.9 0 1.4-.5 1.4-1.3s-.5-1.3-1.4-1.3z M10.4 13v2.6h2.2c1 0 1.6-.5 1.6-1.3s-.6-1.3-1.6-1.3z" fill="#fff" fill-rule="evenodd"/></svg>`;
const browser = await chromium.launch();
const page = await browser.newPage();
for (const s of [16, 32, 48, 128]) {
  await page.setViewportSize({ width: s, height: s });
  await page.setContent(`<html><body style="margin:0;background:transparent">${svg(s)}</body></html>`);
  await page.locator('svg').screenshot({ path: `static/icons/icon-${s}.png`, omitBackground: true });
}
await browser.close();
console.log('icons written');
