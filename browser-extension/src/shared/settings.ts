export type EnginePreference = 'auto' | 'chrome' | 'bergamot';
export type ChannelMode = 'always' | 'never' | 'ask';

export interface Settings {
  targetLang: string;
  engine: EnginePreference;
  /** Download Bergamot packs without asking when a new language shows up (off = ask first). */
  autoDownloadPacks: boolean;
  minConfidence: number;
  discord: {
    enabled: boolean;
    defaultMode: ChannelMode;
    marker: boolean;
    hoverOriginal: boolean;
    altClickRestore: boolean;
    replyHotkey: string;
    backTranslationPreview: boolean;
    servers: Record<string, ChannelMode>;
    channels: Record<string, ChannelMode>;
  };
  sites: { always: string[]; never: string[] };
  recentLangs: string[];
  polish: { enabled: boolean; endpoint: string; model: string };
  clipboardMode: boolean;
  cacheMaxEntries: number;
  /** Developer/test override for where Bergamot packs are fetched from. Hashes are still verified. */
  packBaseUrl: string;
}

export const DEFAULT_SETTINGS: Settings = {
  targetLang: 'en',
  engine: 'auto',
  autoDownloadPacks: false,
  minConfidence: 0.5,
  discord: {
    enabled: true,
    defaultMode: 'always',
    marker: true,
    hoverOriginal: true,
    altClickRestore: true,
    replyHotkey: 'Ctrl+Shift+Y',
    backTranslationPreview: true,
    servers: {},
    channels: {},
  },
  sites: { always: [], never: [] },
  recentLangs: ['fr', 'es', 'ja'],
  polish: { enabled: false, endpoint: 'http://localhost:1234/v1', model: '' },
  clipboardMode: false,
  cacheMaxEntries: 20000,
  packBaseUrl: '',
};

type Plain = Record<string, unknown>;
const isPlain = (v: unknown): v is Plain => typeof v === 'object' && v !== null && !Array.isArray(v);

/** Deep-merges stored (possibly partial / older) settings over the defaults. */
export function mergeSettings<T>(base: T, patch: unknown): T {
  if (!isPlain(base) || !isPlain(patch)) return (patch === undefined ? base : (patch as T));
  const out: Plain = { ...base };
  for (const [k, v] of Object.entries(patch)) {
    const b = (base as Plain)[k];
    // Free-form maps (servers/channels) and leaves are taken as-is; known objects are merged.
    out[k] = isPlain(b) && isPlain(v) && Object.keys(b).length > 0 ? mergeSettings(b, v) : v;
  }
  return out as T;
}

const KEY = 'settings';

export async function loadSettings(): Promise<Settings> {
  const raw = await chrome.storage.local.get(KEY);
  return mergeSettings(DEFAULT_SETTINGS, raw[KEY]);
}

export async function saveSettings(patch: Partial<Settings> | ((s: Settings) => Settings)): Promise<Settings> {
  const current = await loadSettings();
  const next = typeof patch === 'function' ? patch(current) : mergeSettings(current, patch);
  await chrome.storage.local.set({ [KEY]: next });
  return next;
}

export function onSettingsChanged(cb: (s: Settings) => void): void {
  chrome.storage.onChanged.addListener((changes, area) => {
    if (area === 'local' && changes[KEY]) cb(mergeSettings(DEFAULT_SETTINGS, changes[KEY].newValue));
  });
}

/** Most-recent-first list, de-duplicated, capped. */
export function pushRecent(list: string[], code: string, max = 6): string[] {
  return [code, ...list.filter((c) => c !== code)].slice(0, max);
}

export function channelMode(s: Settings, guildId: string, channelId: string): ChannelMode {
  return s.discord.channels[`${guildId}/${channelId}`] ?? s.discord.servers[guildId] ?? s.discord.defaultMode;
}
