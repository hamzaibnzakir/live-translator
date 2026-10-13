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
  await bb.sw.evaluate(() => undefined);
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
    // Per-frame poll: first time each row is seen, and first time its content is translated.
    const tick = () => {
      const now = performance.now();
      document.querySelectorAll('li[id^="chat-messages-"]').forEach((li) => { w.__perf.mounted[`message-content-${li.id.split('-').pop()}`] ??= now; });
      document.querySelectorAll('li [id^="message-content-"][data-bb-translated="1"]').forEach((el) => { w.__perf.translated[el.id] ??= now; });
      requestAnimationFrame(tick);
    };
    requestAnimationFrame(tick);
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
  const perf1Max = Math.max(...Object.values(perf1.translated));
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
  const perfAll = await page.evaluate(() => (window as unknown as { __perf: { translated: Record<string, number> } }).__perf.translated);
  const rowsTranslatedDuringSweep = Object.values(perfAll).filter((t) => t > perf1Max).length;
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

  // Warm run: packs loaded in RAM, translation cache cleared, fresh page → pure per-message latency.
  await handle({ type: 'bb:cache:clear' });
  const warm = await bb.context.newPage();
  await warm.addInitScript(() => {
    const w = window as unknown as { __w: { m: Record<string, number>; t: Record<string, number> } };
    w.__w = { m: {}, t: {} };
    const tick = () => { const now = performance.now(); document.querySelectorAll('li[id^="chat-messages-"]').forEach((li) => { w.__w.m[`message-content-${li.id.split('-').pop()}`] ??= now; }); document.querySelectorAll('li [id^="message-content-"][data-bb-translated="1"]').forEach((el) => { w.__w.t[el.id] ??= now; }); requestAnimationFrame(tick); };
    requestAnimationFrame(tick);
  });
  await warm.goto('https://discord.com/channels/111/222?count=500&seed=2');
  await expect(warm.locator('[data-bb-translated="1"]').first()).toBeVisible({ timeout: 60_000 });
  await settle(bb.stats, 1500);
  const warmLat = await warm.evaluate(() => {
    const w = (window as unknown as { __w: { m: Record<string, number>; t: Record<string, number> } }).__w;
    const sc = document.querySelector('.scroller__36d07')!.getBoundingClientRect();
    return [...document.querySelectorAll('li [id^="message-content-"][data-bb-translated="1"]')].filter((e) => { const r = e.getBoundingClientRect(); return r.bottom > sc.top && r.top < sc.bottom; }).map((e) => (w.t[e.id] ?? NaN) - (w.m[e.id] ?? NaN)).filter((x) => !Number.isNaN(x));
  });
  // Live chat: new messages arrive one at a time while the channel is open.
  const incoming = ['Quelqu’un veut lancer une partie maintenant ?', 'Je viens de finir mes devoirs, j’arrive.', 'Attends-moi deux minutes, je prends à boire.', 'Le nouveau personnage est beaucoup trop fort.', 'On devrait changer de stratégie pour la prochaine manche.', 'Tu peux m’envoyer le lien du tournoi ?', 'Mon frère va nous rejoindre plus tard.', 'J’ai enfin débloqué le dernier niveau !', 'Il est déjà minuit ici, je vais dormir bientôt.', 'Merci pour les conseils, ça m’a beaucoup aidé.'];
  const live: number[] = [];
  for (const text of incoming) {
    const id = await warm.evaluate((t) => window.__fixture.post('Camille', t), text);
    const ms = await warm.evaluate((mid) => new Promise<number>((res) => {
      const t0 = performance.now();
      const check = () => { const el = document.querySelector(`li[id$="-${mid}"] #message-content-${mid}`); if (el?.getAttribute('data-bb-translated') === '1') res(performance.now() - t0); else requestAnimationFrame(check); };
      check();
    }), id);
    live.push(ms);
    await warm.waitForTimeout(300);
  }
  await warm.close();

  const report = {
    messages: 500,
    packsLoaded: s1.workers.map((w) => w.pair),
    oneTimeModelLoadMs: warmupMs,
    timeToFirstTranslatedMessageMs: Math.round(firstTranslated),
    firstTranslatedAfterFirstRowMountMs: Math.round(firstTranslated - firstMounted),
    visibleMessages: visible.length,
    newMessageLatencyMs: { n: live.length, p50: Math.round(pct(live, 50)), p95: Math.round(pct(live, 95)), max: Math.round(Math.max(...live)), all: live.map(Math.round) },
    warmVisibleMessageLatencyMs: { n: warmLat.length, p50: Math.round(pct(warmLat, 50)), p95: Math.round(pct(warmLat, 95)), max: Math.round(Math.max(...warmLat)) },
    coldVisibleMessageLatencyMs: { p50: Math.round(pct(latencies, 50)), p95: Math.round(pct(latencies, 95)), max: Math.round(Math.max(...latencies)) },
    history: { messagesTranslated: rowsTranslatedDuringSweep, messagesPerSecond: +(rowsTranslatedDuringSweep / (activeMs / 1000)).toFixed(1), engineTexts: translatedTexts, seconds: +(activeMs / 1000).toFixed(1), engineTextsPerSecond: +(translatedTexts / (activeMs / 1000)).toFixed(1), engineCalls: s1.engineCalls - s0.engineCalls, cacheHits: s1.cacheHits - s0.cacheHits },
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
  expect(report.warmVisibleMessageLatencyMs.p50).toBeLessThan(1500);
  expect(report.newMessageLatencyMs.p50).toBeLessThan(300);
});
