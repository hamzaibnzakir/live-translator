// Performance on a 500-message Discord-like channel with three packs (fr, ja, es → en) loaded.
import { execSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';
import { test, expect, ROOT } from '../e2e/harness';
import { settle } from '../e2e/helpers';

type Handle = (m: unknown) => Promise<unknown>;
const pct = (a: number[], p: number) => { const s = [...a].sort((x, y) => x - y); return s.length ? s[Math.min(s.length - 1, Math.floor((p / 100) * s.length))]! : NaN; };

function rssByType(): { extensionMB: number; rendererMB: number; totalMB: number } {
  const out = execSync('ps -eo rss=,args=', { encoding: 'utf8' }).split('\n').filter((l) => /chrom/i.test(l));
  let ext = 0, rend = 0, total = 0;
  for (const l of out) {
    const kb = Number(l.trim().split(/\s+/)[0]) || 0;
    total += kb;
    if (l.includes('--extension-process')) ext += kb; else if (l.includes('--type=renderer')) rend += kb;
  }
  return { extensionMB: Math.round(ext / 1024), rendererMB: Math.round(rend / 1024), totalMB: Math.round(total / 1024) };
}

test('500 messages: first visible translation, per-message latency, throughput, long tasks, memory', async ({ bb }) => {
  await bb.configure({ discord: { enabled: true } });
  await bb.installPacks(['fr-en', 'ja-en', 'es-en']);
  const handle = (m: unknown) => bb.sw.evaluate((x) => (globalThis as unknown as { __bbHandle: Handle }).__bbHandle(x), m);
  await handle({ type: 'bb:cache:clear' });
  // Warm the three lanes (model load is a one-time cost per session, reported separately).
  const w0 = Date.now();
  await handle({ type: 'bb:translate', items: [{ id: 'w1', text: 'Préchauffage du moteur de traduction.', from: 'fr' }, { id: 'w2', text: 'エンジンを準備しています。', from: 'ja' }, { id: 'w3', text: 'Calentando el motor de traducción.', from: 'es' }], to: 'en' });
  const warmupMs = Date.now() - w0;
  await handle({ type: 'bb:stats:reset' });
  const memIdle = rssByType();

  const page = await bb.context.newPage();
  await page.addInitScript(() => {
    const w = window as unknown as { __perf: { longTasks: number[]; mounted: Record<string, number>; translated: Record<string, number> } };
    w.__perf = { longTasks: [], mounted: {}, translated: {} };
    new PerformanceObserver((l) => l.getEntries().forEach((e) => w.__perf.longTasks.push(e.duration))).observe({ type: 'longtask', buffered: true });
    const mo = new MutationObserver((recs) => {
      const now = performance.now();
      for (const r of recs) {
        if (r.type === 'attributes') { const el = r.target as Element; if (el.id.startsWith('message-content-') && el.getAttribute('data-bb-translated') === '1') w.__perf.translated[el.id] ??= now; continue; }
        r.addedNodes.forEach((n) => { if (n instanceof Element) (n.matches('li[id^="chat-messages-"]') ? [n] : [...n.querySelectorAll('li[id^="chat-messages-"]')]).forEach((li) => { const id = `message-content-${li.id.split('-').pop()}`; w.__perf.mounted[id] ??= now; }); });
      }
    });
    document.addEventListener('DOMContentLoaded', () => mo.observe(document.body, { childList: true, subtree: true, attributes: true, attributeFilter: ['data-bb-translated'] }));
  });
  await page.goto('https://discord.com/channels/111/222?count=500');
  await expect(page.locator('[data-bb-translated="1"]').first()).toBeVisible({ timeout: 60_000 });
  await settle(bb.stats, 1500);

  const visible = await page.evaluate(() => {
    const sc = document.querySelector('.scroller__36d07')!.getBoundingClientRect();
    return [...document.querySelectorAll('li [id^="message-content-"][data-bb-translated="1"]')].filter((e) => { const r = e.getBoundingClientRect(); return r.bottom > sc.top && r.top < sc.bottom; }).map((e) => e.id);
  });
  const perf1 = await page.evaluate(() => (window as unknown as { __perf: { longTasks: number[]; mounted: Record<string, number>; translated: Record<string, number> } }).__perf);
  const latencies = visible.map((id) => (perf1.translated[id] ?? NaN) - (perf1.mounted[id] ?? NaN)).filter((x) => !Number.isNaN(x));
  const firstTranslated = Math.min(...Object.values(perf1.translated));
  const firstMounted = Math.min(...Object.values(perf1.mounted));

  // Throughput: scroll through the whole history like a user catching up.
  const s0 = await bb.stats();
  const t0 = Date.now();
  let lastChange = Date.now(), last = s0.engineTexts;
  for (let i = 0; i < 80; i++) {
    const atTop = await page.evaluate(() => { const sc = document.querySelector('.scroller__36d07')!; sc.scrollTop = Math.max(0, sc.scrollTop - sc.clientHeight); return sc.scrollTop === 0; });
    await page.waitForTimeout(250);
    const n = (await bb.stats()).engineTexts;
    if (n !== last) { last = n; lastChange = Date.now(); }
    if (atTop) break;
  }
  await settle(bb.stats, 1500, 120_000);
  const s1 = await bb.stats();
  const activeMs = Math.max(lastChange, Date.now() - 1500) - t0;
  const translatedTexts = s1.engineTexts - s0.engineTexts;
  const memLoaded = rssByType();
  const cdp = await bb.context.newCDPSession(page);
  await cdp.send('Performance.enable');
  const metrics = Object.fromEntries((await cdp.send('Performance.getMetrics')).metrics.map((m) => [m.name, m.value]));
  const perf2 = await page.evaluate(() => (window as unknown as { __perf: { longTasks: number[] } }).__perf.longTasks);
  const idleCpu = await page.evaluate(async () => {
    // Content-script idle cost: main-thread busy time over 3 s with nothing happening.
    let busy = 0, last = performance.now();
    const end = last + 3000;
    while (performance.now() < end) { await new Promise((r) => setTimeout(r, 50)); const now = performance.now(); busy += Math.max(0, now - last - 50); last = now; }
    return busy;
  });

  const report = {
    messages: 500,
    packsLoaded: s1.workers.map((w) => w.pair),
    oneTimeModelLoadMs: warmupMs,
    timeToFirstTranslatedMessageMs: Math.round(firstTranslated),
    firstTranslatedAfterFirstRowMountMs: Math.round(firstTranslated - firstMounted),
    visibleMessages: visible.length,
    visibleMessageLatencyMs: { p50: Math.round(pct(latencies, 50)), p95: Math.round(pct(latencies, 95)), max: Math.round(Math.max(...latencies)) },
    history: { translatedTexts, seconds: +(activeMs / 1000).toFixed(1), translationsPerSecond: +(translatedTexts / (activeMs / 1000)).toFixed(1), engineCalls: s1.engineCalls - s0.engineCalls, cacheHits: s1.cacheHits - s0.cacheHits },
    longTasks: { count: perf2.length, totalMs: Math.round(perf2.reduce((a, b) => a + b, 0)), maxMs: Math.round(Math.max(0, ...perf2)) },
    idleMainThreadBusyMsPer3s: Math.round(idleCpu),
    memory: {
      pageJsHeapMB: Math.round((metrics.JSHeapUsedSize ?? 0) / 1048576),
      extensionProcessRssMB_before: memIdle.extensionMB,
      extensionProcessRssMB_with3packs: memLoaded.extensionMB,
      wasmHeapReservedMB: s1.workers.map((w) => `${w.pair}:${Math.round(w.wasmBytes / 1048576)}`),
      allChromiumRssMB: memLoaded.totalMB,
    },
  };
  console.log(JSON.stringify(report, null, 2));
  mkdirSync(join(ROOT, 'reports'), { recursive: true });
  writeFileSync(join(ROOT, 'reports', 'perf.json'), JSON.stringify(report, null, 2));
  expect(visible.length).toBeGreaterThan(3);
  expect(report.visibleMessageLatencyMs.p50).toBeLessThan(1500);
});
