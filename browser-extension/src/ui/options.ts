import { ChromeEngine } from '../engines/chromeEngine';
import { LANGUAGES, languageLabel, languageName, searchLanguages } from '../shared/languages';
import { send, type PackInfo, type PackProgressEvent } from '../shared/messages';
import { DEFAULT_SETTINGS, loadSettings, saveSettings, type Settings } from '../shared/settings';
import { formatHotkey } from '../shared/hotkey';

const $ = <T extends HTMLElement = HTMLElement>(id: string): T => document.getElementById(id) as T;
let settings: Settings;
let packs: PackInfo[] = [];
const chromeAvail = new Map<string, string>();
const progress = new Map<string, number>();
const chromeEngine = new ChromeEngine();

const mb = (n?: number): string => (n ? `${Math.max(1, Math.round(n / 1e6))} MB` : '');
function toast(msg: string): void { const t = $('toast'); t.textContent = msg; t.style.display = 'block'; setTimeout(() => (t.style.display = 'none'), 1800); }

function getPath(o: unknown, path: string): unknown { return path.split('.').reduce<unknown>((a, k) => (a as Record<string, unknown>)?.[k], o); }
function setPath(o: Settings, path: string, v: unknown): Settings {
  const copy = structuredClone(o) as unknown as Record<string, unknown>;
  const keys = path.split('.');
  let cur = copy;
  for (const k of keys.slice(0, -1)) cur = cur[k] as Record<string, unknown>;
  cur[keys.at(-1)!] = v;
  return copy as unknown as Settings;
}

async function save(fn: (s: Settings) => Settings): Promise<void> { settings = await saveSettings(fn); }

function packCell(pair: string): string {
  const p = packs.find((x) => x.pair === pair);
  if (!p) return '<span class="faint">—</span>';
  const pr = progress.get(pair);
  if (pr !== undefined) return `<div class="bar" style="width:90px"><i style="width:${Math.round(pr * 100)}%"></i></div>`;
  if (p.installed) return `<span class="tag ok">✓ ${mb(p.installedBytes)}</span> <button class="ghost small" data-rm="${pair}" title="Remove">✕</button>`;
  return `<button class="ghost small" data-dl="${pair}">Download ${mb(p.sizeBytes)}</button>`;
}

function renderPacks(): void {
  const t = settings.targetLang;
  document.querySelectorAll('.tl').forEach((e) => (e.textContent = languageName(t)));
  const q = $<HTMLInputElement>('pack-search').value;
  const langs = searchLanguages(q).filter((l) => l.code !== 'en' && (packs.some((p) => p.from === l.code || p.to === l.code)));
  $('packs-table').querySelector('tbody')!.innerHTML = langs.map((l) => {
    const inPair = `${l.code}-en`, outPair = `en-${l.code}`;
    const ch = chromeAvail.get(l.code);
    const chCell = ch === undefined ? '<span class="faint">…</span>' : ch === 'ready' ? '<span class="tag ok">✓ ready</span>' : ch === 'unsupported' ? '<span class="faint">n/a</span>' : `<button class="ghost small" data-chrome="${l.code}">Download</button>`;
    const both = packs.filter((p) => p.pair === inPair || p.pair === outPair);
    const offline = both.length && both.every((p) => p.installed);
    return `<tr data-lang="${l.code}"><td>${l.flag} ${l.name} <span class="faint">${l.native}</span></td><td>${packCell(inPair)}</td><td>${packCell(outPair)}</td><td>${chCell}</td>
      <td>${offline ? '<span class="tag ok">Offline ✓</span>' : `<button class="small" data-offline="${l.code}" title="Download both directions">Make fully offline</button>`}</td></tr>`;
  }).join('');
  const installed = packs.filter((p) => p.installed);
  $('storage-line').textContent = `${installed.length} offline pack${installed.length === 1 ? '' : 's'} · ${mb(installed.reduce((s, p) => s + (p.installedBytes ?? 0), 0)) || '0 MB'}`;
}

async function download(pair: string): Promise<void> {
  const [from, to] = pair.split('-') as [string, string];
  progress.set(pair, 0);
  renderPacks();
  try { await send({ type: 'bb:packs:download', from, to }); toast(`${languageName(from)} → ${languageName(to)} installed ✓`); }
  catch (e) { toast(`Download failed: ${(e as Error).message}`); }
  progress.delete(pair);
  await refreshPacks();
}

async function downloadChrome(code: string): Promise<void> {
  // Runs inside the click handler: Chrome requires a user gesture to start the model download.
  try {
    const p1 = chromeEngine.ensurePack(code, 'en');
    const p2 = chromeEngine.ensurePack('en', code);
    await Promise.all([p1, p2]);
    await send({ type: 'bb:packs:chrome-installed', from: code, to: 'en' });
    chromeAvail.set(code, 'ready');
    toast(`Chrome built-in ${languageName(code)} ready ✓`);
  } catch (e) { toast(`Chrome download failed: ${(e as Error).message}`); }
  renderPacks();
}

async function refreshPacks(): Promise<void> {
  packs = await send<PackInfo[]>({ type: 'bb:packs:list' });
  renderPacks();
}

async function refreshChrome(): Promise<void> {
  const status: string[] = [];
  if (!chromeEngine.supported()) status.push('Chrome built-in Translator API: not available in this browser → using offline Bergamot.');
  else {
    status.push('Chrome built-in Translator API: available.');
    const det = await chromeEngine.detectorAvailability();
    status.push(`Language Detector: ${det}.`);
  }
  $('engine-status').textContent = status.join(' ');
  for (const l of LANGUAGES) {
    if (l.code === 'en') continue;
    chromeAvail.set(l.code, chromeEngine.supported() ? await chromeEngine.availability(l.code, 'en') : 'unsupported');
  }
  renderPacks();
}

function renderLists(): void {
  const rules = [
    ...Object.entries(settings.discord.servers).map(([k, v]) => ({ kind: 'servers' as const, k, v, label: `Server ${k}` })),
    ...Object.entries(settings.discord.channels).map(([k, v]) => ({ kind: 'channels' as const, k, v, label: `Channel ${k}` })),
  ];
  $('discord-rules').innerHTML = rules.length ? rules.map((r) => `<div class="row" style="padding:3px 0"><span class="grow">${r.label}</span><span class="tag">${r.v}</span><button class="ghost small" data-rule="${r.kind}|${r.k}">Forget</button></div>`).join('') : 'None yet.';
  const site = (list: string[], kind: 'always' | 'never'): string => list.length ? list.map((o) => `<div class="row" style="padding:3px 0"><span class="grow">${o}</span><button class="ghost small" data-site="${kind}|${o}">Remove</button></div>`).join('') : 'None';
  $('sites-always').innerHTML = site(settings.sites.always, 'always');
  $('sites-never').innerHTML = site(settings.sites.never, 'never');
}

function bindGeneral(): void {
  $<HTMLSelectElement>('target').innerHTML = LANGUAGES.map((l) => `<option value="${l.code}"${l.code === settings.targetLang ? ' selected' : ''}>${languageLabel(l.code)}</option>`).join('');
  $<HTMLSelectElement>('target').onchange = (e) => { void save((s) => ({ ...s, targetLang: (e.target as HTMLSelectElement).value })).then(renderPacks); };
  $<HTMLSelectElement>('engine').value = settings.engine;
  $<HTMLSelectElement>('engine').onchange = (e) => void save((s) => ({ ...s, engine: (e.target as HTMLSelectElement).value as Settings['engine'] }));
  const bool = (id: keyof Settings): void => {
    const el = $<HTMLInputElement>(id);
    el.checked = settings[id] as boolean;
    el.onchange = () => void save((s) => ({ ...s, [id]: el.checked }));
  };
  bool('autoDownloadPacks');
  const num = (id: 'minConfidence' | 'cacheMaxEntries'): void => {
    const el = $<HTMLInputElement>(id);
    el.value = String(settings[id]);
    el.onchange = () => void save((s) => ({ ...s, [id]: Number(el.value) || DEFAULT_SETTINGS[id] }));
  };
  num('minConfidence');
  num('cacheMaxEntries');
  document.querySelectorAll<HTMLInputElement | HTMLSelectElement>('[data-k]').forEach((el) => {
    const k = el.dataset.k!;
    if (el instanceof HTMLInputElement && el.type === 'checkbox') { el.checked = !!getPath(settings, k); el.onchange = () => void save((s) => setPath(s, k, el.checked)); }
    else { el.value = String(getPath(settings, k)); el.onchange = () => void save((s) => setPath(s, k, el.value)); }
  });
  const hk = $<HTMLInputElement>('hotkey');
  hk.value = settings.discord.replyHotkey;
  hk.onfocus = () => { hk.value = 'Press keys…'; };
  hk.onblur = () => { hk.value = settings.discord.replyHotkey; };
  hk.onkeydown = (e) => {
    e.preventDefault();
    const s = formatHotkey(e);
    if (!s || !/[+]/.test(s)) return;
    void save((st) => setPath(st, 'discord.replyHotkey', s)).then(() => { hk.value = s; hk.blur(); toast(`Reply hotkey: ${s}`); });
  };
  $('hotkey-reset').onclick = () => void save((st) => setPath(st, 'discord.replyHotkey', DEFAULT_SETTINGS.discord.replyHotkey)).then(() => { hk.value = settings.discord.replyHotkey; });

  // AI polish
  $<HTMLInputElement>('polish-enabled').checked = settings.polish.enabled;
  $<HTMLInputElement>('polish-endpoint').value = settings.polish.endpoint;
  $<HTMLInputElement>('polish-model').value = settings.polish.model;
  const savePolish = async (enabled: boolean): Promise<void> => {
    const endpoint = $<HTMLInputElement>('polish-endpoint').value.trim() || DEFAULT_SETTINGS.polish.endpoint;
    if (enabled) {
      const origin = new URL(endpoint).origin;
      const ok = await chrome.permissions.request({ origins: [`${origin}/*`] });
      if (!ok) { $<HTMLInputElement>('polish-enabled').checked = false; enabled = false; }
    }
    await save((s) => ({ ...s, polish: { enabled, endpoint, model: $<HTMLInputElement>('polish-model').value.trim() } }));
  };
  $<HTMLInputElement>('polish-enabled').onchange = (e) => void savePolish((e.target as HTMLInputElement).checked);
  $('polish-endpoint').onchange = () => void savePolish($<HTMLInputElement>('polish-enabled').checked);
  $('polish-model').onchange = () => void savePolish($<HTMLInputElement>('polish-enabled').checked);
  $('polish-lmstudio').onclick = () => { $<HTMLInputElement>('polish-endpoint').value = 'http://localhost:1234/v1'; void savePolish($<HTMLInputElement>('polish-enabled').checked); };
  $('polish-ollama').onclick = () => { $<HTMLInputElement>('polish-endpoint').value = 'http://localhost:11434/v1'; void savePolish($<HTMLInputElement>('polish-enabled').checked); };
  $('polish-test').onclick = async () => {
    $('polish-result').textContent = 'Testing…';
    try {
      const r = await send<{ text: string; model: string }>({ type: 'bb:polish', text: 'Bonjour à tous, ça roule ?', from: 'fr', to: settings.targetLang });
      $('polish-result').textContent = `✓ ${r.model}: ${r.text}`;
    } catch (e) { $('polish-result').textContent = `✗ ${(e as Error).message}`; }
  };

  // Clipboard mode needs clipboardRead, requested only when turned on.
  $<HTMLInputElement>('clipboardMode').checked = settings.clipboardMode;
  $<HTMLInputElement>('clipboardMode').onchange = async (e) => {
    const el = e.target as HTMLInputElement;
    if (el.checked && !(await chrome.permissions.request({ permissions: ['clipboardRead'] }))) el.checked = false;
    await save((s) => ({ ...s, clipboardMode: el.checked }));
  };
  $('clear-cache').onclick = async () => { await send({ type: 'bb:cache:clear' }); toast('Translation cache cleared ✓'); };
}

interface CheckRow { availability: string; ok: boolean; output?: string; error?: string; ms?: number }
async function engineCheck(): Promise<void> {
  const out = $('engine-check-out');
  out.hidden = false;
  out.innerHTML = '<div><b>Checking…</b><span class="muted">This can take a few seconds.</span></div>';
  const row = (name: string, r: CheckRow): string => {
    const state = r.ok ? `<span class="tag ok">✓ works · ${r.ms} ms</span> <span class="muted">“${r.output}”</span>` : r.error ? `<span class="tag warn">✗ ${r.availability}, but failed</span> <span class="err">${r.error}</span>` : `<span class="tag">${r.availability}</span>`;
    return `<div><b>${name}</b><span>${state}</span></div>`;
  };
  // 1) Chrome's API in this (visible) page — it needs your click if the model still has to download.
  let here: CheckRow = { availability: 'unsupported', ok: false };
  if (chromeEngine.supported()) {
    here = { availability: await chromeEngine.availability('fr', 'en'), ok: false };
    if (here.availability === 'ready') {
      const t0 = performance.now();
      try { const [o] = await chromeEngine.translate(['Bonjour tout le monde, comment ça va ?'], 'fr', 'en'); here = { ...here, ok: true, output: o, ms: Math.round(performance.now() - t0) }; }
      catch (e) { here = { ...here, error: (e as Error).message }; }
    }
  }
  // 2) The background engine host (where Discord and pages are translated).
  const bg = await send<{ chrome: CheckRow; bergamot: CheckRow; detector: string; userAgent: string }>({ type: 'bb:engine-check', from: 'fr', to: 'en' });
  out.innerHTML = row('Chrome built-in (this page)', here) + row('Chrome built-in (background)', bg.chrome) + row('Offline engine (Bergamot)', bg.bergamot)
    + `<div><b>Chrome language detector</b><span>${bg.detector}</span></div><div><b>Browser</b><span class="muted">${bg.userAgent.replace(/^Mozilla\/5\.0 /, '')}</span></div>`;
  $('engine-check-note').textContent = bg.chrome.error && !bg.bergamot.ok ? 'Tip: download the French offline pack below, or pick "Offline Bergamot only".' : '';
}

async function init(): Promise<void> {
  settings = await loadSettings();
  bindGeneral();
  renderLists();
  document.addEventListener('click', (e) => {
    const t = e.target as HTMLElement;
    if (t.dataset.dl) void download(t.dataset.dl);
    if (t.dataset.rm) { const [f, to] = t.dataset.rm.split('-') as [string, string]; void send({ type: 'bb:packs:remove', from: f, to }).then(refreshPacks); }
    if (t.dataset.offline) { const c = t.dataset.offline; void Promise.all([`${c}-en`, `en-${c}`].filter((p) => packs.some((x) => x.pair === p && !x.installed)).map(download)); if (chromeAvail.get(c) === 'downloadable') void downloadChrome(c); }
    if (t.dataset.chrome) void downloadChrome(t.dataset.chrome);
    if (t.dataset.rule) { const [kind, k] = t.dataset.rule.split('|') as ['servers' | 'channels', string]; void save((s) => { const d = structuredClone(s.discord); delete d[kind][k]; return { ...s, discord: d }; }).then(renderLists); }
    if (t.dataset.site) { const [kind, o] = t.dataset.site.split('|') as ['always' | 'never', string]; void save((s) => ({ ...s, sites: { ...s.sites, [kind]: s.sites[kind].filter((x) => x !== o) } })).then(renderLists); }
  });
  $('pack-search').addEventListener('input', renderPacks);
  $('engine-check').onclick = () => void engineCheck();
  chrome.runtime.onMessage.addListener((m: PackProgressEvent) => {
    if (m.type !== 'bb:packs:progress') return;
    const pair = `${m.from}-${m.to}`;
    if (m.done) return;
    // A pivot download reports both models together; show it on whichever rows exist.
    for (const p of [pair, `${m.from}-en`, `en-${m.to}`]) if (progress.has(p)) progress.set(p, m.total ? m.loaded / m.total : 0);
    renderPacks();
  });
  await refreshPacks();
  void refreshChrome();
  const q = new URLSearchParams(location.search);
  if (q.get('from') && q.get('to')) {
    const code = q.get('from') === 'en' ? q.get('to')! : q.get('from')!;
    $<HTMLInputElement>('pack-search').value = languageName(code);
    renderPacks();
    $('pack-focus').innerHTML = `<div class="card focus"><b>${languageLabel(q.get('from')!)} → ${languageLabel(q.get('to')!)}</b> is needed. Use “Download” in the row below (Chrome built-in needs this click), or “Make fully offline”.</div>`;
  }
  if (location.hash) document.querySelector(location.hash)?.scrollIntoView();
  chrome.storage.onChanged.addListener(() => void loadSettings().then((s) => { settings = s; renderLists(); }));
}

void init();
