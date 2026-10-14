// Message protocol between content scripts / extension pages, the service worker and the offscreen engine host.

export type Purpose = 'read' | 'reply' | 'quick' | 'page';

export interface TranslateItem {
  id: string;
  text: string;
  /** Known source language, or 'auto' to detect. */
  from?: string;
  /** Language to assume when detection is unsure (e.g. the channel's main language). */
  hint?: string;
  /** Detect the language on this (longer) text instead, e.g. the whole message for one segment of it. */
  detectText?: string;
  /** Internal: set by the router when retrying with the next engine. */
  __retried?: boolean;
}

export type ItemStatus = 'ok' | 'skipped' | 'needs-pack' | 'error';

export interface PackRef {
  from: string;
  to: string;
  engine: 'chrome' | 'bergamot';
  sizeBytes?: number;
}

export interface TranslateResult {
  id: string;
  status: ItemStatus;
  from: string;
  confidence: number;
  translation?: string;
  engine?: string;
  cached?: boolean;
  pack?: PackRef;
  reason?: string;
  /** Set when an engine failed and the router fell back to another one. */
  failedEngine?: string;
  failedReason?: string;
}

export interface PackInfo {
  pair: string;
  from: string;
  to: string;
  engine: 'bergamot' | 'chrome';
  installed: boolean;
  sizeBytes: number;
  installedBytes?: number;
  chromeAvailability?: string;
}

export interface EngineStats {
  engineCalls: number;
  engineTexts: number;
  cacheHits: number;
  memoryCacheHits: number;
  detectCalls: number;
  workers: { pair: string; wasmBytes: number }[];
  lastEngine?: string;
  failures?: { engine: string; pair: string; reason: string }[];
}

export type Request =
  | { type: 'bb:translate'; items: TranslateItem[]; to: string; priority?: number; purpose?: Purpose; tabKey?: string }
  | { type: 'bb:cancel'; ids: string[]; tabKey?: string }
  | { type: 'bb:detect'; text: string }
  | { type: 'bb:availability'; from: string; to: string }
  | { type: 'bb:packs:list' }
  | { type: 'bb:packs:download'; from: string; to: string }
  | { type: 'bb:packs:remove'; from: string; to: string }
  | { type: 'bb:packs:chrome-installed'; from: string; to: string }
  | { type: 'bb:cache:clear' }
  | { type: 'bb:stats' }
  | { type: 'bb:stats:reset' }
  | { type: 'bb:polish'; text: string; from: string; to: string; original?: string; test?: { endpoint: string; model: string } }
  | { type: 'bb:clipboard:translate' }
  | { type: 'bb:page:toggle'; tabId?: number }
  | { type: 'bb:page:always'; origin: string; tabId?: number }
  | { type: 'bb:page:never'; origin: string }
  | { type: 'bb:open-packs'; from?: string; to?: string }
  | { type: 'bb:engine-check'; from: string; to: string }
  | { type: 'bb:ping' };

export type Envelope = Request & { target?: 'bg' | 'offscreen' };

/** Broadcast from the offscreen host while a pack downloads. */
export interface PackProgressEvent {
  type: 'bb:packs:progress';
  from: string;
  to: string;
  loaded: number;
  total: number;
  done?: boolean;
  error?: string;
}

/** Messages the service worker sends to content scripts. */
export type TabMessage =
  | { type: 'bb:tab:page-toggle' }
  | { type: 'bb:tab:page-status' }
  | { type: 'bb:tab:reply' }
  | { type: 'bb:tab:pack-progress'; event: PackProgressEvent };

export async function send<T = unknown>(req: Request): Promise<T> {
  const res = (await chrome.runtime.sendMessage({ ...req, target: 'bg' })) as T | { __error: string } | undefined;
  if (res && typeof res === 'object' && '__error' in (res as object)) throw new Error((res as { __error: string }).__error);
  return res as T;
}
