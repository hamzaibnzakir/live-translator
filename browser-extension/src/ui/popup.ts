// Quick translator (popup + side panel): paste → auto-detect → translate → copy.
import { ChromeEngine } from '../engines/chromeEngine';
import { LANGUAGES, languageLabel, languageName } from '../shared/languages';
import { send, type PackProgressEvent, type TranslateResult } from '../shared/messages';
import type { Detection } from '../engines/types';
import { loadSettings, pushRecent, saveSettings, type Settings } from '../shared/settings';

const $ = <T extends HTMLElement = HTMLElement>(id: string): T => document.getElementById(id) as T;
let settings: Settings;
let detected: string | null = null;
let timer = 0;
let seq = 0;

function toast(msg: string): void {
  const t = $('toast');
  t.textContent = msg;
  t.style.display = 'block';
  setTimeout(() => (t.style.display = 'none'), 1600);
}

function fmtMB(n: number): string { return `${Math.max(1, Math.round(n / 1e6))} MB`; }

function fillTargets(selected: string): void {
  const sel = $<HTMLSelectElement>('to');
  const recent = [settings.targetLang, ...settings.recentLangs].filter((c, i, a) => a.indexOf(c) === i);
  const opt = (code: string): string => `<option value="${code}"${code === selected ? ' selected' : ''}>${languageLabel(code)}</option>`;
  sel.innerHTML = `<optgroup label="Recent">${recent.map(opt).join('')}</optgroup><optgroup label="All languages">${LANGUAGES.filter((l) => !recent.includes(l.code)).map((l) => opt(l.code)).join('')}</optgroup>`;
}

async function copy(text: string): Promise<void> {
  if (!text) return;
  try { await navigator.clipboard.writeText(text); }
  catch { const ta = document.createElement('textarea'); ta.value = text; document.body.appendChild(ta); ta.select(); document.execCommand('copy'); ta.remove(); }
  toast(`Copied ✓`);
}

async function translateNow(andCopy = false): Promise<void> {
  const text = $<HTMLTextAreaElement>('src').value;
  const to = $<HTMLSelectElement>('to').value;
  const mine = ++seq;
  $('pack-need').style.display = 'none';
  if (!text.trim()) { $<HTMLTextAreaElement>('out').value = ''; $('detected').textContent = 'auto'; return; }
  const d = await send<Detection | null>({ type: 'bb:detect', text });
  detected = d?.lang ?? null;
  $('detected').textContent = detected ? `${languageLabel(detected)}${d && d.confidence < 0.5 ? ' ?' : ''}` : 'unknown';
  const lines = text.split('\n');
  const items = lines.map((t, i) => ({ id: `q${i}`, text: t, from: detected ?? 'auto' }));
  const res = await send<TranslateResult[]>({ type: 'bb:translate', items, to, priority: 0, purpose: 'quick' });
  if (mine !== seq) return;
  const need = res.find((r) => r.status === 'needs-pack');
  if (need?.pack) { showPackNeed(need); return; }
  const err = res.find((r) => r.status === 'error');
  const out = res.map((r, i) => (r.status === 'ok' ? r.translation ?? '' : lines[i] ?? '')).join('\n');
  $<HTMLTextAreaElement>('out').value = out;
  const engine = res.find((r) => r.engine)?.engine;
  $('engine-line').textContent = err ? `⚠ ${err.reason}` : res.every((r) => r.status === 'skipped' && r.reason === 'same-language') ? `Already ${languageName(to)}` : engine ? `${engine === 'bergamot' ? 'Offline (Bergamot)' : 'Chrome built-in'} · on-device` : 'Ctrl+Enter = translate & copy';
  if (andCopy) await copy(out);
  if (to !== settings.targetLang) settings = await saveSettings((s) => ({ ...s, recentLangs: pushRecent(s.recentLangs, to) }));
}

function showPackNeed(r: TranslateResult): void {
  const p = r.pack!;
  const box = $('pack-need');
  box.style.display = 'block';
  box.innerHTML = `<div class="muted">Needs the ${languageName(p.from)} → ${languageName(p.to)} pack${p.sizeBytes ? ` (${fmtMB(p.sizeBytes)})` : ''} — downloaded once, then works offline.</div>
    <div class="row" style="margin-top:6px"><button class="small" id="dl">Download ${languageName(p.from)} → ${languageName(p.to)} pack</button></div><div class="bar" style="margin-top:6px;display:none"><i></i></div>`;
  $('dl').onclick = async () => {
    ($('dl') as HTMLButtonElement).disabled = true;
    (box.querySelector('.bar') as HTMLElement).style.display = 'block';
    try {
      if (p.engine === 'chrome') {
        // Chrome only downloads its on-device model from a user gesture in the calling page.
        await new ChromeEngine().ensurePack(p.from, p.to, ({ loaded, total }) => { (box.querySelector('.bar > i') as HTMLElement).style.width = `${Math.round((loaded / (total || 1)) * 100)}%`; });
        await send({ type: 'bb:packs:chrome-installed', from: p.from, to: p.to });
      } else {
        await send({ type: 'bb:packs:download', from: p.from, to: p.to });
      }
      box.style.display = 'none';
      void translateNow();
    } catch (e) {
      box.insertAdjacentHTML('beforeend', `<div class="err">${(e as Error).message}</div>`);
      ($('dl') as HTMLButtonElement).disabled = false;
    }
  };
}

async function setupPageTab(): Promise<void> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  let origin = '';
  try { origin = tab?.url ? new URL(tab.url).origin : ''; } catch { /* chrome:// etc. */ }
  const web = /^https?:/.test(origin);
  $('site-line').textContent = web ? origin : 'This page can’t be translated (browser page).';
  ($('page-toggle') as HTMLButtonElement).disabled = !web || origin === 'https://discord.com';
  $<HTMLInputElement>('site-always').checked = settings.sites.always.includes(origin);
  $<HTMLInputElement>('site-never').checked = settings.sites.never.includes(origin);
  if (origin === 'https://discord.com') {
    $('discord-card').style.display = 'block';
    $('page-hint').textContent = 'Discord is handled by the dedicated Discord mode.';
    $<HTMLInputElement>('discord-enabled').checked = settings.discord.enabled;
    $<HTMLInputElement>('discord-enabled').onchange = (e) => { void saveSettings((s) => ({ ...s, discord: { ...s.discord, enabled: (e.target as HTMLInputElement).checked } })); };
  }
  if (tab?.id && web) {
    chrome.tabs.sendMessage(tab.id, { type: 'bb:tab:page-status' }).then((st: { active: boolean } | undefined) => {
      if (st?.active) $('page-toggle').textContent = 'Show original';
    }).catch(() => undefined);
  }
  $('page-toggle').onclick = async () => {
    if (!tab?.id) return;
    const r = await send<{ active: boolean } | undefined>({ type: 'bb:page:toggle', tabId: tab.id });
    $('page-toggle').textContent = r?.active ? 'Show original' : 'Translate this page';
  };
  $<HTMLInputElement>('site-always').onchange = async (e) => {
    const on = (e.target as HTMLInputElement).checked;
    if (on) {
      // Optional host permission, granted per site.
      const granted = await chrome.permissions.request({ origins: [`${origin}/*`] });
      if (!granted) { (e.target as HTMLInputElement).checked = false; return; }
      await send({ type: 'bb:page:always', origin, tabId: tab?.id });
      $<HTMLInputElement>('site-never').checked = false;
    } else {
      settings = await saveSettings((s) => ({ ...s, sites: { ...s.sites, always: s.sites.always.filter((o) => o !== origin) } }));
    }
  };
  $<HTMLInputElement>('site-never').onchange = async (e) => {
    if ((e.target as HTMLInputElement).checked) { await send({ type: 'bb:page:never', origin }); $<HTMLInputElement>('site-always').checked = false; }
    else settings = await saveSettings((s) => ({ ...s, sites: { ...s.sites, never: s.sites.never.filter((o) => o !== origin) } }));
  };
}

async function init(): Promise<void> {
  settings = await loadSettings();
  fillTargets(settings.targetLang);
  const src = $<HTMLTextAreaElement>('src');
  src.addEventListener('input', () => { clearTimeout(timer); timer = window.setTimeout(() => void translateNow(), 350); });
  src.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); clearTimeout(timer); void translateNow(true); } });
  $('to').addEventListener('change', () => void translateNow());
  $('translate').onclick = () => void translateNow();
  $('copy-out').onclick = () => void copy($<HTMLTextAreaElement>('out').value);
  $('copy-src').onclick = () => void copy(src.value);
  $('swap').onclick = () => {
    const out = $<HTMLTextAreaElement>('out').value;
    const to = $<HTMLSelectElement>('to').value;
    const next = detected && detected !== to ? detected : settings.recentLangs.find((c) => c !== to) ?? 'en';
    src.value = out || src.value;
    fillTargets(next);
    detected = to;
    void translateNow();
  };
  document.querySelectorAll<HTMLButtonElement>('.tabs button').forEach((b) => b.addEventListener('click', () => {
    document.querySelectorAll('.tabs button').forEach((x) => x.classList.toggle('on', x === b));
    $('tab-quick').style.display = b.dataset.tab === 'quick' ? 'block' : 'none';
    $('tab-page').style.display = b.dataset.tab === 'page' ? 'block' : 'none';
  }));
  $('open-options').onclick = () => void chrome.runtime.openOptionsPage();
  $('open-packs').onclick = (e) => { e.preventDefault(); void chrome.tabs.create({ url: chrome.runtime.getURL('options.html#packs') }); };
  $('open-panel')?.addEventListener('click', async () => {
    const w = await chrome.windows.getCurrent();
    if (w.id !== undefined) await chrome.sidePanel.open({ windowId: w.id });
    window.close();
  });
  chrome.runtime.onMessage.addListener((m: PackProgressEvent) => {
    if (m.type !== 'bb:packs:progress') return;
    const bar = document.querySelector<HTMLElement>('#pack-need .bar > i');
    if (bar && m.total) bar.style.width = `${Math.round((m.loaded / m.total) * 100)}%`;
  });
  const s = await send<{ engineCalls: number }>({ type: 'bb:stats' }).catch(() => null);
  $('status-line').textContent = s ? 'On-device · nothing leaves your PC' : '';
  void setupPageTab();
  src.focus();
}

void init();
