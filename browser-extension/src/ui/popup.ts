// Quick translator (popup + side panel): paste → detect (or pick the language) → translate → copy.
import { ChromeEngine } from '../engines/chromeEngine';
import { LANGUAGES, languageLabel, languageName } from '../shared/languages';
import { send, type EngineStats, type PackProgressEvent, type TranslateResult } from '../shared/messages';
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
  t.hidden = false;
  setTimeout(() => (t.hidden = true), 1600);
}

const fmtMB = (n: number): string => `${Math.max(1, Math.round(n / 1e6))} MB`;
const engineName = (id?: string): string => (id === 'bergamot' ? 'Offline engine' : id === 'chrome' ? 'Chrome built-in' : id ?? '');

function fillSelect(sel: HTMLSelectElement, selected: string, withAuto: boolean): void {
  const recent = [settings.targetLang, ...settings.recentLangs].filter((c, i, a) => a.indexOf(c) === i);
  const opt = (code: string): string => `<option value="${code}"${code === selected ? ' selected' : ''}>${languageLabel(code)}</option>`;
  sel.innerHTML = (withAuto ? `<option value="auto"${selected === 'auto' ? ' selected' : ''}>✦ Detect language</option>` : '')
    + `<optgroup label="Recent">${recent.map(opt).join('')}</optgroup><optgroup label="All languages">${LANGUAGES.filter((l) => !recent.includes(l.code)).map((l) => opt(l.code)).join('')}</optgroup>`;
}

async function copy(text: string, what = 'Copied'): Promise<void> {
  if (!text) return;
  try { await navigator.clipboard.writeText(text); }
  catch { const ta = document.createElement('textarea'); ta.value = text; document.body.appendChild(ta); ta.select(); document.execCommand('copy'); ta.remove(); }
  toast(`${what} ✓`);
}

function showError(title: string, body: string, fix?: { label: string; run: () => void }): void {
  const box = $('error-box');
  box.hidden = false;
  box.innerHTML = `<div class="t"></div><div class="muted b"></div>${fix ? '<div class="row"><button class="small" id="fix"></button></div>' : ''}`;
  (box.querySelector('.t') as HTMLElement).textContent = title;
  (box.querySelector('.b') as HTMLElement).textContent = body;
  if (fix) { const b = $('fix'); b.textContent = fix.label; b.onclick = fix.run; }
}

async function translateNow(andCopy = false): Promise<void> {
  const text = $<HTMLTextAreaElement>('src').value;
  const to = $<HTMLSelectElement>('to').value;
  const fromSel = $<HTMLSelectElement>('from').value;
  const mine = ++seq;
  $('pack-need').hidden = true;
  $('error-box').hidden = true;
  $('count').textContent = text.length ? `${text.length}` : '';
  if (!text.trim()) { $<HTMLTextAreaElement>('out').value = ''; $('detected').textContent = 'Auto-detect'; return; }
  $('out-pane').classList.add('loading');
  try {
    let from = fromSel;
    if (fromSel === 'auto') {
      const d = await send<Detection | null>({ type: 'bb:detect', text });
      detected = d?.lang ?? null;
      from = detected ?? 'auto';
      $('detected').textContent = detected ? `Detected: ${languageName(detected)}${d && d.confidence < 0.5 ? ' (unsure)' : ''}` : 'Language unclear — pick it above';
    } else {
      detected = fromSel;
      $('detected').textContent = languageName(fromSel);
    }
    const lines = text.split('\n');
    const items = lines.map((t, i) => ({ id: `q${i}`, text: t, from }));
    const res = await send<TranslateResult[]>({ type: 'bb:translate', items, to, priority: 0, purpose: 'quick' });
    if (mine !== seq) return;
    const need = res.find((r) => r.status === 'needs-pack');
    if (need?.pack) { showPackNeed(need); return; }
    const err = res.find((r) => r.status === 'error' && r.reason !== 'cancelled');
    const out = res.map((r, i) => (r.status === 'ok' ? r.translation ?? '' : lines[i] ?? '')).join('\n');
    $<HTMLTextAreaElement>('out').value = out;
    const pane = $('out-pane'); pane.classList.remove('fresh'); void pane.offsetWidth; pane.classList.add('fresh');
    const used = res.find((r) => r.engine);
    const fell = res.find((r) => r.failedEngine);
    if (err) {
      showError('Translation failed', err.reason ?? 'Unknown error', { label: 'Use the offline engine', run: async () => { settings = await saveSettings((s) => ({ ...s, engine: 'bergamot' })); void translateNow(); void refreshChip(); } });
      $('engine-line').textContent = '';
    } else if (res.every((r) => r.status === 'skipped' && r.reason === 'same-language')) {
      $('engine-line').textContent = `Already ${languageName(to)}`;
    } else {
      $('engine-line').textContent = used ? `${engineName(used.engine)}${fell ? ` · ${engineName(fell.failedEngine)} failed` : ''}` : '';
    }
    if (fell) void refreshChip();
    if (andCopy) await copy(out, `Copied ${languageName(to)}`);
    if (to !== settings.targetLang) settings = await saveSettings((s) => ({ ...s, recentLangs: pushRecent(s.recentLangs, to) }));
  } catch (e) {
    showError('Brainbox engine not reachable', (e as Error).message);
  } finally {
    if (mine === seq) $('out-pane').classList.remove('loading');
  }
}

function showPackNeed(r: TranslateResult): void {
  const p = r.pack!;
  const box = $('pack-need');
  box.hidden = false;
  const fell = r.failedEngine === 'chrome' ? '<div class="muted" style="margin-bottom:6px">Chrome’s built-in translator failed here, so Brainbox uses its offline engine.</div>' : '';
  box.innerHTML = `${fell}<div class="t">${languageName(p.from)} → ${languageName(p.to)} pack needed</div>
    <div class="muted">${p.sizeBytes ? `${fmtMB(p.sizeBytes)}, ` : ''}downloaded once, then it works offline.</div>
    <div class="row"><button class="small" id="dl">Download ${languageName(p.from)} → ${languageName(p.to)}</button></div><div class="bar" style="margin-top:8px" hidden><i></i></div>`;
  $('dl').onclick = async () => {
    ($('dl') as HTMLButtonElement).disabled = true;
    (box.querySelector('.bar') as HTMLElement).hidden = false;
    try {
      if (p.engine === 'chrome') {
        // Chrome only downloads its on-device model from a user gesture in the calling page.
        await new ChromeEngine().ensurePack(p.from, p.to, ({ loaded, total }) => { (box.querySelector('.bar > i') as HTMLElement).style.width = `${Math.round((loaded / (total || 1)) * 100)}%`; });
        await send({ type: 'bb:packs:chrome-installed', from: p.from, to: p.to });
      } else {
        await send({ type: 'bb:packs:download', from: p.from, to: p.to });
      }
      box.hidden = true;
      void translateNow();
    } catch (e) {
      box.insertAdjacentHTML('beforeend', `<div class="err" style="margin-top:6px">${(e as Error).message}</div>`);
      ($('dl') as HTMLButtonElement).disabled = false;
    }
  };
}

async function refreshChip(): Promise<void> {
  const chip = $('engine-chip');
  try {
    const s = await send<EngineStats>({ type: 'bb:stats' });
    const chromeFail = s.failures?.find((f) => f.engine === 'chrome');
    if (settings.engine === 'bergamot') { chip.textContent = 'Offline'; chip.className = 'chip ok'; chip.title = 'Using Mozilla Bergamot, fully on this computer.'; }
    else if (chromeFail) { chip.textContent = 'Offline (Chrome failed)'; chip.className = 'chip warn'; chip.title = `Chrome built-in translator: “${chromeFail.reason}”. Using the offline engine instead.`; }
    else { chip.textContent = 'On-device'; chip.className = 'chip ok'; chip.title = 'Auto: Chrome built-in when it works, otherwise the offline engine.'; }
  } catch { chip.textContent = 'Engine starting…'; chip.className = 'chip'; }
}

async function setupPageTab(): Promise<void> {
  const [tab] = await chrome.tabs.query({ active: true, currentWindow: true });
  let origin = '';
  try { origin = tab?.url ? new URL(tab.url).origin : ''; } catch { /* chrome:// etc. */ }
  const web = /^https?:/.test(origin);
  const isDiscord = origin === 'https://discord.com';
  $('site-line').textContent = web ? origin.replace(/^https?:\/\//, '') : 'Browser pages can’t be translated.';
  ($('page-toggle') as HTMLButtonElement).disabled = !web || isDiscord;
  $<HTMLInputElement>('site-always').checked = settings.sites.always.includes(origin);
  $<HTMLInputElement>('site-never').checked = settings.sites.never.includes(origin);
  if (isDiscord) {
    $('discord-card').hidden = false;
    $('page-hint').textContent = 'On Discord, Brainbox translates messages as you read, so this button isn’t needed.';
    const en = $<HTMLInputElement>('discord-enabled'), mk = $<HTMLInputElement>('discord-marker');
    en.checked = settings.discord.enabled;
    mk.checked = settings.discord.marker;
    en.onchange = () => { void saveSettings((s) => ({ ...s, discord: { ...s.discord, enabled: en.checked } })); };
    mk.onchange = () => { void saveSettings((s) => ({ ...s, discord: { ...s.discord, marker: mk.checked } })); };
    document.querySelector<HTMLButtonElement>('.tabs [data-tab="page"]')!.textContent = 'Discord';
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
  fillSelect($<HTMLSelectElement>('from'), 'auto', true);
  fillSelect($<HTMLSelectElement>('to'), settings.targetLang, false);
  const src = $<HTMLTextAreaElement>('src');
  src.addEventListener('input', () => { clearTimeout(timer); timer = window.setTimeout(() => void translateNow(), 350); });
  src.addEventListener('keydown', (e) => { if (e.key === 'Enter' && (e.ctrlKey || e.metaKey)) { e.preventDefault(); clearTimeout(timer); void translateNow(true); } });
  $('to').addEventListener('change', () => void translateNow());
  $('from').addEventListener('change', () => void translateNow());
  $('translate').onclick = () => void translateNow();
  $('copy-out').onclick = () => void copy($<HTMLTextAreaElement>('out').value);
  $('copy-src').onclick = () => void copy(src.value);
  $('clear').onclick = () => { src.value = ''; void translateNow(); src.focus(); };
  $('swap').onclick = () => {
    const out = $<HTMLTextAreaElement>('out').value;
    const to = $<HTMLSelectElement>('to').value;
    const from = $<HTMLSelectElement>('from').value;
    const next = from !== 'auto' ? from : detected && detected !== to ? detected : settings.recentLangs.find((c) => c !== to) ?? 'en';
    src.value = out || src.value;
    fillSelect($<HTMLSelectElement>('to'), next, false);
    fillSelect($<HTMLSelectElement>('from'), from === 'auto' ? 'auto' : to, true);
    detected = to;
    void translateNow();
  };
  document.querySelectorAll<HTMLButtonElement>('.tabs button').forEach((b) => b.addEventListener('click', () => {
    document.querySelectorAll('.tabs button').forEach((x) => { x.classList.toggle('on', x === b); x.setAttribute('aria-selected', String(x === b)); });
    $('tab-quick').hidden = b.dataset.tab !== 'quick';
    $('tab-page').hidden = b.dataset.tab !== 'page';
  }));
  $('open-options').onclick = () => void chrome.runtime.openOptionsPage();
  $('open-packs').onclick = (e) => { e.preventDefault(); void chrome.tabs.create({ url: chrome.runtime.getURL('options.html#packs') }); };
  document.getElementById('open-panel')?.addEventListener('click', async () => {
    const w = await chrome.windows.getCurrent();
    if (w.id !== undefined) await chrome.sidePanel.open({ windowId: w.id });
    window.close();
  });
  chrome.runtime.onMessage.addListener((m: PackProgressEvent) => {
    if (m.type !== 'bb:packs:progress') return;
    const bar = document.querySelector<HTMLElement>('#pack-need .bar > i');
    if (bar && m.total) bar.style.width = `${Math.round((m.loaded / m.total) * 100)}%`;
  });
  void refreshChip();
  void setupPageTab();
  src.focus();
}

void init();
